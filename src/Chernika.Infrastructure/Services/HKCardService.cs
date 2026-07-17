using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.RegularExpressions;

namespace Chernika.Infrastructure.Services;

public class HKCardService
{
    private readonly AppDbContext _db;
    private readonly TaskService _tasks;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<HKCardService> _logger;

    public HKCardService(
        AppDbContext db,
        TaskService tasks,
        UserManager<ApplicationUser> userManager,
        ICurrentUserService currentUser,
        ILogger<HKCardService> logger)
    {
        _db = db;
        _tasks = tasks;
        _userManager = userManager;
        _currentUser = currentUser;
        _logger = logger;
    }

    private async Task<Guid?> GetAccessibleBranchIdAsync(Guid? requestedBranchId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor is null)
            throw new UnauthorizedAccessException("Пользователь не найден.");

        var roles = await _userManager.GetRolesAsync(actor);
        if (roles.Contains("SystemAdmin"))
            return requestedBranchId;

        if (actor.BranchId is null || actor.BranchId == Guid.Empty)
            throw new UnauthorizedAccessException("У пользователя не указан филиал.");

        if (requestedBranchId.HasValue && requestedBranchId != actor.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к данным другого филиала.");

        return actor.BranchId;
    }

    public async Task<List<HKCard>> GetFilteredForExportAsync(
        HKCardStatus? status = null, Guid? branchId = null, CancellationToken ct = default)
    {
        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);
        var query = BuildFilteredQuery(status, safeBranchId);
        return await query.Take(10000).ToListAsync(ct);
    }

    public async Task<List<HKCard>> GetAllAsync(CancellationToken ct = default)
    {
        var safeBranchId = await GetAccessibleBranchIdAsync(null, ct);
        var query = _db.HKCards.AsNoTracking()
            .Include(x => x.Branch)
            .Include(x => x.Node)
            .AsQueryable();
        if (safeBranchId.HasValue)
            query = query.Where(x => x.BranchId == safeBranchId.Value);
        return await query.ToListAsync(ct);
    }

    public async Task<PagedResult<HKCardListItemDto>> GetPagedAsync(
        int page = 1, int pageSize = 50, HKCardStatus? status = null, Guid? branchId = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);

        var query = _db.HKCards.AsNoTracking().AsQueryable();

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        if (safeBranchId.HasValue)
            query = query.Where(x => x.BranchId == safeBranchId.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new HKCardListItemDto
            {
                Id = x.Id,
                Code = x.Code,
                Version = x.Version,
                Status = x.Status,
                BranchId = x.BranchId,
                BranchName = x.Branch.Name,
                NodeCode = x.Node.Code,
                NodeName = x.Node.Name,
                CreatedAt = x.CreatedAt,
                ApprovedDate = x.ApprovedDate
            })
            .ToListAsync(ct);

        return new PagedResult<HKCardListItemDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<HKCardListItemDto>> GetFilteredAsync(
        string? code = null,
        HKCardStatus? status = null,
        string? version = null,
        string? nodeSearch = null,
        Guid? branchId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);

        var query = _db.HKCards.AsNoTracking().AsQueryable();

        if (safeBranchId.HasValue)
            query = query.Where(x => x.BranchId == safeBranchId.Value);
        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(code))
            query = query.Where(x => EF.Functions.ILike(x.Code, $"%{code.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(version))
            query = query.Where(x => EF.Functions.ILike(x.Version, $"%{version.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(nodeSearch))
        {
            var term = $"%{nodeSearch.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.Node.Name, term) ||
                EF.Functions.ILike(x.Node.Code, term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new HKCardListItemDto
            {
                Id = x.Id,
                Code = x.Code,
                Version = x.Version,
                Status = x.Status,
                BranchId = x.BranchId,
                BranchName = x.Branch.Name,
                NodeCode = x.Node.Code,
                NodeName = x.Node.Name,
                CreatedAt = x.CreatedAt,
                ApprovedDate = x.ApprovedDate
            })
            .ToListAsync(ct);

        return new PagedResult<HKCardListItemDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<HKCardListItemDto>> GetTaskCardsAsync(
        HKCardStatus status, Guid? branchId = null, CancellationToken ct = default)
    {
        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);

        var query = _db.HKCards.AsNoTracking()
            .Where(x => x.Status == status)
            .AsQueryable();

        if (safeBranchId.HasValue)
            query = query.Where(x => x.BranchId == safeBranchId.Value);

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(3)
            .Select(x => new HKCardListItemDto
            {
                Id = x.Id,
                Code = x.Code,
                Version = x.Version,
                Status = x.Status,
                BranchId = x.BranchId,
                BranchName = x.Branch.Name,
                NodeCode = x.Node.Code,
                NodeName = x.Node.Name,
                CreatedAt = x.CreatedAt,
                ApprovedDate = x.ApprovedDate
            })
            .ToListAsync(ct);
    }

    public async Task<Dictionary<HKCardStatus, int>> GetStatusCountsAsync(Guid? branchId = null, CancellationToken ct = default)
    {
        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);

        var query = _db.HKCards.AsNoTracking().AsQueryable();
        if (safeBranchId.HasValue)
            query = query.Where(x => x.BranchId == safeBranchId.Value);

        return await query
            .GroupBy(x => x.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<HKCard?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.HKCards
            .AsSplitQuery()
            .Include(x => x.Branch)
            .Include(x => x.Node)
            .Include(x => x.Items.OrderBy(i => i.SortOrder)).ThenInclude(i => i.AssemblyUnit)
            .Include(x => x.Items).ThenInclude(i => i.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(x => x.StatusLog.OrderByDescending(s => s.ChangedAt))
            .FirstOrDefaultAsync(x => x.Id == id, ct);
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
            ?? throw new ArgumentException("Узел не найден");

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
        await _db.HKCards.AsNoTracking()
            .Where(x =>
                x.NodeId == nodeId &&
                (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired))
            .FirstOrDefaultAsync();

    public async Task<(bool Success, string? Error)> ValidateCardItemsAsync(ICollection<HKCardItem> items)
    {
        if (items == null || items.Count == 0)
            return (false, "ХК должна содержать хотя бы одну строку.");

        var assemblyUnitIds = items.Select(i => i.AssemblyUnitId).Distinct().ToHashSet();
        var existingAus = await _db.AssemblyUnits
            .IgnoreQueryFilters()
            .Where(a => assemblyUnitIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync();
        var missingAus = assemblyUnitIds.Except(existingAus).ToList();
        if (missingAus.Any())
            return (false, $"Сборочная единица с идентификатором {missingAus[0]} не найдена.");

        var allMaterials = items.SelectMany(i => i.Materials).ToList();
        if (allMaterials.Any(m => !Enum.IsDefined(typeof(GsmCategory), m.Category)))
            return (false, "Некорректная категория материала.");

        var gsmMaterialIds = allMaterials.Select(m => m.GsmMaterialId).Distinct().ToHashSet();
        var existingMaterials = await _db.GsmMaterials
            .IgnoreQueryFilters()
            .Where(m => gsmMaterialIds.Contains(m.Id))
            .ToListAsync();
        var existingMaterialIds = existingMaterials.Select(m => m.Id).ToHashSet();
        var missingMats = gsmMaterialIds.Except(existingMaterialIds).ToList();
        if (missingMats.Any())
            return (false, $"Марка ГСМ с идентификатором {missingMats[0]} не найдена.");

        foreach (var grp in allMaterials.GroupBy(m => new { m.HKCardItemId, m.Category }))
        {
            var dupes = grp.GroupBy(m => m.GsmMaterialId).FirstOrDefault(g => g.Count() > 1);
            if (dupes != null)
                return (false, "Обнаружены дублирующиеся марки ГСМ в одной строке и категории.");
        }

        foreach (var item in items)
        {
            if (item.AssemblyUnitId == Guid.Empty)
                return (false, "Укажите сборочную единицу во всех строках таблицы.");
            if (item.Quantity <= 0)
                return (false, "Количество изделий должно быть больше нуля.");
            if (item.Volume < 0)
                return (false, "Масса/объём не может быть отрицательным.");
        }

        return (true, null);
    }

    public async Task<HKCard> CreateAsync(HKCard card, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null || !await _userManager.IsInRoleAsync(actor, "Operator"))
            throw new UnauthorizedAccessException("Недостаточно прав для создания ХК.");

        if (card.NodeId == Guid.Empty)
            throw new ArgumentException("Необходимо выбрать узел.");

        if (actor.BranchId == null || actor.BranchId.Value == Guid.Empty)
            throw new InvalidOperationException("У пользователя не указан филиал. Создание ХК невозможно.");

        var validation = await ValidateCardItemsAsync(card.Items);
        if (!validation.Success)
            throw new ArgumentException(validation.Error);

        if (card.EffectiveDate.HasValue && card.ExpirationDate.HasValue
            && card.ExpirationDate.Value < card.EffectiveDate.Value)
            throw new ArgumentException(
                "Дата окончания действия не может быть раньше даты начала действия.");

        card.Id = Guid.NewGuid();
        card.Code = await GenerateCodeAsync(card.NodeId);
        card.Version = GenerateVersion();
        card.CreatedAt = DateTime.UtcNow;
        card.UpdatedAt = DateTime.UtcNow;
        card.Status = HKCardStatus.Draft;
        card.AuthorId = actorId;
        card.BranchId = actor.BranchId.Value;

        foreach (var item in card.Items)
        {
            item.Id = Guid.NewGuid();
            item.HKCardId = card.Id;
            foreach (var mat in item.Materials)
            {
                mat.Id = Guid.NewGuid();
                mat.HKCardItemId = item.Id;
            }
        }

        var hasActiveDuplicate = await _db.HKCards.AnyAsync(x =>
            x.NodeId == card.NodeId &&
            (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired));
        if (hasActiveDuplicate)
            throw new InvalidOperationException(
                "Для выбранного узла уже существует активная ХК " +
                "в статусе «Черновик», «На согласовании» или «На доработке». " +
                "Завершите или архивируйте существующую карточку перед созданием новой.");

        _db.HKCards.Add(card);
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCard",
            EntityId = card.Id.ToString(),
            Action = "Created",
            UserId = actorId,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "UX_HKCards_OneActivePerNode"
            })
        {
            throw new InvalidOperationException(
                "Для выбранного узла уже существует активная ХК. " +
                "Завершите, архивируйте или откройте существующую карточку.");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to create HK card (NodeId={NodeId})", card.NodeId);
            throw new InvalidOperationException("Не удалось сохранить ХК. Проверьте заполнение всех полей и повторите попытку.");
        }

        return card;
    }

    public async Task<HKCard> UpdateAsync(HKCard card, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null)
            throw new UnauthorizedAccessException("Пользователь не найден.");
        var roles = await _userManager.GetRolesAsync(actor);
        if (!roles.Contains("Operator"))
            throw new UnauthorizedAccessException("Недостаточно прав для редактирования ХК.");

        if (card.NodeId == Guid.Empty)
            throw new ArgumentException("Необходимо выбрать узел.");

        if (card.RowVersion == 0)
            throw new InvalidOperationException("Версия карточки не указана. Обновите страницу и повторите попытку.");

        if (card.EffectiveDate.HasValue && card.ExpirationDate.HasValue
            && card.ExpirationDate.Value < card.EffectiveDate.Value)
            throw new ArgumentException(
                "Дата окончания действия не может быть раньше даты начала действия.");

        var validation = await ValidateCardItemsAsync(card.Items);
        if (!validation.Success)
            throw new ArgumentException(validation.Error);

        var existing = await _db.HKCards
            .Include(x => x.Items).ThenInclude(i => i.Materials)
            .FirstOrDefaultAsync(x => x.Id == card.Id, ct)
            ?? throw new ArgumentException("ХК не найдена.");

        if (actor.BranchId != existing.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к карточке другого филиала.");

        if (existing.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            throw new InvalidOperationException("Редактирование недоступно для карточки в текущем статусе.");

        if (card.RowVersion != existing.RowVersion)
            throw new InvalidOperationException("Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");

        existing.Purpose = card.Purpose;
        existing.NormativeBasis = card.NormativeBasis;
        existing.Notes = card.Notes;
        existing.EffectiveDate = card.EffectiveDate;
        existing.ExpirationDate = card.ExpirationDate;
        existing.UpdatedAt = DateTime.UtcNow;

        if (existing.NodeId != card.NodeId)
        {
            if (existing.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
                throw new InvalidOperationException("Изменение узла недоступно для карточки в текущем статусе.");

            var hasActiveOnNewNode = await _db.HKCards.AnyAsync(x =>
                x.Id != existing.Id &&
                x.NodeId == card.NodeId &&
                (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired));
            if (hasActiveOnNewNode)
                throw new InvalidOperationException(
                    "Для выбранного узла уже существует активная ХК. " +
                    "Завершите или архивируйте существующую карточку перед сменой узла.");

            existing.NodeId = card.NodeId;
            existing.Code = await GenerateCodeAsync(card.NodeId);
        }

        var incomingItems = card.Items.OrderBy(i => i.SortOrder).ToList();
        var incomingItemIds = incomingItems.Select(i => i.Id).ToHashSet();

        var removedItems = existing.Items.Where(i => !incomingItemIds.Contains(i.Id)).ToList();
        foreach (var item in removedItems)
            _db.HKCardItems.Remove(item);

        foreach (var incomingItem in incomingItems)
        {
            if (incomingItem.Id != Guid.Empty
                && incomingItem.HKCardId != Guid.Empty
                && incomingItem.HKCardId != existing.Id)
            {
                throw new InvalidOperationException("Обнаружена строка, не принадлежащая данной ХК.");
            }

            var existingItem = existing.Items.FirstOrDefault(i => i.Id == incomingItem.Id);
            if (existingItem != null)
            {
                existingItem.AssemblyUnitId = incomingItem.AssemblyUnitId;
                existingItem.Quantity = incomingItem.Quantity;
                existingItem.Volume = incomingItem.Volume;
                existingItem.UnitOfMeasure = incomingItem.UnitOfMeasure;
                existingItem.Periodicity = incomingItem.Periodicity;
                existingItem.Notes = incomingItem.Notes;
                existingItem.SortOrder = incomingItem.SortOrder;

                _db.HKCardItemMaterials.RemoveRange(existingItem.Materials);
                foreach (var mat in incomingItem.Materials)
                {
                    existingItem.Materials.Add(new HKCardItemMaterial
                    {
                        Id = Guid.NewGuid(),
                        HKCardItemId = existingItem.Id,
                        GsmMaterialId = mat.GsmMaterialId,
                        Category = mat.Category
                    });
                }
            }
            else
            {
                incomingItem.Id = Guid.NewGuid();
                incomingItem.HKCardId = existing.Id;
                incomingItem.Materials = incomingItem.Materials.Select(m => new HKCardItemMaterial
                {
                    Id = Guid.NewGuid(),
                    HKCardItemId = incomingItem.Id,
                    GsmMaterialId = m.GsmMaterialId,
                    Category = m.Category
                }).ToList();
                _db.HKCardItems.Add(incomingItem);
            }
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCard",
            EntityId = card.Id.ToString(),
            Action = "Updated",
            UserId = actorId,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
        }

        return existing;
    }

    public async Task<(bool Success, string? Error)> ChangeStatusAsync(
        Guid id, HKCardStatus newStatus, string? comment = null, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null)
            return (false, "Пользователь не найден");
        var roles = await _userManager.GetRolesAsync(actor);

        var card = await _db.HKCards.FindAsync(id, ct);
        if (card == null)
            return (false, "Карточка не найдена");

        if (actor.BranchId != card.BranchId && !roles.Contains("SystemAdmin"))
            return (false, "Нет прав для изменения карточки другого филиала");

        var roleError = ValidateStatusChangeRole(card.Status, newStatus, roles);
        if (roleError != null)
            return (false, roleError);

        var oldStatus = card.Status;
        if (!HKCardStatusTransitions.IsAllowed(oldStatus, newStatus))
            return (false, HKCardStatusTransitions.GetErrorMessage(oldStatus, newStatus));

        card.Status = newStatus;
        card.UpdatedAt = DateTime.UtcNow;

        if (newStatus == HKCardStatus.Approved)
        {
            card.ApprovedDate = DateTime.UtcNow;
            card.ReviewerId = actorId;
            if (!card.EffectiveDate.HasValue)
                card.EffectiveDate = card.ApprovedDate;
        }

        _db.HKCardStatusLogs.Add(new HKCardStatusLog
        {
            Id = Guid.NewGuid(),
            HKCardId = id,
            FromStatus = oldStatus,
            ToStatus = newStatus,
            ChangedByUserId = actorId,
            Comment = comment,
            ChangedAt = DateTime.UtcNow
        });

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCard",
            EntityId = id.ToString(),
            Action = $"Status:{newStatus}",
            UserId = actorId,
            CreatedAt = DateTime.UtcNow,
            Details = comment
        });

        if (newStatus == HKCardStatus.Approved
            && card.ExpirationDate.HasValue
            && card.ApprovedDate.HasValue
            && card.ApprovedDate.Value > card.ExpirationDate.Value)
        {
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                EntityType = "HKCard",
                EntityId = id.ToString(),
                Action = "ApprovedAfterExpiration",
                UserId = actorId,
                CreatedAt = DateTime.UtcNow,
                Details = "Card approved after its own ExpirationDate"
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
        }

        await CreateTasksForStatusChangeAsync(card, oldStatus, newStatus, actorId);

        if (newStatus == HKCardStatus.Approved)
            await ArchivePreviousApprovedVersionsAsync(card, actorId, ct);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null)
            return (false, "Пользователь не найден");
        var roles = await _userManager.GetRolesAsync(actor);

        var card = await _db.HKCards.FindAsync(id, ct);
        if (card == null)
            return (false, "Карточка не найдена");

        bool isSysAdmin = roles.Contains("SystemAdmin");
        if (!isSysAdmin && actor.BranchId != card.BranchId)
            return (false, "Нет доступа к карточке другого филиала");

        var actorRole = ResolveUserRole(roles);
        if (!HKCardStatusTransitions.CanDelete(card.Status, actorRole))
            return (false,
                $"Нельзя удалить карточку со статусом «{card.Status}». " +
                "Утверждённые карточки можно только архивировать.");

        return await ChangeStatusAsync(id, HKCardStatus.Deleted, null, ct);
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

        var tasks = instanceIds.Select(instanceId => new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = $"Пересчёт инд. карт — экземпляр",
            AssigneeId = assignee,
            Description = $"Утверждена новая версия ХК {card.Code} (v{card.Version}). Требуется подтверждение пересчёта индивидуальных карт.",
            EntityType = "EquipmentInstance",
            EntityId = instanceId.ToString(),
            DueDate = DateTime.UtcNow.AddDays(14),
            CreatedAt = DateTime.UtcNow
        }).ToList();

        await _tasks.CreateTasksAsync(tasks);
    }

    private async Task ArchivePreviousApprovedVersionsAsync(HKCard card, Guid actorUserId, CancellationToken ct = default)
    {
        var previousApproved = await _db.HKCards
            .Where(h => h.Code == card.Code
                && h.Status == HKCardStatus.Approved
                && h.Id != card.Id)
            .ToListAsync(ct);

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
                ChangedByUserId = actorUserId,
                Comment = comment,
                ChangedAt = now
            });

            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                EntityType = "HKCard",
                EntityId = prev.Id.ToString(),
                Action = $"Status:{HKCardStatus.Archived}",
                UserId = actorUserId,
                CreatedAt = now,
                Details = comment
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Concurrency conflict during archival is non-fatal
        }
    }

    private async Task CreateBranchRoleTaskAsync(HKCard card, string role, string title, string description, string fallbackAssignee)
    {
        var users = await GetBranchUsersInRoleAsync(card.BranchId, role);
        var assignee = users.Count > 0 ? users[0] : fallbackAssignee;
        await _tasks.CreateTaskAsync(title, assignee, description, "HKCard", card.Id.ToString(), DateTime.UtcNow.AddDays(7));
    }

    private async Task<List<string>> GetBranchUsersInRoleAsync(Guid branchId, string role)
    {
        var roleEntity = await _db.Roles.FirstOrDefaultAsync(r => r.Name == role);
        if (roleEntity == null) return new();

        return await _userManager.Users
            .Where(u => u.IsActive && u.BranchId == branchId
                && _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == roleEntity.Id))
            .Select(u => u.Id)
            .ToListAsync();
    }

    private async Task<string?> GetAnyUserInRoleAsync(string role)
    {
        var users = await _userManager.GetUsersInRoleAsync(role);
        var user = users.FirstOrDefault(u => u.IsActive);
        return user?.Id;
    }

    private IQueryable<HKCard> BuildFilteredQuery(HKCardStatus? status = null, Guid? branchId = null)
    {
        var query = _db.HKCards.AsNoTracking()
            .Include(x => x.Branch)
            .Include(x => x.Node)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        if (branchId.HasValue)
            query = query.Where(x => x.BranchId == branchId.Value);

        return query.OrderByDescending(x => x.CreatedAt);
    }

    private static string? ValidateStatusChangeRole(HKCardStatus oldStatus, HKCardStatus newStatus, IList<string> roles)
    {
        var isOperator = roles.Contains("Operator");
        var isNormAdmin = roles.Contains("NormAdmin");
        var isSysAdmin = roles.Contains("SystemAdmin");

        return newStatus switch
        {
            HKCardStatus.OnReview when !isOperator && !isSysAdmin =>
                "Недостаточно прав для отправки ХК на проверку.",
            HKCardStatus.Approved when !isNormAdmin && !isSysAdmin =>
                "Недостаточно прав для утверждения ХК.",
            HKCardStatus.RevisionRequired when !isNormAdmin && !isSysAdmin =>
                "Недостаточно прав для возврата ХК на доработку.",
            HKCardStatus.Archived when !isNormAdmin && !isSysAdmin =>
                "Недостаточно прав для архивации ХК.",
            HKCardStatus.Deleted when !HKCardStatusTransitions.CanDelete(oldStatus, ResolveUserRole(roles)) =>
                "Недостаточно прав для удаления ХК в текущем статусе.",
            _ => null
        };
    }

    private static UserRole ResolveUserRole(IList<string> roles)
    {
        if (roles.Contains("SystemAdmin")) return UserRole.SystemAdmin;
        if (roles.Contains("NormAdmin")) return UserRole.NormAdmin;
        if (roles.Contains("DepartmentHead")) return UserRole.DepartmentHead;
        return UserRole.Operator;
    }
}
