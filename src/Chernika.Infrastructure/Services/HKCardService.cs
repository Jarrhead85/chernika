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
    private readonly IPermissionService _permissions;
    private readonly HKCardValidationService _hkValidation;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<HKCardService> _logger;

    public HKCardService(
        AppDbContext db,
        TaskService tasks,
        UserManager<ApplicationUser> userManager,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        HKCardValidationService hkValidation,
        AuditService audit,
        TimeProvider time,
        ILogger<HKCardService> logger)
    {
        _db = db;
        _tasks = tasks;
        _userManager = userManager;
        _currentUser = currentUser;
        _permissions = permissions;
        _hkValidation = hkValidation;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    private async Task<Guid?> GetAccessibleBranchIdAsync(Guid? requestedBranchId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        if (await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig))
            return requestedBranchId;

        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor is null)
            throw new UnauthorizedAccessException("Пользователь не найден.");

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
            .Include(x => x.Aggregate)
            .Include(x => x.EquipmentModel)
            .Include(x => x.Complex)
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
                ObjectLevel = x.ObjectLevel,
                BranchId = x.BranchId,
                BranchName = x.Branch.Name,
                ObjectCode = x.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.Node!.Code
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.Aggregate!.Code
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.EquipmentModel!.Index
                    : x.Complex!.Code,
                ObjectName = x.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.Node!.Name
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.Aggregate!.Name
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.EquipmentModel!.Name
                    : x.Complex!.Name,
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
        HKObjectLevel? objectLevel = null,
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
        if (objectLevel.HasValue)
            query = query.Where(x => x.ObjectLevel == objectLevel.Value);
        if (!string.IsNullOrWhiteSpace(code))
            query = query.Where(x => EF.Functions.ILike(x.Code, $"%{code.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(version))
            query = query.Where(x => EF.Functions.ILike(x.Version, $"%{version.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(nodeSearch))
        {
            var term = $"%{nodeSearch.Trim()}%";
            query = query.Where(x =>
                x.ObjectLevel == Domain.Enums.HKObjectLevel.Node && (
                    EF.Functions.ILike(x.Node!.Name, term) ||
                    EF.Functions.ILike(x.Node!.Code, term)));
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
                ObjectLevel = x.ObjectLevel,
                BranchId = x.BranchId,
                BranchName = x.Branch.Name,
                ObjectCode = x.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.Node!.Code
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.Aggregate!.Code
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.EquipmentModel!.Index
                    : x.Complex!.Code,
                ObjectName = x.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.Node!.Name
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.Aggregate!.Name
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.EquipmentModel!.Name
                    : x.Complex!.Name,
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
                ObjectLevel = x.ObjectLevel,
                BranchId = x.BranchId,
                BranchName = x.Branch.Name,
                ObjectCode = x.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.Node!.Code
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.Aggregate!.Code
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.EquipmentModel!.Index
                    : x.Complex!.Code,
                ObjectName = x.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.Node!.Name
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.Aggregate!.Name
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.EquipmentModel!.Name
                    : x.Complex!.Name,
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
            .Include(x => x.Aggregate)
            .Include(x => x.EquipmentModel)
            .Include(x => x.Complex)
            .Include(x => x.Items.OrderBy(i => i.SortOrder)).ThenInclude(i => i.AssemblyUnit)
            .Include(x => x.Items).ThenInclude(i => i.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(x => x.StatusLog.OrderByDescending(s => s.ChangedAt))
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<HKCardComponent> AddComponentAsync(Guid parentCardId, Guid childCardId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        var parent = await _db.HKCards.FirstOrDefaultAsync(x => x.Id == parentCardId, ct)
            ?? throw new ArgumentException("Родительская ХК не найдена.");
        var child = await _db.HKCards.FirstOrDefaultAsync(x => x.Id == childCardId, ct)
            ?? throw new ArgumentException("Дочерняя ХК не найдена.");

        if (parent.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            throw new InvalidOperationException("Родительская ХК должна быть в статусе «Черновик» или «На доработке».");

        if (child.Status != HKCardStatus.Approved)
            throw new InvalidOperationException("Дочерняя ХК должна быть утверждена.");

        var now = DateTime.UtcNow;
        if (child.EffectiveDate.HasValue && child.EffectiveDate.Value > now)
            throw new InvalidOperationException("Дочерняя ХК ещё не вступила в силу.");
        if (child.ExpirationDate.HasValue && child.ExpirationDate.Value < now)
            throw new InvalidOperationException("Срок действия дочерней ХК истёк.");

        var levelError = ValidateLevelChain(parent.ObjectLevel, child.ObjectLevel);
        if (levelError != null)
            throw new InvalidOperationException(levelError);

        var compositionError = await ValidateCompositionLinkAsync(parent, child, ct);
        if (compositionError != null)
            throw new InvalidOperationException(compositionError);

        var hasCycle = await DetectCycleAsync(parentCardId, childCardId, ct);
        if (hasCycle)
            throw new InvalidOperationException("Обнаружен циклический состав: дочерняя ХК прямо или косвенно ссылается на родительскую.");

        var existingComponent = await _db.HKCardComponents
            .AnyAsync(x => x.ParentHKCardId == parentCardId && x.ChildHKCardId == childCardId, ct);
        if (existingComponent)
            throw new InvalidOperationException("Эта дочерняя ХК уже включена в состав родительской.");

        var maxOrder = await _db.HKCardComponents
            .Where(x => x.ParentHKCardId == parentCardId)
            .MaxAsync(x => (int?)x.SortOrder, ct) ?? 0;

        var component = new HKCardComponent
        {
            Id = Guid.NewGuid(),
            ParentHKCardId = parentCardId,
            ChildHKCardId = childCardId,
            SortOrder = maxOrder + 1,
            AddedAt = now,
            AddedByUserId = actorId.ToString(),
            ChildCode = child.Code,
            ChildVersion = child.Version,
            ChildApprovedAt = child.ApprovedDate
        };

        _db.HKCardComponents.Add(component);
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCardComponent",
            EntityId = component.Id.ToString(),
            Action = "Added",
            UserId = actorId,
            CreatedAt = now,
            Details = $"Parent: {parent.Code} ({parent.Id}), Child: {child.Code} ({child.Id})"
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("Эта дочерняя ХК уже включена в состав родительской.");
        }

        return component;
    }

    public async Task RemoveComponentAsync(Guid componentId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var component = await _db.HKCardComponents
            .Include(x => x.ParentHKCard)
            .FirstOrDefaultAsync(x => x.Id == componentId, ct)
            ?? throw new ArgumentException("Компонент не найден.");

        if (component.ParentHKCard.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            throw new InvalidOperationException("Нельзя изменить состав утверждённой или отправленной на проверку ХК.");

        _db.HKCardComponents.Remove(component);
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCardComponent",
            EntityId = componentId.ToString(),
            Action = "Removed",
            UserId = actorId,
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<HKCardComponentDto>> GetComponentsAsync(Guid cardId, CancellationToken ct = default)
    {
        return await _db.HKCardComponents
            .AsNoTracking()
            .Where(x => x.ParentHKCardId == cardId)
            .OrderBy(x => x.SortOrder)
            .Select(x => new HKCardComponentDto
            {
                Id = x.Id,
                ParentHKCardId = x.ParentHKCardId,
                ChildHKCardId = x.ChildHKCardId,
                SortOrder = x.SortOrder,
                AddedAt = x.AddedAt,
                ChildCode = x.ChildCode,
                ChildVersion = x.ChildVersion,
                ChildApprovedAt = x.ChildApprovedAt,
                ChildObjectName = x.ChildHKCard.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.ChildHKCard.Node!.Name
                    : x.ChildHKCard.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.ChildHKCard.Aggregate!.Name
                    : x.ChildHKCard.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.ChildHKCard.EquipmentModel!.Name
                    : x.ChildHKCard.Complex!.Name
            })
            .ToListAsync(ct);
    }

    public async Task<List<HKCardComponentDto>> GetParentComponentsAsync(Guid cardId, CancellationToken ct = default)
    {
        return await _db.HKCardComponents
            .AsNoTracking()
            .Where(x => x.ChildHKCardId == cardId)
            .OrderBy(x => x.SortOrder)
            .Select(x => new HKCardComponentDto
            {
                Id = x.Id,
                ParentHKCardId = x.ParentHKCardId,
                ChildHKCardId = x.ChildHKCardId,
                SortOrder = x.SortOrder,
                AddedAt = x.AddedAt,
                ChildCode = x.ChildCode,
                ChildVersion = x.ChildVersion,
                ChildApprovedAt = x.ChildApprovedAt,
                ChildObjectName = x.ChildHKCard.ObjectLevel == Domain.Enums.HKObjectLevel.Node ? x.ChildHKCard.Node!.Name
                    : x.ChildHKCard.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.ChildHKCard.Aggregate!.Name
                    : x.ChildHKCard.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.ChildHKCard.EquipmentModel!.Name
                    : x.ChildHKCard.Complex!.Name
            })
            .ToListAsync(ct);
    }

    public async Task<List<AggregatedRowDto>> GetAggregatedRowsAsync(Guid cardId, CancellationToken ct = default)
    {
        var card = await _db.HKCards.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == cardId, ct)
            ?? throw new ArgumentException("ХК не найдена.");

        if (card.ObjectLevel == Domain.Enums.HKObjectLevel.Node)
            return new List<AggregatedRowDto>();

        var childCardIds = await _db.HKCardComponents
            .Where(x => x.ParentHKCardId == cardId)
            .Select(x => x.ChildHKCardId)
            .ToListAsync(ct);

        if (childCardIds.Count == 0)
            return new List<AggregatedRowDto>();

        return await _db.HKCardItems
            .AsNoTracking()
            .Where(i => childCardIds.Contains(i.HKCardId))
            .OrderBy(i => i.SortOrder)
            .SelectMany(i => i.Materials, (i, m) => new AggregatedRowDto
            {
                SourceCardCode = i.HKCard.Code,
                SourceCardVersion = i.HKCard.Version,
                SourceCardId = i.HKCardId,
                AssemblyUnitName = i.AssemblyUnit!.Name,
                Volume = i.Volume,
                UnitOfMeasure = i.UnitOfMeasure,
                GsmMaterialName = m.GsmMaterial.Name,
                Gost = m.GsmMaterial.Gost,
                Category = m.Category.ToString()
            })
            .ToListAsync(ct);
    }

    private static string? ValidateLevelChain(HKObjectLevel parentLevel, HKObjectLevel childLevel)
    {
        var allowed = (parentLevel, childLevel) switch
        {
            (Domain.Enums.HKObjectLevel.Complex, Domain.Enums.HKObjectLevel.EquipmentModel) => true,
            (Domain.Enums.HKObjectLevel.EquipmentModel, Domain.Enums.HKObjectLevel.Aggregate) => true,
            (Domain.Enums.HKObjectLevel.Aggregate, Domain.Enums.HKObjectLevel.Node) => true,
            _ => false
        };
        return allowed ? null
            : $"Недопустимый уровень: ХК «{parentLevel}» может включать только ХК «{childLevel}».";
    }

    private async Task<string?> ValidateCompositionLinkAsync(HKCard parent, HKCard child, CancellationToken ct = default)
    {
        return (parent.ObjectLevel, child.ObjectLevel) switch
        {
            (Domain.Enums.HKObjectLevel.Aggregate, Domain.Enums.HKObjectLevel.Node) =>
                await ValidateNodeInAggregateCompositionAsync(parent.AggregateId!.Value, child.NodeId!.Value, ct),
            (Domain.Enums.HKObjectLevel.EquipmentModel, Domain.Enums.HKObjectLevel.Aggregate) =>
                await ValidateAggregateInProductCompositionAsync(parent.EquipmentModelId!.Value, child.AggregateId!.Value, ct),
            (Domain.Enums.HKObjectLevel.Complex, Domain.Enums.HKObjectLevel.EquipmentModel) =>
                await ValidateEquipmentModelInComplexCompositionAsync(parent.ComplexId!.Value, child.EquipmentModelId!.Value, ct),
            _ => "Не удалось проверить принадлежность объекта дочерней ХК конструктивному составу."
        };
    }

    private async Task<string?> ValidateNodeInAggregateCompositionAsync(Guid aggregateId, Guid nodeId, CancellationToken ct = default)
    {
        var exists = await _db.AggregateCompositions
            .Where(ac => ac.AggregateId == aggregateId && ac.Status == ProductCompositionStatus.Approved && ac.IsActive)
            .SelectMany(ac => ac.Nodes)
            .AnyAsync(n => n.NodeId == nodeId, ct);
        return exists ? null : "Узел не входит в утверждённый действующий состав агрегата.";
    }

    private async Task<string?> ValidateAggregateInProductCompositionAsync(Guid equipmentModelId, Guid aggregateId, CancellationToken ct = default)
    {
        var exists = await _db.ProductCompositions
            .Where(pc => pc.EquipmentModelId == equipmentModelId && pc.Status == ProductCompositionStatus.Approved && pc.IsActive)
            .SelectMany(pc => pc.Parts)
            .SelectMany(p => p.Aggregates)
            .AnyAsync(a => a.AggregateId == aggregateId, ct);
        return exists ? null : "Агрегат не входит в утверждённый действующий состав изделия.";
    }

    private async Task<string?> ValidateEquipmentModelInComplexCompositionAsync(Guid complexId, Guid equipmentModelId, CancellationToken ct = default)
    {
        var exists = await _db.ComplexCompositions
            .Where(cc => cc.ComplexId == complexId && cc.Status == ProductCompositionStatus.Approved && cc.IsActive)
            .SelectMany(cc => cc.Items)
            .AnyAsync(i => i.EquipmentModelId == equipmentModelId, ct);
        return exists ? null : "Изделие не входит в утверждённый действующий состав комплекса.";
    }

    private async Task<bool> DetectCycleAsync(Guid parentCardId, Guid childCardId, CancellationToken ct = default)
    {
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(childCardId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == parentCardId)
                return true;

            if (!visited.Add(current))
                continue;

            var parentIds = await _db.HKCardComponents
                .Where(x => x.ChildHKCardId == current)
                .Select(x => x.ParentHKCardId)
                .ToListAsync(ct);

            foreach (var id in parentIds)
                queue.Enqueue(id);
        }

        return false;
    }

    private static readonly Regex VersionRegex = new(@"^v(0[1-9]|1[0-2])(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"^ХК-[A-Za-zА-Яа-я0-9]+-\d{4}(-\d+)?$", RegexOptions.Compiled);

    public static bool IsValidVersion(string? version) =>
        !string.IsNullOrEmpty(version) && VersionRegex.IsMatch(version);

    private static string GenerateVersion() =>
        "v" + DateTime.UtcNow.ToString("MMyy");

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && CodeRegex.IsMatch(code);

    private async Task<string> GenerateCodeAsync(string objectCode)
    {
        var year = DateTime.UtcNow.Year.ToString();
        var baseCode = $"ХК-{objectCode}-{year}";

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

    private static string LevelDisplayName(HKObjectLevel level) => level switch
    {
        Domain.Enums.HKObjectLevel.Complex => "Комплекс",
        Domain.Enums.HKObjectLevel.EquipmentModel => "Изделие",
        Domain.Enums.HKObjectLevel.Aggregate => "Агрегат",
        Domain.Enums.HKObjectLevel.Node => "Узел",
        _ => "Объект"
    };

    private async Task EnsureNoActiveDuplicateAsync(HKCard card, CancellationToken ct = default)
    {
        var activeStatuses = new[] { HKCardStatus.Draft, HKCardStatus.OnReview, HKCardStatus.RevisionRequired };

        var hasActive = card.ObjectLevel switch
        {
            Domain.Enums.HKObjectLevel.Node => await _db.HKCards.AnyAsync(x =>
                x.ObjectLevel == Domain.Enums.HKObjectLevel.Node &&
                x.NodeId == card.NodeId &&
                activeStatuses.Contains(x.Status), ct),
            Domain.Enums.HKObjectLevel.Aggregate => await _db.HKCards.AnyAsync(x =>
                x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate &&
                x.AggregateId == card.AggregateId &&
                activeStatuses.Contains(x.Status), ct),
            Domain.Enums.HKObjectLevel.EquipmentModel => await _db.HKCards.AnyAsync(x =>
                x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel &&
                x.EquipmentModelId == card.EquipmentModelId &&
                activeStatuses.Contains(x.Status), ct),
            Domain.Enums.HKObjectLevel.Complex => await _db.HKCards.AnyAsync(x =>
                x.ObjectLevel == Domain.Enums.HKObjectLevel.Complex &&
                x.ComplexId == card.ComplexId &&
                activeStatuses.Contains(x.Status), ct),
            _ => throw new ArgumentException("Неизвестный уровень объекта.")
        };

        if (hasActive)
            throw new InvalidOperationException(
                $"Для выбранного {LevelDisplayName(card.ObjectLevel).ToLowerInvariant()} уже существует активная ХК. " +
                "Откройте, продолжите или удалите существующую карточку.");
    }

    private async Task<string> ResolveObjectCodeAsync(HKCard card)
    {
        var code = card.ObjectLevel switch
        {
            Domain.Enums.HKObjectLevel.Node => (await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == card.NodeId))?.Code,
            Domain.Enums.HKObjectLevel.Aggregate => (await _db.Aggregates.AsNoTracking().FirstOrDefaultAsync(a => a.Id == card.AggregateId))?.Code,
            Domain.Enums.HKObjectLevel.EquipmentModel => (await _db.EquipmentModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == card.EquipmentModelId))?.Index,
            Domain.Enums.HKObjectLevel.Complex => (await _db.Complexes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == card.ComplexId))?.Code,
            _ => null
        };
        return code ?? throw new ArgumentException("Не удалось определить код объекта нормирования.");
    }

    public async Task<string> GenerateCodeForNodeAsync(Guid nodeId)
    {
        var node = await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId)
            ?? throw new ArgumentException("Узел не найден");
        return await GenerateCodeAsync(node.Code);
    }

    public async Task<bool> HasActiveCardForNodeAsync(Guid nodeId) =>
        await _db.HKCards.AnyAsync(x =>
            x.ObjectLevel == Domain.Enums.HKObjectLevel.Node &&
            x.NodeId == nodeId &&
            (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired));

    public async Task<HKCard?> GetActiveCardForNodeAsync(Guid nodeId) =>
        await _db.HKCards.AsNoTracking()
            .Where(x =>
                x.ObjectLevel == Domain.Enums.HKObjectLevel.Node &&
                x.NodeId == nodeId &&
                (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired))
            .FirstOrDefaultAsync();

    public async Task<HKCard> CreateAsync(HKCard card, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null)
            throw new UnauthorizedAccessException("Пользователь не найден.");
        var createPerm = card.ObjectLevel switch
        {
            Domain.Enums.HKObjectLevel.Node => PermissionCodes.HKNodeCreate,
            Domain.Enums.HKObjectLevel.Aggregate => PermissionCodes.HKAggregateCreate,
            Domain.Enums.HKObjectLevel.EquipmentModel => PermissionCodes.HKEquipmentCreate,
            Domain.Enums.HKObjectLevel.Complex => PermissionCodes.HKComplexCreate,
            _ => throw new ArgumentException("Неизвестный уровень объекта.")
        };
        if (!await _permissions.HasPermissionAsync(actorId.ToString(), createPerm))
            throw new UnauthorizedAccessException("Недостаточно прав для создания ХК.");

        if (actor.BranchId == null || actor.BranchId.Value == Guid.Empty)
            throw new InvalidOperationException("У пользователя не указан филиал. Создание ХК невозможно.");

        var validation = await _hkValidation.ValidateDraftAsync(card, ct);
        if (!validation.IsValid)
            throw new HKCardValidationException(validation.Errors);

        await EnsureNoActiveDuplicateAsync(card, ct);

        card.Id = Guid.NewGuid();
        var objectCode = await ResolveObjectCodeAsync(card);
        card.Code = await GenerateCodeAsync(objectCode);
        card.Version = GenerateVersion();
        var now = _time.GetUtcNow().UtcDateTime;
        card.CreatedAt = now;
        card.UpdatedAt = now;
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

        _db.HKCards.Add(card);
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "HKCard",
            card.Id.ToString(),
            "Created",
            actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: $"Создана ХК уровня «{LevelDisplayName(card.ObjectLevel)}» для объекта «{objectCode}»."), ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: string constraint
            } && constraint.StartsWith("UX_HKCards_OneActivePer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Для выбранного {LevelDisplayName(card.ObjectLevel).ToLowerInvariant()} уже существует активная ХК. " +
                "Откройте, продолжите или удалите существующую карточку.");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to create HK card (ObjectLevel={ObjectLevel})", card.ObjectLevel);
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
        var editPerm = card.ObjectLevel switch
        {
            Domain.Enums.HKObjectLevel.Node => PermissionCodes.HKNodeEditDraft,
            Domain.Enums.HKObjectLevel.Aggregate => PermissionCodes.HKAggregateEditDraft,
            Domain.Enums.HKObjectLevel.EquipmentModel => PermissionCodes.HKEquipmentEditDraft,
            Domain.Enums.HKObjectLevel.Complex => PermissionCodes.HKComplexEditDraft,
            _ => throw new ArgumentException("Неизвестный уровень объекта.")
        };
        if (!await _permissions.HasPermissionAsync(actorId.ToString(), editPerm))
            throw new UnauthorizedAccessException("Недостаточно прав для редактирования ХК.");

        if (card.ObjectLevel == Domain.Enums.HKObjectLevel.Node && (!card.NodeId.HasValue || card.NodeId == Guid.Empty))
            throw new ArgumentException("Необходимо выбрать узел.");

        if (card.RowVersion == 0)
            throw new InvalidOperationException("Версия карточки не указана. Обновите страницу и повторите попытку.");

        var validation = await _hkValidation.ValidateDraftAsync(card, ct);
        if (!validation.IsValid)
            throw new HKCardValidationException(validation.Errors);

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
        existing.RequestOrganization = card.RequestOrganization;
        existing.RequestSenderFullName = card.RequestSenderFullName;
        existing.RequestReceivedDate = card.RequestReceivedDate;
        existing.RequestDetails = card.RequestDetails;
        existing.IncomingLetterNumber = card.IncomingLetterNumber;
        existing.OutgoingLetterNumber = card.OutgoingLetterNumber;
        existing.EffectiveDate = card.EffectiveDate;
        existing.ExpirationDate = card.ExpirationDate;
        existing.UpdatedAt = DateTime.UtcNow;

        if (existing.ObjectLevel == Domain.Enums.HKObjectLevel.Node && existing.NodeId != card.NodeId)
        {
            if (existing.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
                throw new InvalidOperationException("Изменение узла недоступно для карточки в текущем статусе.");

            var hasActiveOnNewNode = await _db.HKCards.AnyAsync(x =>
                x.Id != existing.Id &&
                x.ObjectLevel == Domain.Enums.HKObjectLevel.Node &&
                x.NodeId == card.NodeId &&
                (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.OnReview || x.Status == HKCardStatus.RevisionRequired));
            if (hasActiveOnNewNode)
                throw new InvalidOperationException(
                    "Для выбранного узла уже существует активная ХК. " +
                    "Завершите или архивируйте существующую карточку перед сменой узла.");

            existing.NodeId = card.NodeId;
            existing.Code = await GenerateCodeAsync(await ResolveObjectCodeAsync(card));
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

        var card = await _db.HKCards.FindAsync(id, ct);
        if (card == null)
            return (false, "Карточка не найдена");

        if (actor.BranchId != card.BranchId && !await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig))
            return (false, "Нет прав для изменения карточки другого филиала");

        var permError = await CheckStatusChangePermissionAsync(card, newStatus);
        if (permError != null)
            return (false, permError);

        var oldStatus = card.Status;
        if (!HKCardStatusTransitions.IsAllowed(oldStatus, newStatus))
            return (false, HKCardStatusTransitions.GetErrorMessage(oldStatus, newStatus));

        if (newStatus is HKCardStatus.OnReview or HKCardStatus.Approved)
        {
            var validationCard = await _db.HKCards.AsNoTracking()
                .Include(x => x.Items).ThenInclude(i => i.Materials)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (validationCard != null)
            {
                var result = newStatus == HKCardStatus.Approved
                    ? await _hkValidation.ValidateForApprovalAsync(validationCard, ct)
                    : await _hkValidation.ValidateForReviewAsync(validationCard, ct);
                if (!result.IsValid)
                    return (false, result.ToUserMessage());
            }
        }

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
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await ApplyWorkflowTasksAsync(card, newStatus, actorId, ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct);
                return (false, "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
        }

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

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig) && actor.BranchId != card.BranchId)
            return (false, "Нет доступа к карточке другого филиала");

        var actorRole = ResolveUserRole(roles);
        if (!HKCardStatusTransitions.CanDelete(card.Status, actorRole))
            return (false,
                $"Нельзя удалить карточку со статусом «{card.Status}». " +
                "Утверждённые карточки можно только архивировать.");

        return await ChangeStatusAsync(id, HKCardStatus.Deleted, null, ct);
    }

    private async Task ApplyWorkflowTasksAsync(HKCard card, HKCardStatus to, Guid actorUserId, CancellationToken ct)
    {
        switch (to)
        {
            case HKCardStatus.OnReview:
                await CreateWorkflowTaskAsync(
                    card,
                    type: WorkTaskType.HKReview,
                    title: $"Проверка ХК {card.Code}",
                    description: $"Карточка {card.Code} (v{card.Version}) отправлена на проверку.",
                    role: "NormAdmin",
                    dueDays: 7,
                    ct: ct);
                break;

            case HKCardStatus.RevisionRequired:
                await CloseOpenWorkflowTasksAsync(WorkTaskType.HKReview, "HKCard", card.Id, cancelled: true, actorUserId, ct);
                if (card.AuthorId.HasValue)
                {
                    await CreateWorkflowTaskAsync(
                        card,
                        type: WorkTaskType.HKRevision,
                        title: $"Доработка ХК {card.Code}",
                        description: $"Карточка {card.Code} возвращена на доработку.",
                        assignee: card.AuthorId.Value.ToString(),
                        dueDays: 7,
                        ct: ct);
                }
                break;

            case HKCardStatus.Approved:
                await CloseOpenWorkflowTasksAsync(WorkTaskType.HKReview, "HKCard", card.Id, cancelled: false, actorUserId, ct);
                await CreateRecalculationTasksAsync(card, actorUserId, ct);
                break;

            case HKCardStatus.Archived:
            case HKCardStatus.Deleted:
                break;
        }
    }

    private async Task CreateWorkflowTaskAsync(
        HKCard card,
        WorkTaskType type,
        string title,
        string description,
        string? assignee = null,
        string? role = null,
        int? dueDays = null,
        CancellationToken ct = default)
    {
        var resolvedAssignee = assignee;
        if (string.IsNullOrWhiteSpace(resolvedAssignee) && !string.IsNullOrWhiteSpace(role))
        {
            var users = await GetBranchUsersInRoleAsync(card.BranchId, role);
            resolvedAssignee = users.Count > 0 ? users[0] : await GetAnyUserInRoleAsync(role);
        }
        if (string.IsNullOrWhiteSpace(resolvedAssignee))
            return;

        await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
            Title: title,
            Type: type,
            Priority: WorkTaskPriority.Normal,
            Description: description,
            AssignedToUserId: resolvedAssignee,
            BranchId: card.BranchId,
            EntityType: "HKCard",
            EntityId: card.Id,
            EntityCodeSnapshot: card.Code,
            EntityTitleSnapshot: $"v{card.Version}",
            DueDateUtc: dueDays.HasValue ? DateTime.UtcNow.AddDays(dueDays.Value) : null,
            NotifyAssignee: true), ct);
    }

    private async Task CloseOpenWorkflowTasksAsync(
        WorkTaskType type, string entityType, Guid entityId, bool cancelled, Guid actorUserId, CancellationToken ct)
    {
        var open = await _db.WorkTasks
            .Where(t => !t.IsDeleted
                && t.Type == type
                && t.EntityType == entityType
                && t.EntityId == entityId
                && (t.Status == WorkTaskStatus.Open
                    || t.Status == WorkTaskStatus.InProgress
                    || t.Status == WorkTaskStatus.Overdue))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var task in open)
        {
            if (cancelled)
            {
                task.Status = WorkTaskStatus.Cancelled;
                await _audit.CreateLogAsync(
                    new AuditWriteRequest(
                        EntityType: "WorkTask",
                        EntityId: task.Id.ToString(),
                        Action: "Task.Cancelled",
                        ActorUserId: actorUserId,
                        EntityDisplayName: task.Title,
                        Details: "Задача закрыта при изменении статуса ХК"),
                    ct);
            }
            else
            {
                task.Status = WorkTaskStatus.Completed;
                task.CompletedAtUtc = now;
                task.CompletedByUserId = actorUserId.ToString();
                task.CompletionComment = "ХК утверждена";
                await _audit.CreateLogAsync(
                    new AuditWriteRequest(
                        EntityType: "WorkTask",
                        EntityId: task.Id.ToString(),
                        Action: "Task.Completed",
                        ActorUserId: actorUserId,
                        EntityDisplayName: task.Title,
                        Details: "ХК утверждена"),
                    ct);
            }
            task.UpdatedAtUtc = now;
        }
    }

    private async Task CreateRecalculationTasksAsync(HKCard card, Guid actorUserId, CancellationToken ct)
    {
        var instanceIds = await _db.IndividualCards
            .Where(c => _db.HKCards.Any(h => h.Id == c.HKCardId && h.Code == card.Code))
            .Select(c => c.EquipmentInstanceId)
            .Distinct()
            .ToListAsync(ct);

        var objectId = card.ObjectLevel switch
        {
            Domain.Enums.HKObjectLevel.Node => card.NodeId,
            Domain.Enums.HKObjectLevel.Aggregate => card.AggregateId,
            Domain.Enums.HKObjectLevel.EquipmentModel => card.EquipmentModelId,
            Domain.Enums.HKObjectLevel.Complex => card.ComplexId,
            _ => null
        };
        var modelIds = await (
            from a in _db.ProductCompositionAggregates
            join p in _db.ProductCompositionParts on a.PartId equals p.Id
            join pc in _db.ProductCompositions on p.ProductCompositionId equals pc.Id
            where a.AggregateId == objectId
            select pc.EquipmentModelId).Distinct().ToListAsync(ct);

        if (modelIds.Count != 0)
        {
            var fromModels = await _db.EquipmentInstances
                .Where(i => modelIds.Contains(i.EquipmentModelId))
                .Select(i => i.Id)
                .ToListAsync(ct);
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
            await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
                Title: "Пересчёт инд. карт — экземпляр",
                Type: WorkTaskType.HKReview,
                Priority: WorkTaskPriority.Normal,
                Description: $"Утверждена новая версия ХК {card.Code} (v{card.Version}). Требуется подтверждение пересчёта индивидуальных карт.",
                AssignedToUserId: assignee,
                BranchId: card.BranchId,
                EntityType: "EquipmentInstance",
                EntityId: instanceId,
                EntityCodeSnapshot: card.Code,
                EntityTitleSnapshot: $"v{card.Version}",
                DueDateUtc: DateTime.UtcNow.AddDays(14),
                NotifyAssignee: true), ct);
        }
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
            .Include(x => x.Aggregate)
            .Include(x => x.EquipmentModel)
            .Include(x => x.Complex)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        if (branchId.HasValue)
            query = query.Where(x => x.BranchId == branchId.Value);

        return query.OrderByDescending(x => x.CreatedAt);
    }

    private async Task<string?> CheckStatusChangePermissionAsync(HKCard card, HKCardStatus newStatus)
    {
        var actorId = _currentUser.GetRequiredUserId();

        switch (newStatus)
        {
            case HKCardStatus.OnReview:
                var submitPerm = card.ObjectLevel switch
                {
                    Domain.Enums.HKObjectLevel.Node => PermissionCodes.HKNodeSubmit,
                    Domain.Enums.HKObjectLevel.Aggregate => PermissionCodes.HKAggregateSubmit,
                    Domain.Enums.HKObjectLevel.EquipmentModel => PermissionCodes.HKEquipmentSubmit,
                    Domain.Enums.HKObjectLevel.Complex => PermissionCodes.HKComplexSubmit,
                    _ => null
                };
                if (submitPerm != null && !await _permissions.HasPermissionAsync(actorId.ToString(), submitPerm))
                    return "Недостаточно прав для отправки ХК на проверку.";
                break;

            case HKCardStatus.Approved:
                if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKApprove))
                    return "Недостаточно прав для утверждения ХК.";
                break;

            case HKCardStatus.RevisionRequired:
                if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKReview))
                    return "Недостаточно прав для возврата ХК на доработку.";
                break;

            case HKCardStatus.Archived:
                if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKArchive))
                    return "Недостаточно прав для архивации ХК.";
                break;

            case HKCardStatus.Deleted:
                if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKDelete))
                    return "Недостаточно прав для удаления ХК.";
                var actor = await _userManager.FindByIdAsync(actorId.ToString());
                var roles = actor != null ? await _userManager.GetRolesAsync(actor) : [];
                if (!HKCardStatusTransitions.CanDelete(card.Status, ResolveUserRole(roles)))
                    return "Нельзя удалить карточку в текущем статусе.";
                break;
        }

        return null;
    }

    private static UserRole ResolveUserRole(IList<string> roles)
    {
        if (roles.Contains(nameof(UserRole.SystemAdmin))) return UserRole.SystemAdmin;
        if (roles.Contains(nameof(UserRole.NormAdmin))) return UserRole.NormAdmin;
        if (roles.Contains(nameof(UserRole.HeadOfDepartment))) return UserRole.HeadOfDepartment;
        if (roles.Contains(nameof(UserRole.Guest))) return UserRole.Guest;
        return UserRole.Operator;
    }

    public async Task<ReferenceProposal> CreateProposalAsync(
        Guid hkCardId, ProposalTargetType targetType,
        string code, string name, string? description, string? gost, string? type)
    {
        var card = await _db.HKCards.FindAsync(hkCardId)
            ?? throw new ArgumentException("ХК не найдена.");
        if (card.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            throw new InvalidOperationException("Предложения можно создавать только для черновика или карты на доработке.");

        var actorId = _currentUser.GetRequiredUserId();
        var proposal = new ReferenceProposal
        {
            Id = Guid.NewGuid(),
            HKCardId = hkCardId,
            TargetType = targetType,
            Code = code.Trim(),
            Name = name.Trim(),
            Description = description?.Trim(),
            Gost = gost?.Trim(),
            Type = type?.Trim(),
            Status = ProposalStatus.Pending,
            CreatedByUserId = actorId.ToString(),
            CreatedAt = DateTime.UtcNow
        };

        switch (targetType)
        {
            case ProposalTargetType.Node:
                var node = new Node
                {
                    Id = proposal.Id,
                    Code = proposal.Code,
                    Name = proposal.Name,
                    Description = proposal.Description,
                    IsDraft = true
                };
                _db.Nodes.Add(node);
                proposal.CreatedStubNodeId = node.Id;
                break;

            case ProposalTargetType.AssemblyUnit:
                var au = new AssemblyUnit
                {
                    Id = proposal.Id,
                    Code = proposal.Code,
                    Name = proposal.Name,
                    Description = proposal.Description,
                    IsDraft = true
                };
                _db.AssemblyUnits.Add(au);
                proposal.CreatedStubAssemblyUnitId = au.Id;
                break;

            case ProposalTargetType.GsmMaterial:
                var gsm = new GsmMaterial
                {
                    Id = proposal.Id,
                    Name = proposal.Name,
                    Type = proposal.Type ?? "",
                    Gost = proposal.Gost,
                    Description = proposal.Description,
                    IsDraft = true
                };
                _db.GsmMaterials.Add(gsm);
                proposal.CreatedStubGsmMaterialId = gsm.Id;
                break;
        }

        _db.ReferenceProposals.Add(proposal);
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "ReferenceProposal",
            EntityId = proposal.Id.ToString(),
            Action = "Created",
            UserId = actorId,
            CreatedAt = DateTime.UtcNow,
            EntityDisplayName = $"Предложение для {card.Code}: {name}"
        });

        await _db.SaveChangesAsync();
        return proposal;
    }

    public async Task<List<ReferenceProposal>> GetProposalsAsync(Guid hkCardId)
    {
        return await _db.ReferenceProposals
            .Where(p => p.HKCardId == hkCardId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task AcceptProposalAsync(Guid proposalId)
    {
        var proposal = await _db.ReferenceProposals.FindAsync(proposalId)
            ?? throw new ArgumentException("Предложение не найдено.");
        if (proposal.Status != ProposalStatus.Pending)
            throw new InvalidOperationException("Принять можно только предложение в статусе Ожидает.");

        proposal.Status = ProposalStatus.Accepted;
        proposal.ResolvedAt = DateTime.UtcNow;

        switch (proposal.TargetType)
        {
            case ProposalTargetType.Node:
                if (proposal.CreatedStubNodeId.HasValue)
                {
                    var node = await _db.Nodes.FindAsync(proposal.CreatedStubNodeId.Value);
                    if (node != null) node.IsDraft = false;
                }
                break;
            case ProposalTargetType.AssemblyUnit:
                if (proposal.CreatedStubAssemblyUnitId.HasValue)
                {
                    var au = await _db.AssemblyUnits.FindAsync(proposal.CreatedStubAssemblyUnitId.Value);
                    if (au != null) au.IsDraft = false;
                }
                break;
            case ProposalTargetType.GsmMaterial:
                if (proposal.CreatedStubGsmMaterialId.HasValue)
                {
                    var gsm = await _db.GsmMaterials.FindAsync(proposal.CreatedStubGsmMaterialId.Value);
                    if (gsm != null) gsm.IsDraft = false;
                }
                break;
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "ReferenceProposal",
            EntityId = proposal.Id.ToString(),
            Action = "Accepted",
            UserId = Guid.Parse(proposal.CreatedByUserId),
            CreatedAt = DateTime.UtcNow,
            EntityDisplayName = $"Предложение: {proposal.Name}"
        });

        await _db.SaveChangesAsync();
    }

    public async Task RejectProposalAsync(Guid proposalId)
    {
        var proposal = await _db.ReferenceProposals.FindAsync(proposalId)
            ?? throw new ArgumentException("Предложение не найдено.");
        if (proposal.Status != ProposalStatus.Pending)
            throw new InvalidOperationException("Отклонить можно только предложение в статусе Ожидает.");

        proposal.Status = ProposalStatus.Rejected;
        proposal.ResolvedAt = DateTime.UtcNow;

        switch (proposal.TargetType)
        {
            case ProposalTargetType.Node:
                if (proposal.CreatedStubNodeId.HasValue)
                {
                    var node = await _db.Nodes.FindAsync(proposal.CreatedStubNodeId.Value);
                    if (node != null) _db.Nodes.Remove(node);
                }
                break;
            case ProposalTargetType.AssemblyUnit:
                if (proposal.CreatedStubAssemblyUnitId.HasValue)
                {
                    var au = await _db.AssemblyUnits.FindAsync(proposal.CreatedStubAssemblyUnitId.Value);
                    if (au != null) _db.AssemblyUnits.Remove(au);
                }
                break;
            case ProposalTargetType.GsmMaterial:
                if (proposal.CreatedStubGsmMaterialId.HasValue)
                {
                    var gsm = await _db.GsmMaterials.FindAsync(proposal.CreatedStubGsmMaterialId.Value);
                    if (gsm != null) _db.GsmMaterials.Remove(gsm);
                }
                break;
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "ReferenceProposal",
            EntityId = proposal.Id.ToString(),
            Action = "Rejected",
            UserId = Guid.Parse(proposal.CreatedByUserId),
            CreatedAt = DateTime.UtcNow,
            EntityDisplayName = $"Предложение: {proposal.Name}"
        });

        await _db.SaveChangesAsync();
    }
}
