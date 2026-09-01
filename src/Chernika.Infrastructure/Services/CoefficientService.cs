using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class CoefficientService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _time;
    private readonly IPermissionService _permissions;

    public CoefficientService(AppDbContext db, AuditService audit, ICurrentUserService currentUser, TimeProvider time, IPermissionService permissions)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
        _time = time;
        _permissions = permissions;
    }

    private static string NormalizeTypeName(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException("Укажите наименование типа коэффициента.");
        return trimmed;
    }

    private static string NormalizeTypeKey(string value) =>
        NormalizeTypeName(value).ToUpperInvariant();

    public async Task<PagedResult<CoefficientTypeListItemDto>> GetCoefficientTypesAsync(
        CoefficientTypeListQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var baseQuery = _db.CoefficientTypes.AsNoTracking();

        if (query.StatusFilter == ReferenceStatusFilter.All)
        {
            baseQuery = baseQuery.IgnoreQueryFilters();
        }
        else if (query.StatusFilter == ReferenceStatusFilter.Archived)
        {
            baseQuery = baseQuery.IgnoreQueryFilters().Where(t => t.IsDeleted);
        }
        else
        {
            baseQuery = baseQuery.Where(t => !t.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim().ToLowerInvariant();
            baseQuery = baseQuery.Where(t => EF.Functions.Like(t.Name.ToLower(), $"%{term}%"));
        }

        var totalCount = await baseQuery.CountAsync(ct);

        IOrderedQueryable<CoefficientType> ordered = query.SortBy switch
        {
            "name" => query.SortDescending
                ? baseQuery.OrderByDescending(t => t.Name).ThenByDescending(t => t.SortOrder)
                : baseQuery.OrderBy(t => t.Name).ThenBy(t => t.SortOrder),
            "status" => query.SortDescending
                ? baseQuery.OrderByDescending(t => t.IsDeleted).ThenBy(t => t.SortOrder)
                : baseQuery.OrderBy(t => t.IsDeleted).ThenBy(t => t.SortOrder),
            _ => query.SortDescending
                ? baseQuery.OrderByDescending(t => t.SortOrder).ThenByDescending(t => t.Name)
                : baseQuery.OrderBy(t => t.SortOrder).ThenBy(t => t.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new CoefficientTypeListItemDto(
                t.Id,
                t.Name,
                t.SortOrder,
                t.Coefficients.Count(c => !c.IsDeleted),
                t.IsDeleted,
                t.CreatedAt,
                t.UpdatedAt,
                t.DeletedAt))
            .ToListAsync(ct);

        return new PagedResult<CoefficientTypeListItemDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<IReadOnlyList<CoefficientTypeListItemDto>> GetActiveCoefficientTypesForSelectAsync(
        CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        return await _db.CoefficientTypes.AsNoTracking()
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => new CoefficientTypeListItemDto(
                t.Id,
                t.Name,
                t.SortOrder,
                t.Coefficients.Count(c => !c.IsDeleted),
                t.IsDeleted,
                t.CreatedAt,
                t.UpdatedAt,
                t.DeletedAt))
            .ToListAsync(ct);
    }

    public async Task<CoefficientTypeListItemDto?> GetCoefficientTypeByIdAsync(
        Guid id, bool includeArchived = false, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var query = _db.CoefficientTypes.AsNoTracking();
        if (!includeArchived)
            query = query.Where(t => !t.IsDeleted);
        else
            query = query.IgnoreQueryFilters();

        return await query
            .Where(t => t.Id == id)
            .Select(t => new CoefficientTypeListItemDto(
                t.Id,
                t.Name,
                t.SortOrder,
                t.Coefficients.Count(c => !c.IsDeleted),
                t.IsDeleted,
                t.CreatedAt,
                t.UpdatedAt,
                t.DeletedAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CoefficientTypeListItemDto> CreateCoefficientTypeAsync(
        CreateCoefficientTypeRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var name = NormalizeTypeName(request.Name);
        await EnsureNameUniqueAsync(name, null, ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var sortOrder = request.SortOrder ?? (await GetMaxSortOrderAsync(ct)) + 10;

        var type = new CoefficientType
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = sortOrder,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.CoefficientTypes.Add(type);
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "CoefficientType", type.Id.ToString(), "CoefficientType.Created",
            _currentUser.GetRequiredUserId(), EntityDisplayName: type.Name), ct);
        await _db.SaveChangesAsync(ct);

        return new CoefficientTypeListItemDto(
            type.Id, type.Name, type.SortOrder, 0, false, type.CreatedAt, type.UpdatedAt, null);
    }

    public async Task<CoefficientTypeListItemDto> UpdateCoefficientTypeAsync(
        UpdateCoefficientTypeRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var type = await _db.CoefficientTypes.FirstOrDefaultAsync(t => t.Id == request.Id, ct)
            ?? throw new InvalidOperationException("Тип коэффициента не найден. Обновите список и повторите попытку.");

        var name = NormalizeTypeName(request.Name);
        await EnsureNameUniqueAsync(name, type.Id, ct);

        type.Name = name;
        type.SortOrder = request.SortOrder;
        type.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "CoefficientType", type.Id.ToString(), "CoefficientType.Updated",
            _currentUser.GetRequiredUserId(), EntityDisplayName: type.Name), ct);
        await _db.SaveChangesAsync(ct);

        return await GetTypeListItemAsync(type.Id, ct);
    }

    public async Task ArchiveCoefficientTypeAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var type = await _db.CoefficientTypes
            .Include(t => t.Coefficients)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException("Тип коэффициента не найден. Обновите список и повторите попытку.");

        if (type.IsDeleted)
            throw new InvalidOperationException("Тип коэффициента уже архивирован.");

        var workingCoefficients = type.Coefficients.Count(c => !c.IsDeleted);
        if (workingCoefficients > 0)
            throw new InvalidOperationException(
                $"Нельзя архивировать тип «{type.Name}». " +
                $"В типе содержится {workingCoefficients} рабочих коэффициентов. " +
                "Сначала архивируйте коэффициенты или перенесите их в другой тип.");

        type.IsDeleted = true;
        type.DeletedAt = _time.GetUtcNow().UtcDateTime;
        type.UpdatedAt = type.DeletedAt.Value;

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "CoefficientType", type.Id.ToString(), "CoefficientType.Archived",
            _currentUser.GetRequiredUserId(), EntityDisplayName: type.Name), ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RestoreCoefficientTypeAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var type = await _db.CoefficientTypes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException("Тип коэффициента не найден.");

        if (!type.IsDeleted)
            throw new InvalidOperationException("Тип коэффициента не является архивированным.");

        await EnsureNameUniqueAsync(type.Name, type.Id, ct);

        type.IsDeleted = false;
        type.DeletedAt = null;
        type.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "CoefficientType", type.Id.ToString(), "CoefficientType.Restored",
            _currentUser.GetRequiredUserId(), EntityDisplayName: type.Name), ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task EnsureNameUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var key = NormalizeTypeKey(name);
        var conflict = await _db.CoefficientTypes.IgnoreQueryFilters()
            .Where(t => !t.IsDeleted && t.Id != excludeId)
            .ToListAsync(ct);

        if (conflict.Any(t => NormalizeTypeKey(t.Name) == key))
            throw new InvalidOperationException($"Тип коэффициента «{name}» уже существует.");
    }

    private async Task<int> GetMaxSortOrderAsync(CancellationToken ct)
    {
        return await _db.CoefficientTypes.Where(t => !t.IsDeleted).MaxAsync(t => (int?)t.SortOrder) ?? 0;
    }

    private async Task<CoefficientTypeListItemDto> GetTypeListItemAsync(Guid id, CancellationToken ct)
    {
        return await _db.CoefficientTypes.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new CoefficientTypeListItemDto(
                t.Id,
                t.Name,
                t.SortOrder,
                t.Coefficients.Count(c => !c.IsDeleted),
                t.IsDeleted,
                t.CreatedAt,
                t.UpdatedAt,
                t.DeletedAt))
            .FirstAsync(ct);
    }
}
