using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Chernika.Infrastructure.Services;

public class HKCardService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly TaskService _tasks;
    private readonly HKCardItemService _itemService;
    private readonly UserManager<ApplicationUser> _userManager;

    public HKCardService(
        AppDbContext db,
        AuditService audit,
        TaskService tasks,
        HKCardItemService itemService,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _audit = audit;
        _tasks = tasks;
        _itemService = itemService;
        _userManager = userManager;
    }

    public Task<PagedResult<HKCard>> GetPagedAsync(int page = 1, int pageSize = 50, HKCardStatus? status = null, Guid? branchId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.HKCards
            .Include(x => x.Branch)
            .Include(x => x.Node)
            .Include(x => x.Items).ThenInclude(i => i.AssemblyUnit)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        if (branchId.HasValue)
            query = query.Where(x => x.BranchId == branchId.Value);

        return GetPagedInternalAsync(query, page, pageSize);
    }

    public IQueryable<HKCard> GetFilteredQuery(HKCardStatus? status = null, Guid? branchId = null)
    {
        var query = _db.HKCards
            .Include(x => x.Branch)
            .Include(x => x.Node)
            .Include(x => x.Items).ThenInclude(i => i.AssemblyUnit)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        if (branchId.HasValue)
            query = query.Where(x => x.BranchId == branchId.Value);

        return query.OrderByDescending(x => x.CreatedAt);
    }

    public async Task<List<HKCard>> GetAllAsync() =>
        await _db.HKCards
            .Include(x => x.Branch)
            .Include(x => x.Node)
            .Include(x => x.Items).ThenInclude(i => i.AssemblyUnit)
            .ToListAsync();

    public Task<HKCard?> GetByIdAsync(Guid id)
    {
        return _db.HKCards
            .Include(x => x.Branch)
            .Include(x => x.Node)
            .Include(x => x.Items.OrderBy(i => i.SortOrder)).ThenInclude(i => i.AssemblyUnit)
            .Include(x => x.Items).ThenInclude(i => i.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(x => x.StatusLog.OrderByDescending(s => s.ChangedAt))
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    private static readonly Regex VersionRegex = new(@"^v(0[1-9]|1[0-2])(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"^ХК-[A-Za-zА-Яа-я0-9]+-\d{4}(-\d+)?$", RegexOptions.Compiled);

    public static bool IsValidVersion(string? version) =>
        !string.IsNullOrEmpty(version) && VersionRegex.IsMatch(version);

    private static string GenerateVersion() =>
        "v" + DateTime.UtcNow.ToString("MMyy");

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && CodeRegex.IsMatch(code);

    public async Task<string> GenerateCodeAsync(Guid nodeId)
    {
        var node = await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId)
            ?? throw new ArgumentException("Изделие не найдено", nameof(nodeId));

        var year = DateTime.UtcNow.Year.ToString();
        var baseCode = $"ХК-{node.Code}-{year}";

        var existing = await _db.HKCards
            .IgnoreQueryFilters()
            .Where(c => c.Code == baseCode || c.Code!.StartsWith(baseCode + "-"))
            .Select(c => c.Code)
            .ToListAsync();

        if (!existing.Any())
            return baseCode;

        var maxSuffix = existing
            .Select(c =>
            {
                var parts = c!.Split('-');
                return parts.Length >= 4 && int.TryParse(parts[^1], out var n) && c != baseCode ? n : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        return maxSuffix == 0 ? $"{baseCode}-2" : $"{baseCode}-{maxSuffix + 1}";
    }

    public async Task<bool> HasActiveCardForNodeAsync(Guid nodeId) =>
        await _db.HKCards.AnyAsync(x =>
            x.NodeId == nodeId &&
            (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired));

    public async Task<HKCard?> GetActiveCardForNodeAsync(Guid nodeId) =>
        await _db.HKCards
            .Where(x =>
                x.NodeId == nodeId &&
                (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired))
            .FirstOrDefaultAsync();

    public async Task<HKCard> CreateAsync(HKCard card, Guid userId)
    {
        card.Id = Guid.NewGuid();
        card.Code = await GenerateCodeAsync(card.NodeId);
        card.Version = GenerateVersion();
        card.CreatedAt = DateTime.UtcNow;
        card.UpdatedAt = DateTime.UtcNow;
        card.Status = HKCardStatus.Draft;
        card.AuthorId = userId;

        if (card.EffectiveDate.HasValue && card.ExpirationDate.HasValue
            && card.ExpirationDate.Value < card.EffectiveDate.Value)
            throw new ArgumentException(
                "Дата окончания действия не может быть раньше даты начала действия.",
                nameof(card));

        var hasActiveDuplicate = await _db.HKCards.AnyAsync(x =>
            x.NodeId == card.NodeId &&
            (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired));
        if (hasActiveDuplicate)
            throw new InvalidOperationException(
                "Для выбранного изделия уже существует активная ХК " +
                "в статусе «Черновик», «На согласовании» или «На доработке». " +
                "Завершите или архивируйте существующую карточку перед созданием новой.");

        _db.HKCards.Add(card);
        await _db.SaveChangesAsync();

        await _audit.LogAsync("HKCard", card.Id.ToString(), "Created", userId);
        return card;
    }

    public async Task<HKCard> UpdateAsync(HKCard card, Guid userId)
    {
        if (!IsValidCode(card.Code))
            throw new ArgumentException("Код ХК имеет неверный формат.", nameof(card));

        var duplicate = await _db.HKCards
            .AnyAsync(x => x.Code == card.Code && x.Id != card.Id);
        if (duplicate)
            throw new ArgumentException("Карточка с таким кодом уже существует.");

        // Version is regenerated on each save so draft/revision edits are
        // always visually distinct.  It is fixed at the moment the card
        // transitions to OnReview — after that the version is stable.
        card.Version = GenerateVersion();

        var versionDuplicate = await _db.HKCards
            .AnyAsync(x => x.Code == card.Code && x.Version == card.Version && x.Id != card.Id);
        if (versionDuplicate)
            throw new ArgumentException(
                "Версия v" + DateTime.UtcNow.ToString("MMyy")
                + " уже существует для данного кода ХК. Попробуйте сохранить позднее.");

        if (card.EffectiveDate.HasValue && card.ExpirationDate.HasValue
            && card.ExpirationDate.Value < card.EffectiveDate.Value)
            throw new ArgumentException(
                "Дата окончания действия не может быть раньше даты начала действия.",
                nameof(card));

        var existingCard = await _db.HKCards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == card.Id);
        if (existingCard != null && existingCard.NodeId != card.NodeId
            && existingCard.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            throw new InvalidOperationException("Изменение изделия недоступно для карточки в текущем статусе.");

        var existingIds = await _db.HKCardItems
            .Where(i => i.HKCardId == card.Id)
            .Select(i => i.Id)
            .ToListAsync();

        var removedIds = existingIds.Except(card.Items.Select(i => i.Id)).ToList();
        if (removedIds.Count != 0)
        {
            var removedItems = await _db.HKCardItems
                .Where(i => removedIds.Contains(i.Id))
                .ToListAsync();
            _db.HKCardItems.RemoveRange(removedItems);
        }

        card.UpdatedAt = DateTime.UtcNow;
        _db.HKCards.Update(card);

        using var tx = await _db.Database.BeginTransactionAsync();

        await _db.SaveChangesAsync();

        foreach (var item in card.Items)
        {
            var materials = item.Materials
                .Select(m => (m.GsmMaterialId, m.Category));

            await _itemService.SaveMaterialsAsync(item.Id, materials);
        }

        await tx.CommitAsync();

        await _audit.LogAsync("HKCard", card.Id.ToString(), "Updated", userId);
        return card;
    }

    public async Task<(bool Success, string? Error)> ChangeStatusAsync(Guid id, HKCardStatus newStatus, Guid userId, string? comment = null)
    {
        var card = await _db.HKCards.FindAsync(id);
        if (card == null)
            return (false, "Карточка не найдена");

        var actor = await _userManager.FindByIdAsync(userId.ToString());
        var isSystemAdmin = actor != null && await _userManager.IsInRoleAsync(actor, "SystemAdmin");
        if (!isSystemAdmin && actor?.BranchId != card.BranchId)
            return (false, "Нет прав для изменения карточки другого филиала");

        var oldStatus = card.Status;
        if (!HKCardStatusTransitions.IsAllowed(oldStatus, newStatus))
            return (false, HKCardStatusTransitions.GetErrorMessage(oldStatus, newStatus));

        card.Status = newStatus;
        card.UpdatedAt = DateTime.UtcNow;

        if (newStatus == HKCardStatus.Approved)
        {
            card.ApprovedDate = DateTime.UtcNow;
            card.ReviewerId = userId;
            if (!card.EffectiveDate.HasValue)
                card.EffectiveDate = card.ApprovedDate;

            if (card.ExpirationDate.HasValue && card.ApprovedDate.Value > card.ExpirationDate.Value)
            {
                await _audit.LogAsync("HKCard", id.ToString(), "Approved",
                    userId, "Card approved after its own ExpirationDate");
            }
        }

        _db.HKCardStatusLogs.Add(new HKCardStatusLog
        {
            Id = Guid.NewGuid(),
            HKCardId = id,
            FromStatus = oldStatus,
            ToStatus = newStatus,
            ChangedByUserId = userId,
            Comment = comment,
            ChangedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        await _audit.LogAsync("HKCard", id.ToString(), $"Status:{newStatus}", userId, comment);
        await CreateTasksForStatusChangeAsync(card, oldStatus, newStatus, userId);

        if (newStatus == HKCardStatus.Approved)
            await ArchivePreviousApprovedVersionsAsync(card, userId);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(Guid id, Guid userId, UserRole actorRole)
    {
        var card = await _db.HKCards.FindAsync(id);
        if (card == null)
            return (false, "Карточка не найдена");

        var actor = await _userManager.FindByIdAsync(userId.ToString());
        bool isSysAdmin = actorRole == UserRole.SystemAdmin;
        if (!isSysAdmin && actor?.BranchId != card.BranchId)
            return (false, "Нет доступа к карточке другого филиала");

        if (!HKCardStatusTransitions.CanDelete(card.Status, actorRole))
            return (false,
                $"Нельзя удалить карточку со статусом «{card.Status}». " +
                "Утверждённые карточки можно только архивировать.");

        return await ChangeStatusAsync(id, HKCardStatus.Deleted, userId, null);
    }

    private async Task CreateTasksForStatusChangeAsync(HKCard card, HKCardStatus _, HKCardStatus to, Guid actorUserId)
    {
        switch (to)
        {
            case HKCardStatus.OnReview:
                    await CreateBranchRoleTaskAsync(
                        card,
                        "NormAdmin",
                        $"Проверка ХК {card.Code}",
                        $"Карточка {card.Code} (v{card.Version}) отправлена на проверку.",
                        actorUserId.ToString());
                break;

            case HKCardStatus.RevisionRequired:
                if (card.AuthorId.HasValue)
                {
                    await _tasks.CreateTaskAsync(
                        $"Доработка ХК {card.Code}",
                        card.AuthorId.Value.ToString(),
                        $"Карточка {card.Code} возвращена на доработку.",
                        "HKCard",
                        card.Id.ToString());
                }
                break;

            case HKCardStatus.Approved:
                await CreateRecalculationTasksAsync(card);
                break;

            case HKCardStatus.Archived:
                break;
        }
    }

    private async Task CreateRecalculationTasksAsync(HKCard card)
    {
        var instanceIds = await _db.IndividualCards
            .Where(c => _db.HKCards.Any(h => h.Id == c.HKCardId && h.Code == card.Code))
            .Select(c => c.EquipmentInstanceId)
            .Distinct()
            .ToListAsync();

        var modelIds = await (
            from n in _db.ProductCompositionNodes
            join p in _db.ProductCompositionParts on n.PartId equals p.Id
            join pc in _db.ProductCompositions on p.ProductCompositionId equals pc.Id
            where n.NodeId == card.NodeId
            select pc.EquipmentModelId).Distinct().ToListAsync();

        if (modelIds.Count != 0)
        {
            var fromModels = await _db.EquipmentInstances
                .Where(i => modelIds.Contains(i.EquipmentModelId))
                .Select(i => i.Id)
                .ToListAsync();
            instanceIds = instanceIds.Union(fromModels).Distinct().ToList();
        }

        if (instanceIds.Count == 0)
            return;

        var operators = await GetBranchUsersInRoleAsync(card.BranchId, "Operator");
        var assignee = operators.Count > 0
            ? operators[0]
            : await GetAnyUserInRoleAsync("Operator");
        if (assignee == null)
            return;

        foreach (var instanceId in instanceIds)
        {
            await _tasks.CreateTaskAsync(
                $"Пересчёт инд. карт — экземпляр",
                assignee,
                $"Утверждена новая версия ХК {card.Code} (v{card.Version}). Требуется подтверждение пересчёта индивидуальных карт.",
                "EquipmentInstance",
                instanceId.ToString(),
                DateTime.UtcNow.AddDays(14));
        }
    }

    private async Task ArchivePreviousApprovedVersionsAsync(HKCard card, Guid userId)
    {
        var previousApproved = await _db.HKCards
            .Where(h => h.Code == card.Code
                && h.Status == HKCardStatus.Approved
                && h.Id != card.Id)
            .ToListAsync();

        if (previousApproved.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var comment = "Автоматическая архивация при утверждении новой версии";

        foreach (var prev in previousApproved)
        {
            prev.Status = HKCardStatus.Archived;
            prev.UpdatedAt = now;

            _db.HKCardStatusLogs.Add(new HKCardStatusLog
            {
                Id = Guid.NewGuid(),
                HKCardId = prev.Id,
                FromStatus = HKCardStatus.Approved,
                ToStatus = HKCardStatus.Archived,
                ChangedByUserId = userId,
                Comment = comment,
                ChangedAt = now
            });

            await _audit.LogAsync("HKCard", prev.Id.ToString(),
                $"Status:{HKCardStatus.Archived}", userId, comment);
        }

        await _db.SaveChangesAsync();
    }

    private async Task CreateBranchRoleTaskAsync(HKCard card, string role, string title, string description, string fallbackAssignee)
    {
        var users = await GetBranchUsersInRoleAsync(card.BranchId, role);
        var assignee = users.Count > 0 ? users[0] : fallbackAssignee;
        await _tasks.CreateTaskAsync(title, assignee, description, "HKCard", card.Id.ToString(), DateTime.UtcNow.AddDays(7));
    }

    private async Task<List<string>> GetBranchUsersInRoleAsync(Guid branchId, string role)
    {
        var result = new List<string>();
        var users = await _userManager.Users.Where(u => u.IsActive && u.BranchId == branchId).ToListAsync();
        foreach (var user in users)
        {
            if (await _userManager.IsInRoleAsync(user, role))
                result.Add(user.Id);
        }
        return result;
    }

    private async Task<string?> GetAnyUserInRoleAsync(string role)
    {
        var users = await _userManager.GetUsersInRoleAsync(role);
        var user = users.FirstOrDefault(u => u.IsActive);
        return user?.Id;
    }

    private static async Task<PagedResult<HKCard>> GetPagedInternalAsync(IQueryable<HKCard> query, int page, int pageSize)
    {
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<HKCard>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }
}
