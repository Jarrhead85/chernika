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
    private readonly NotificationService _notifications;
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
        NotificationService notifications,
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
        _notifications = notifications;
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

    public async Task<PagedResult<HKCardRegistryListItemDto>> GetRegistryPageAsync(
        HKCardRegistryQuery query, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId().ToString();
        var safeBranchId = await GetAccessibleBranchIdAsync(query.BranchId, ct);

        var baseQuery = _db.HKCards.AsNoTracking().AsQueryable();
        if (safeBranchId.HasValue)
            baseQuery = baseQuery.Where(x => x.BranchId == safeBranchId.Value);

        if (query.Status.HasValue)
            baseQuery = baseQuery.Where(x => x.Status == query.Status.Value);
        if (query.ObjectLevel.HasValue)
            baseQuery = baseQuery.Where(x => x.ObjectLevel == query.ObjectLevel.Value);
        if (query.OnlyMine)
            baseQuery = baseQuery.Where(x => x.AuthorId.ToString() == actorId);
        if (!string.IsNullOrWhiteSpace(query.AuthorId))
            baseQuery = baseQuery.Where(x => x.AuthorId.ToString() == query.AuthorId);
        if (query.CreatedFrom.HasValue)
            baseQuery = baseQuery.Where(x => x.CreatedAt >= query.CreatedFrom.Value);
        if (query.CreatedTo.HasValue)
            baseQuery = baseQuery.Where(x => x.CreatedAt < query.CreatedTo.Value.AddDays(1));
        if (query.ApprovedFrom.HasValue)
            baseQuery = baseQuery.Where(x => x.ApprovedDate >= query.ApprovedFrom.Value);
        if (query.ApprovedTo.HasValue)
            baseQuery = baseQuery.Where(x => x.ApprovedDate < query.ApprovedTo.Value.AddDays(1));

        var today = _time.GetUtcNow().UtcDateTime.Date;
        baseQuery = query.ExpirationFilter switch
        {
            HKCardExpirationFilter.Expiring90Days => baseQuery.Where(x => x.ExpirationDate >= today && x.ExpirationDate <= today.AddDays(90)),
            HKCardExpirationFilter.Expiring30Days => baseQuery.Where(x => x.ExpirationDate >= today && x.ExpirationDate <= today.AddDays(30)),
            HKCardExpirationFilter.Expired => baseQuery.Where(x => x.ExpirationDate < today),
            _ => baseQuery
        };

        if (query.HasPdf.HasValue)
        {
            var pdfIds = await _db.HKCardAttachments.AsNoTracking()
                .Where(a => a.ContentType == "application/pdf")
                .Select(a => a.HKCardId)
                .Distinct()
                .ToListAsync(ct);
            baseQuery = query.HasPdf.Value
                ? baseQuery.Where(x => pdfIds.Contains(x.Id))
                : baseQuery.Where(x => !pdfIds.Contains(x.Id));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = $"%{query.SearchText.Trim()}%";
            baseQuery = baseQuery.Where(x =>
                EF.Functions.ILike(x.Code, term)
                || EF.Functions.ILike(x.Version, term)
                || EF.Functions.ILike(x.RequestOrganization ?? "", term)
                || EF.Functions.ILike(x.IncomingLetterNumber ?? "", term)
                || EF.Functions.ILike(x.OutgoingLetterNumber ?? "", term)
                || (x.ObjectLevel == Domain.Enums.HKObjectLevel.Node && (EF.Functions.ILike(x.Node!.Name, term) || EF.Functions.ILike(x.Node!.Code, term)))
                || (x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate && (EF.Functions.ILike(x.Aggregate!.Name, term) || EF.Functions.ILike(x.Aggregate!.Code, term)))
                || (x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel && (EF.Functions.ILike(x.EquipmentModel!.Name, term) || EF.Functions.ILike(x.EquipmentModel!.Index, term)))
                || (x.ObjectLevel == Domain.Enums.HKObjectLevel.Complex && (EF.Functions.ILike(x.Complex!.Name, term) || EF.Functions.ILike(x.Complex!.Code, term))));
        }

        if (query.RequiresMyAction)
        {
            var actionableIds = await GetActionableCardIdsAsync(actorId, query.BranchId, ct);
            if (actionableIds.Count == 0)
                return new PagedResult<HKCardRegistryListItemDto> { Items = new List<HKCardRegistryListItemDto>(), TotalCount = 0, Page = query.Page, PageSize = query.PageSize };
            baseQuery = baseQuery.Where(x => actionableIds.Contains(x.Id));
        }

        var ordered = (query.SortBy?.ToLowerInvariant() switch
        {
            "code" => query.SortDescending ? baseQuery.OrderByDescending(x => x.Code) : baseQuery.OrderBy(x => x.Code),
            "status" => query.SortDescending ? baseQuery.OrderByDescending(x => x.Status) : baseQuery.OrderBy(x => x.Status),
            "level" => query.SortDescending ? baseQuery.OrderByDescending(x => x.ObjectLevel) : baseQuery.OrderBy(x => x.ObjectLevel),
            _ => query.SortDescending ? baseQuery.OrderByDescending(x => x.CreatedAt) : baseQuery.OrderBy(x => x.CreatedAt),
        })!;

        var total = await ordered.CountAsync(ct);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new HKCardRegistryListItemDto
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
                ObjectId = x.ObjectLevel == Domain.Enums.HKObjectLevel.Aggregate ? x.AggregateId
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.EquipmentModel ? x.EquipmentModelId
                    : x.ObjectLevel == Domain.Enums.HKObjectLevel.Complex ? x.ComplexId
                    : (Guid?)null,
                CreatedAt = x.CreatedAt,
                ApprovedDate = x.ApprovedDate,
                EffectiveDate = x.EffectiveDate,
                ExpirationDate = x.ExpirationDate,
                AuthorId = x.AuthorId.ToString(),
                RequestOrganization = x.RequestOrganization,
                IncomingLetterNumber = x.IncomingLetterNumber,
                OutgoingLetterNumber = x.OutgoingLetterNumber,
                HasPdf = _db.HKCardAttachments.Any(a => a.HKCardId == x.Id && a.ContentType == "application/pdf")
            })
            .ToListAsync(ct);

        var authorIds = items.Select(x => x.AuthorId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (authorIds.Any())
        {
            var names = await _db.Users.AsNoTracking()
                .Where(u => authorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
            foreach (var item in items)
                if (item.AuthorId != null && names.TryGetValue(item.AuthorId, out var name))
                    item.AuthorName = name;
        }

        return new PagedResult<HKCardRegistryListItemDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<HKCardKpiDto> GetRegistryKpiAsync(Guid? branchId = null, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId().ToString();
        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);

        var query = _db.HKCards.AsNoTracking().AsQueryable();
        if (safeBranchId.HasValue)
            query = query.Where(x => x.BranchId == safeBranchId.Value);

        var statusGroups = await query
            .GroupBy(x => x.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var statusCounts = statusGroups.ToDictionary(x => x.Key, x => x.Count);
        var total = statusCounts.Values.Sum();

        var actionableIds = await GetActionableCardIdsAsync(actorId, branchId, ct);

        return new HKCardKpiDto
        {
            Total = total,
            Draft = statusCounts.GetValueOrDefault(HKCardStatus.Draft),
            OnReview = statusCounts.GetValueOrDefault(HKCardStatus.OnReview),
            RevisionRequired = statusCounts.GetValueOrDefault(HKCardStatus.RevisionRequired),
            Approved = statusCounts.GetValueOrDefault(HKCardStatus.Approved),
            RequiresMyAction = actionableIds.Count
        };
    }

    private async Task<HashSet<Guid>> GetActionableCardIdsAsync(string actorId, Guid? branchId, CancellationToken ct)
    {
        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);
        var result = new HashSet<Guid>();

        var taskIds = await _db.WorkTasks.AsNoTracking()
            .Where(t => !t.IsDeleted
                && t.AssignedToUserId == actorId
                && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue)
                && t.EntityType == "HKCard"
                && t.EntityId.HasValue)
            .Select(t => t.EntityId!.Value)
            .ToListAsync(ct);
        foreach (var id in taskIds)
            result.Add(id);

        var baseQuery = _db.HKCards.AsNoTracking().AsQueryable();
        if (safeBranchId.HasValue)
            baseQuery = baseQuery.Where(x => x.BranchId == safeBranchId.Value);

        var editDraftLevels = new List<Domain.Enums.HKObjectLevel>();
        if (await _permissions.HasPermissionAsync(actorId, PermissionCodes.HKNodeEditDraft))
            editDraftLevels.Add(Domain.Enums.HKObjectLevel.Node);
        if (await _permissions.HasPermissionAsync(actorId, PermissionCodes.HKAggregateEditDraft))
            editDraftLevels.Add(Domain.Enums.HKObjectLevel.Aggregate);
        if (await _permissions.HasPermissionAsync(actorId, PermissionCodes.HKEquipmentEditDraft))
            editDraftLevels.Add(Domain.Enums.HKObjectLevel.EquipmentModel);
        if (await _permissions.HasPermissionAsync(actorId, PermissionCodes.HKComplexEditDraft))
            editDraftLevels.Add(Domain.Enums.HKObjectLevel.Complex);

        if (editDraftLevels.Any())
        {
            var editableIds = await baseQuery
                .Where(x => (x.Status == HKCardStatus.Draft || x.Status == HKCardStatus.RevisionRequired)
                    && x.AuthorId.ToString() == actorId
                    && editDraftLevels.Contains(x.ObjectLevel))
                .Select(x => x.Id)
                .ToListAsync(ct);
            foreach (var id in editableIds) result.Add(id);
        }

        if (await _permissions.HasPermissionAsync(actorId, PermissionCodes.HKReview))
        {
            var reviewIds = await baseQuery.Where(x => x.Status == HKCardStatus.OnReview).Select(x => x.Id).ToListAsync(ct);
            foreach (var id in reviewIds) result.Add(id);
        }

        if (await _permissions.HasPermissionAsync(actorId, PermissionCodes.HKArchive))
        {
            var archiveIds = await baseQuery.Where(x => x.Status == HKCardStatus.Approved).Select(x => x.Id).ToListAsync(ct);
            foreach (var id in archiveIds) result.Add(id);
        }

        return result;
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
    public async Task<IReadOnlyList<HKCardVersionDto>> GetVersionsAsync(Guid id, CancellationToken ct = default)
    {
        var card = await _db.HKCards
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.Status != HKCardStatus.Deleted, ct);

        if (card is null)
            return Array.Empty<HKCardVersionDto>();

        var accessibleBranchId = await GetAccessibleBranchIdAsync(card.BranchId, ct);

        var query = _db.HKCards
            .AsNoTracking()
            .Where(x =>
                x.Status != HKCardStatus.Deleted &&
                x.Code == card.Code &&
                x.ObjectLevel == card.ObjectLevel &&
                x.ComplexId == card.ComplexId &&
                x.EquipmentModelId == card.EquipmentModelId &&
                x.AggregateId == card.AggregateId &&
                x.NodeId == card.NodeId);

        if (accessibleBranchId.HasValue)
            query = query.Where(x => x.BranchId == accessibleBranchId.Value);

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Version)
            .Select(x => new HKCardVersionDto
            {
                Id = x.Id,
                Version = x.Version,
                Status = x.Status,
                ApprovedDate = x.ApprovedDate,
                CreatedAt = x.CreatedAt,
                IsCurrent = x.Id == id,
                SupersedesHKCardId = x.SupersedesHKCardId
            })
            .ToListAsync(ct);
    }

    public async Task<List<Branch>> GetAllBranchesAsync(CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.SystemConfig, ct);

        return await _db.Branches.AsNoTracking()
            .Where(b => !b.IsDeleted)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
    }

    public async Task<List<HKCardRegistryAuthorDto>> GetAuthorsAsync(CancellationToken ct = default)
    {
        var safeBranchId = await GetAccessibleBranchIdAsync(null, ct);

        var query = _db.HKCards.AsNoTracking()
            .Where(c => c.AuthorId != null && c.AuthorId != Guid.Empty);

        if (safeBranchId.HasValue)
            query = query.Where(c => c.BranchId == safeBranchId.Value);

        var authorIds = await query
            .Select(c => c.AuthorId!.Value.ToString())
            .Distinct()
            .ToListAsync(ct);

        if (authorIds.Count == 0)
            return new List<HKCardRegistryAuthorDto>();

        return await _db.Users.AsNoTracking()
            .Where(u => authorIds.Contains(u.Id))
            .OrderBy(u => u.FullName)
            .ThenBy(u => u.UserName)
            .Select(u => new HKCardRegistryAuthorDto
            {
                Id = u.Id,
                FullName = u.FullName ?? u.UserName ?? u.Id
            })
            .ToListAsync(ct);
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

        if (newStatus == HKCardStatus.Deleted && string.IsNullOrWhiteSpace(comment))
            return (false, "Укажите причину удаления ХК.");

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

        if (newStatus == HKCardStatus.OnReview)
        {
            var branchNormAdmins = await GetBranchUsersInRoleAsync(card.BranchId, "NormAdmin");
            if (branchNormAdmins.Count == 0)
            {
                await LogWorkflowNoAssigneeAsync(card, "NormAdmin", $"Проверка ХК {card.Code}", ct);
                await _db.SaveChangesAsync(ct);
                return (false, "Невозможно отправить ХК на проверку: в филиале не назначен нормативный администратор.");
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
                await ApplyWorkflowTasksAsync(card, newStatus, actorId, comment, ct);
                if (newStatus == HKCardStatus.Approved)
                {
                    if (card.SupersedesHKCardId.HasValue)
                    {
                        if (!await ArchiveSupersededCardAsync(card, actorId, ct))
                            return (false, "Не удалось заархивировать заменяемую ХК. Проверьте статус и связь версий.");
                    }
                    else
                    {
                        await ArchivePreviousApprovedVersionsAsync(card, actorId, ct);
                    }
                }
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

        return (true, null);
    }

    public async Task<bool> ArchiveExpiredAsync(Guid cardId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var card = await _db.HKCards
            .FirstOrDefaultAsync(c => c.Id == cardId && c.Status == HKCardStatus.Approved, ct);
        if (card == null)
            return false;

        var oldStatus = card.Status;
        card.Status = HKCardStatus.Archived;
        card.UpdatedAt = now;

        _db.HKCardStatusLogs.Add(new HKCardStatusLog
        {
            Id = Guid.NewGuid(),
            HKCardId = card.Id,
            FromStatus = oldStatus,
            ToStatus = HKCardStatus.Archived,
            ChangedByUserId = Guid.Empty,
            Comment = "Автоматическое архивирование по истечении срока действия",
            ChangedAt = now
        });

        await CloseOpenWorkflowTasksAsync(WorkTaskType.HKExpirationReview, "HKCard", card.Id, cancelled: true, Guid.Empty, ct);
        await CloseOpenWorkflowTasksAsync(WorkTaskType.HKReview, "HKCard", card.Id, cancelled: true, Guid.Empty, ct);

        await _audit.CreateLogAsync(new AuditWriteRequest(
            EntityType: "HKCard",
            EntityId: card.Id.ToString(),
            Action: "HK.ExpiredArchived",
            ActorUserId: Guid.Empty,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: $"ХК {card.Code} (v{card.Version}) автоматически переведена в архив по истечении срока действия."), ct);

        return true;
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(Guid id, string reason, CancellationToken ct = default)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reason))
            return (false, "Укажите причину удаления ХК.");

        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null)
            return (false, "Пользователь не найден");

        var card = await _db.HKCards.FindAsync(id, ct);
        if (card == null || card.Status == HKCardStatus.Deleted)
            return (false, "ХК не найдена или уже удалена.");

        if (!HKCardStatusTransitions.IsAllowed(card.Status, HKCardStatus.Deleted))
            return (false, $"Удаление ХК из статуса «{card.Status}» не предусмотрено.");

        var requiredPermission = card.Status switch
        {
            HKCardStatus.Draft => PermissionCodes.HKDeleteDraft,
            HKCardStatus.OnReview => PermissionCodes.HKDeleteOnReview,
            HKCardStatus.RevisionRequired => PermissionCodes.HKDeleteRevisionRequired,
            _ => null
        };

        if (requiredPermission == null || !await _permissions.HasPermissionAsync(actorId.ToString(), requiredPermission))
            return (false, "Недостаточно прав для удаления ХК в этом статусе.");

        var isSystemAdmin = await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig);
        if (!isSystemAdmin && actor.BranchId != card.BranchId)
            return (false, "Нельзя удалить ХК другого филиала.");

        return await ChangeStatusAsync(id, HKCardStatus.Deleted, reason, ct);
    }

    public async Task<(bool Success, Guid? NewCardId, string? Error)> CreateNewVersionAsync(Guid sourceCardId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null)
            return (false, null, "Пользователь не найден.");

        var source = await _db.HKCards
            .AsSplitQuery()
            .Include(x => x.Items).ThenInclude(i => i.Materials)
            .Include(x => x.ParentComponents)
            .Include(x => x.MilitaryBranches)
            .FirstOrDefaultAsync(x => x.Id == sourceCardId, ct);

        if (source == null || source.Status == HKCardStatus.Deleted)
            return (false, null, "Исходная ХК не найдена или удалена.");

        if (source.Status is not (HKCardStatus.Approved or HKCardStatus.Archived))
            return (false, null, "Новую версию можно создать только из утверждённой или архивной ХК.");

        var isSystemAdmin = await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig);
        if (!isSystemAdmin && actor.BranchId != source.BranchId)
            return (false, null, "Нельзя создать версию для ХК другого филиала.");

        var createPerm = source.ObjectLevel switch
        {
            Domain.Enums.HKObjectLevel.Node => PermissionCodes.HKNodeCreate,
            Domain.Enums.HKObjectLevel.Aggregate => PermissionCodes.HKAggregateCreate,
            Domain.Enums.HKObjectLevel.EquipmentModel => PermissionCodes.HKEquipmentCreate,
            Domain.Enums.HKObjectLevel.Complex => PermissionCodes.HKComplexCreate,
            _ => null
        };
        if (createPerm == null || !await _permissions.HasPermissionAsync(actorId.ToString(), createPerm))
            return (false, null, "Недостаточно прав для создания новой версии ХК.");

        var now = _time.GetUtcNow().UtcDateTime;
        var newCard = new HKCard
        {
            Id = Guid.NewGuid(),
            Status = HKCardStatus.Draft,
            SupersedesHKCardId = source.Id,
            ObjectLevel = source.ObjectLevel,
            ComplexId = source.ComplexId,
            EquipmentModelId = source.EquipmentModelId,
            AggregateId = source.AggregateId,
            NodeId = source.NodeId,
            BranchId = source.BranchId,
            AuthorId = actorId,
            CreatedAt = now,
            UpdatedAt = now,
            Code = source.Code,
            Version = await GenerateUniqueVersionAsync(source.Code, ct),
            Purpose = source.Purpose,
            NormativeBasis = source.NormativeBasis,
            Notes = source.Notes,
            RequestOrganization = source.RequestOrganization,
            RequestSenderFullName = source.RequestSenderFullName,
            RequestReceivedDate = source.RequestReceivedDate,
            RequestDetails = source.RequestDetails,
            IncomingLetterNumber = source.IncomingLetterNumber,
            OutgoingLetterNumber = source.OutgoingLetterNumber,
            EffectiveDate = source.EffectiveDate,
            ExpirationDate = source.ExpirationDate,
        };

        await EnsureNoActiveDuplicateAsync(newCard, ct);

        foreach (var sourceItem in source.Items.OrderBy(i => i.SortOrder))
        {
            var newItem = new HKCardItem
            {
                Id = Guid.NewGuid(),
                HKCardId = newCard.Id,
                AssemblyUnitId = sourceItem.AssemblyUnitId,
                Quantity = sourceItem.Quantity,
                Volume = sourceItem.Volume,
                UnitOfMeasure = sourceItem.UnitOfMeasure,
                Periodicity = sourceItem.Periodicity,
                Notes = sourceItem.Notes,
                SortOrder = sourceItem.SortOrder,
                Materials = sourceItem.Materials.Select(m => new HKCardItemMaterial
                {
                    Id = Guid.NewGuid(),
                    GsmMaterialId = m.GsmMaterialId,
                    Category = m.Category
                }).ToList()
            };
            newCard.Items.Add(newItem);
        }

        foreach (var sourceComponent in source.ParentComponents.OrderBy(c => c.SortOrder))
        {
            newCard.ParentComponents.Add(new HKCardComponent
            {
                Id = Guid.NewGuid(),
                ParentHKCardId = newCard.Id,
                ChildHKCardId = sourceComponent.ChildHKCardId,
                SortOrder = sourceComponent.SortOrder,
                AddedAt = sourceComponent.AddedAt,
                AddedByUserId = sourceComponent.AddedByUserId,
                ChildCode = sourceComponent.ChildCode,
                ChildVersion = sourceComponent.ChildVersion,
                ChildApprovedAt = sourceComponent.ChildApprovedAt
            });
        }

        foreach (var sourceMb in source.MilitaryBranches)
        {
            newCard.MilitaryBranches.Add(new HKCardMilitaryBranch
            {
                HKCardId = newCard.Id,
                MilitaryBranchId = sourceMb.MilitaryBranchId
            });
        }

        _db.HKCards.Add(newCard);

        _db.HKCardStatusLogs.Add(new HKCardStatusLog
        {
            Id = Guid.NewGuid(),
            HKCardId = newCard.Id,
            FromStatus = HKCardStatus.Draft,
            ToStatus = HKCardStatus.Draft,
            ChangedByUserId = actorId,
            Comment = $"Создана новая версия на основе {source.Code} {source.Version}",
            ChangedAt = now
        });

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "HKCard",
            newCard.Id.ToString(),
            "HKCard.NewVersionCreated",
            actorId,
            EntityDisplayName: $"{newCard.Code} v{newCard.Version}",
            Details: $"Создана новая версия ХК на основе {source.Code} {source.Version} (Id {source.Id}). Новая карта: {newCard.Code} {newCard.Version} (Id {newCard.Id})."), ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("Не удалось создать новую версию: нарушено ограничение уникальности.");
        }

        return (true, newCard.Id, null);
    }

    public async Task<(bool Success, string? Error)> ArchiveAsync(Guid id, Guid replacementCardId, string reason, CancellationToken ct = default)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reason))
            return (false, "Укажите причину архивирования.");

        if (id == replacementCardId)
            return (false, "Заменяющая ХК не может совпадать с архивируемой.");

        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.HKArchive, ct);

        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor == null)
            return (false, "Пользователь не найден.");

        var card = await _db.HKCards.FindAsync(id, ct);
        if (card == null || card.Status == HKCardStatus.Deleted)
            return (false, "ХК не найдена или удалена.");

        if (card.Status != HKCardStatus.Approved)
            return (false, "Ручное архивирование доступно только для утверждённой ХК.");

        var isSystemAdmin = await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig);
        if (!isSystemAdmin && actor.BranchId != card.BranchId)
            return (false, "Нельзя архивировать ХК другого филиала.");

        var replacement = await _db.HKCards.FindAsync(replacementCardId, ct);
        if (replacement == null || replacement.Status == HKCardStatus.Deleted)
            return (false, "Заменяющая ХК не найдена или удалена.");

        if (replacement.Status != HKCardStatus.Approved)
            return (false, "Заменяющая ХК должна быть утверждена.");

        if (replacement.BranchId != card.BranchId)
            return (false, "Заменяющая ХК должна принадлежать тому же филиалу.");

        if (replacement.ObjectLevel != card.ObjectLevel ||
            replacement.ComplexId != card.ComplexId ||
            replacement.EquipmentModelId != card.EquipmentModelId ||
            replacement.AggregateId != card.AggregateId ||
            replacement.NodeId != card.NodeId)
        {
            return (false, "Заменяющая ХК должна относиться к тому же нормативному объекту.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (replacement.EffectiveDate.HasValue && replacement.EffectiveDate.Value > now)
            return (false, "Заменяющая ХК ещё не вступила в силу.");
        if (replacement.ExpirationDate.HasValue && replacement.ExpirationDate.Value < now)
            return (false, "Срок действия заменяющей ХК истёк.");

        ArchiveCardInternal(card, replacement, actorId, reason, now);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
        }

        return (true, null);
    }

    private async Task<string> GenerateUniqueVersionAsync(string code, CancellationToken ct)
    {
        var baseVersion = "v" + _time.GetUtcNow().UtcDateTime.ToString("MMyy");
        var existing = await _db.HKCards
            .IgnoreQueryFilters()
            .Where(c => c.Code == code && c.Version.StartsWith(baseVersion))
            .Select(c => c.Version)
            .ToListAsync(ct);

        if (existing.Count == 0)
            return baseVersion;

        var maxSuffix = existing
            .Select(v =>
            {
                var suffix = v.Length > baseVersion.Length + 1 ? v[(baseVersion.Length + 1)..] : string.Empty;
                return int.TryParse(suffix, out var n) ? n : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        return maxSuffix == 0 ? $"{baseVersion}.2" : $"{baseVersion}.{maxSuffix + 1}";
    }

    private void ArchiveCardInternal(HKCard card, HKCard replacement, Guid actorUserId, string reason, DateTime now)
    {
        var oldStatus = card.Status;
        card.Status = HKCardStatus.Archived;
        card.UpdatedAt = now;

        _db.HKCardStatusLogs.Add(new HKCardStatusLog
        {
            Id = Guid.NewGuid(),
            HKCardId = card.Id,
            FromStatus = oldStatus,
            ToStatus = HKCardStatus.Archived,
            ChangedByUserId = actorUserId,
            Comment = reason,
            ChangedAt = now
        });

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCard",
            EntityId = card.Id.ToString(),
            Action = $"Status:{HKCardStatus.Archived}",
            UserId = actorUserId,
            CreatedAt = now,
            Details = $"{reason} Заменяющая карта: {replacement.Code} {replacement.Version}."
        });
    }

    private async Task<bool> ArchiveSupersededCardAsync(HKCard newCard, Guid actorUserId, CancellationToken ct)
    {
        if (!newCard.SupersedesHKCardId.HasValue)
            return true;

        var superseded = await _db.HKCards
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == newCard.SupersedesHKCardId.Value, ct);

        if (superseded == null || superseded.Status == HKCardStatus.Deleted)
            return false;

        if (superseded.ObjectLevel != newCard.ObjectLevel ||
            superseded.ComplexId != newCard.ComplexId ||
            superseded.EquipmentModelId != newCard.EquipmentModelId ||
            superseded.AggregateId != newCard.AggregateId ||
            superseded.NodeId != newCard.NodeId ||
            superseded.BranchId != newCard.BranchId)
        {
            return false;
        }

        if (superseded.Status == HKCardStatus.Archived)
            return true;

        if (superseded.Status != HKCardStatus.Approved)
            return false;

        var now = _time.GetUtcNow().UtcDateTime;
        var reason = $"Заменена утверждённой ХК {newCard.Code}, версия {newCard.Version}";
        ArchiveCardInternal(superseded, newCard, actorUserId, reason, now);
        return true;
    }

    private async Task ApplyWorkflowTasksAsync(HKCard card, HKCardStatus to, Guid actorUserId, string? comment, CancellationToken ct)
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
                await CloseOpenWorkflowTasksAsync(
                    WorkTaskType.HKRevision,
                    "HKCard",
                    card.Id,
                    cancelled: false,
                    actorUserId,
                    ct,
                    completionComment: "ХК повторно отправлена на проверку");
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
                    await _notifications.CreateFromWorkflowAsync(card.AuthorId.Value.ToString(), new CreateNotificationCommand(
                        Type: NotificationType.HKReturnedForRevision,
                        Title: $"ХК {card.Code} возвращена на доработку",
                        Message: string.IsNullOrWhiteSpace(comment) ? null : comment,
                        EntityType: "HKCard",
                        EntityId: card.Id,
                        NavigationUrl: $"/хк/{card.Id}",
                        BranchId: card.BranchId), actorUserId, ct);
                }
                break;

            case HKCardStatus.Approved:
                await CloseOpenWorkflowTasksAsync(WorkTaskType.HKReview, "HKCard", card.Id, cancelled: false, actorUserId, ct);
                await CreateRecalculationTasksAsync(card, actorUserId, ct);
                if (card.AuthorId.HasValue)
                {
                    await _notifications.CreateFromWorkflowAsync(card.AuthorId.Value.ToString(), new CreateNotificationCommand(
                        Type: NotificationType.HKApproved,
                        Title: $"ХК {card.Code} утверждена",
                        Message: comment,
                        EntityType: "HKCard",
                        EntityId: card.Id,
                        NavigationUrl: $"/хк/{card.Id}",
                        BranchId: card.BranchId), actorUserId, ct);
                }
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
            resolvedAssignee = users.Count > 0 ? users[0] : null;
        }
        if (string.IsNullOrWhiteSpace(resolvedAssignee))
        {
            await LogWorkflowNoAssigneeAsync(card, role ?? "исполнитель", title, ct);
            return;
        }

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
            DueDateUtc: dueDays.HasValue ? _time.GetUtcNow().UtcDateTime.AddDays(dueDays.Value) : null,
            NotifyAssignee: true), ct: ct);
    }

    private async Task CloseOpenWorkflowTasksAsync(
        WorkTaskType type, string entityType, Guid entityId, bool cancelled, Guid actorUserId, CancellationToken ct,
        string? completionComment = null)
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
                var finalComment = completionComment ?? "ХК утверждена";
                task.Status = WorkTaskStatus.Completed;
                task.CompletedAtUtc = now;
                task.CompletedByUserId = actorUserId.ToString();
                task.CompletionComment = finalComment;
                await _audit.CreateLogAsync(
                    new AuditWriteRequest(
                        EntityType: "WorkTask",
                        EntityId: task.Id.ToString(),
                        Action: "Task.Completed",
                        ActorUserId: actorUserId,
                        EntityDisplayName: task.Title,
                        Details: finalComment),
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
        var assignee = operators.Count > 0 ? operators[0] : null;
        if (assignee == null)
        {
            await LogWorkflowNoAssigneeAsync(card, "Operator", "Пересчёт инд. карт — экземпляр", ct);
            return;
        }

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
                DueDateUtc: _time.GetUtcNow().UtcDateTime.AddDays(14),
                NotifyAssignee: true), ct: ct);
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
    }

    public async Task<List<string>> GetBranchUsersInRoleAsync(Guid branchId, string role)
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
                if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKDeleteDraft)
                    && !await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKDeleteOnReview)
                    && !await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKDeleteRevisionRequired))
                {
                    return "Недостаточно прав для удаления ХК.";
                }
                if (!HKCardStatusTransitions.CanDelete(card.Status))
                    return "Нельзя удалить карточку в текущем статусе.";
                break;
        }

        return null;
    }

    public async Task<ReferenceProposal> CreateProposalAsync(
        Guid hkCardId, ProposalTargetType targetType,
        string code, string name, string? description, string? gost, string? type,
        CancellationToken ct = default)
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
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "ReferenceProposal",
                EntityId: proposal.Id.ToString(),
                Action: "Created",
                ActorUserId: actorId,
                EntityDisplayName: $"Предложение для {card.Code}: {name}"),
            ct);

        var reviewerId = await PickBranchReviewerAsync(card.BranchId, "NormAdmin", actorId.ToString(), ct);
        if (reviewerId == null)
        {
            await HandleProposalWithoutReviewerAsync(card, proposal, actorId, name, ct);
        }
        else
        {
            var task = await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
                Title: $"Проверка предложения справочника: {name}",
                Type: WorkTaskType.ReferenceProposalReview,
                Priority: WorkTaskPriority.Normal,
                Description: $"Предложение создано для ХК {card.Code} (v{card.Version}). Требуется проверка и принятие/отклонение.",
                AssignedToUserId: reviewerId,
                BranchId: card.BranchId,
                EntityType: "ReferenceProposal",
                EntityId: proposal.Id,
                EntityCodeSnapshot: card.Code,
                EntityTitleSnapshot: name,
                DueDateUtc: _time.GetUtcNow().UtcDateTime.AddDays(7),
                NotifyAssignee: false), ct: ct);

            await _notifications.CreateFromWorkflowAsync(reviewerId, new CreateNotificationCommand(
                Type: NotificationType.ReferenceProposalPending,
                Title: $"Новое предложение справочника: {name}",
                Message: card.Code,
                EntityType: "ReferenceProposal",
                EntityId: proposal.Id,
                WorkTaskId: task.Id,
                NavigationUrl: $"/хк/{hkCardId}",
                BranchId: card.BranchId,
                DeduplicationKey: $"ref-proposal:{proposal.Id}:{reviewerId}"), actorId, ct);
        }

        await _db.SaveChangesAsync(ct);
        return proposal;
    }

    private async Task<string?> PickBranchReviewerAsync(
        Guid branchId, string role, string? excludeUserId, CancellationToken ct)
    {
        var reviewers = (await GetBranchUsersInRoleAsync(branchId, role))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return reviewers.FirstOrDefault(r => !string.Equals(r, excludeUserId, StringComparison.Ordinal));
    }

    private async Task HandleProposalWithoutReviewerAsync(
        HKCard card, ReferenceProposal proposal, Guid actorUserId, string name, CancellationToken ct)
    {
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "ReferenceProposal",
                EntityId: proposal.Id.ToString(),
                Action: "ReferenceProposal.NoNormAdmin",
                ActorUserId: actorUserId,
                EntityDisplayName: $"Предложение для {card.Code}: {name}",
                Details: $"В филиале карты {card.Code} нет активного NormAdmin для проверки предложения «{name}»."),
            ct);

        var systemAdminId = await GetAnyUserInRoleAsync("SystemAdmin");
        if (systemAdminId == null)
            return;

        await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
            Title: $"Нет NormAdmin для проверки предложения: {name}",
            Type: WorkTaskType.UserAdministration,
            Priority: WorkTaskPriority.Normal,
            Description: $"В филиале карты {card.Code} (v{card.Version}) нет активного NormAdmin. Предложение справочника «{name}» некому проверять. Назначьте NormAdmin в филиал.",
            AssignedToUserId: systemAdminId,
            BranchId: card.BranchId,
            EntityType: "ReferenceProposal",
            EntityId: proposal.Id,
            EntityCodeSnapshot: card.Code,
            EntityTitleSnapshot: name,
            NotifyAssignee: true), ct: ct);
    }

    private async Task LogWorkflowNoAssigneeAsync(HKCard card, string role, string taskTitle, CancellationToken ct)
    {
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "HKCard",
                EntityId: card.Id.ToString(),
                Action: "Workflow.NoAssignee",
                ActorUserId: Guid.Empty,
                EntityDisplayName: $"{card.Code} v{card.Version}",
                Details: $"Нет активного пользователя с ролью {role} в филиале для задачи «{taskTitle}»."),
            ct);
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