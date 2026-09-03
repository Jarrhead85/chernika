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

    // ── Coefficient CRUD (C2) ─────────────────────────────────────────────

    private static string NormalizeCoefficientName(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException("Укажите наименование коэффициента.");
        return trimmed;
    }

    private static string NormalizeCoefficientKey(string value) =>
        NormalizeCoefficientName(value).ToUpperInvariant();

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private static IQueryable<Coefficient> ApplyCoefficientStatusFilter(
        IQueryable<Coefficient> query, ReferenceStatusFilter filter)
    {
        var filtered = filter switch
        {
            ReferenceStatusFilter.All => query.IgnoreQueryFilters(),
            ReferenceStatusFilter.Archived => query.IgnoreQueryFilters().Where(c => c.IsDeleted),
            _ => query.Where(c => !c.IsDeleted)
        };
        return filtered;
    }

    public async Task<PagedResult<CoefficientListItemDto>> GetCoefficientsAsync(
        CoefficientListQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<Coefficient> baseQuery = _db.Coefficients.AsNoTracking();

        baseQuery = ApplyCoefficientStatusFilter(baseQuery, query.StatusFilter);

        if (query.CoefficientTypeId.HasValue)
        {
            var typeId = query.CoefficientTypeId.Value;
            baseQuery = baseQuery.Where(c => c.CoefficientTypeId == typeId);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            baseQuery = baseQuery.Where(c =>
                EF.Functions.ILike(c.Name, $"%{term}%")
                || (c.ConditionDescription != null && EF.Functions.ILike(c.ConditionDescription, $"%{term}%"))
                || (c.NormativeBasis != null && EF.Functions.ILike(c.NormativeBasis, $"%{term}%"))
                || EF.Functions.ILike(c.CoefficientType.Name, $"%{term}%"));
        }

        if (query.HasNormativeBasis.HasValue)
        {
            if (query.HasNormativeBasis.Value)
                baseQuery = baseQuery.Where(c => c.NormativeBasis != null && c.NormativeBasis != "");
            else
                baseQuery = baseQuery.Where(c => c.NormativeBasis == null || c.NormativeBasis == "");
        }

        baseQuery = baseQuery.Include(c => c.CoefficientType);

        var totalCount = await baseQuery.CountAsync(ct);

        IOrderedQueryable<Coefficient> ordered = query.SortBy switch
        {
            "name" => query.SortDescending
                ? baseQuery.OrderByDescending(c => c.Name).ThenByDescending(c => c.CoefficientType.Name)
                : baseQuery.OrderBy(c => c.Name).ThenBy(c => c.CoefficientType.Name),
            "value" => query.SortDescending
                ? baseQuery.OrderByDescending(c => c.Value).ThenBy(c => c.Name)
                : baseQuery.OrderBy(c => c.Value).ThenBy(c => c.Name),
            "updated" => query.SortDescending
                ? baseQuery.OrderByDescending(c => c.UpdatedAt)
                : baseQuery.OrderBy(c => c.UpdatedAt),
            _ => query.SortDescending
                ? baseQuery.OrderByDescending(c => c.CoefficientType.Name).ThenByDescending(c => c.Name)
                : baseQuery.OrderBy(c => c.CoefficientType.Name).ThenBy(c => c.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CoefficientListItemDto { Id = c.Id, CoefficientTypeId = c.CoefficientTypeId, CoefficientTypeName = c.CoefficientType.Name, Name = c.Name, Value = c.Value, ConditionDescription = c.ConditionDescription, NormativeBasis = c.NormativeBasis, SortOrder = c.SortOrder, IsDeleted = c.IsDeleted, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt, DeletedAt = c.DeletedAt })
            .ToListAsync(ct);

        return new PagedResult<CoefficientListItemDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<IReadOnlyList<CoefficientListItemDto>> GetWorkingCoefficientsForSelectAsync(
        Guid? coefficientTypeId = null, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<Coefficient> query = _db.Coefficients.AsNoTracking()
            .Where(c => !c.IsDeleted && !c.CoefficientType.IsDeleted);

        if (coefficientTypeId.HasValue)
        {
            var typeId = coefficientTypeId.Value;
            query = query.Where(c => c.CoefficientTypeId == typeId);
        }

        query = query.Include(c => c.CoefficientType);

        return await query
            .OrderBy(c => c.CoefficientType.Name).ThenBy(c => c.Name)
            .Select(c => new CoefficientListItemDto { Id = c.Id, CoefficientTypeId = c.CoefficientTypeId, CoefficientTypeName = c.CoefficientType.Name, Name = c.Name, Value = c.Value, ConditionDescription = c.ConditionDescription, NormativeBasis = c.NormativeBasis, SortOrder = c.SortOrder, IsDeleted = c.IsDeleted, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt, DeletedAt = c.DeletedAt })
            .ToListAsync(ct);
    }

    public async Task<CoefficientListItemDto?> GetCoefficientByIdAsync(
        Guid id, bool includeArchived = false, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<Coefficient> query = _db.Coefficients.AsNoTracking();
        if (!includeArchived)
            query = query.Where(c => !c.IsDeleted);
        else
            query = query.IgnoreQueryFilters();

        query = query.Include(c => c.CoefficientType);

        return await query
            .Where(c => c.Id == id)
            .Select(c => new CoefficientListItemDto { Id = c.Id, CoefficientTypeId = c.CoefficientTypeId, CoefficientTypeName = c.CoefficientType.Name, Name = c.Name, Value = c.Value, ConditionDescription = c.ConditionDescription, NormativeBasis = c.NormativeBasis, SortOrder = c.SortOrder, IsDeleted = c.IsDeleted, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt, DeletedAt = c.DeletedAt })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CoefficientListItemDto> CreateCoefficientAsync(
        CreateCoefficientRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var name = NormalizeCoefficientName(request.Name);
        var condition = NormalizeOptional(request.ConditionDescription);
        var basis = NormalizeOptional(request.NormativeBasis);

        if (request.Value <= 0m)
            throw new InvalidOperationException("Значение коэффициента должно быть больше нуля.");

        var type = await _db.CoefficientTypes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.CoefficientTypeId, ct);
        if (type == null || type.IsDeleted)
            throw new InvalidOperationException("Тип коэффициента не найден или архивирован.");

        await EnsureCoefficientNameUniqueAsync(type.Id, name, null, ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var sortOrder = request.SortOrder
            ?? (await _db.Coefficients
                .Where(c => c.CoefficientTypeId == type.Id && !c.IsDeleted)
                .MaxAsync(c => (int?)c.SortOrder, ct)) + 10
            ?? 10;

        var coefficient = new Coefficient
        {
            Id = Guid.NewGuid(),
            CoefficientTypeId = type.Id,
            Name = name,
            Value = request.Value,
            ConditionDescription = condition,
            NormativeBasis = basis,
            SortOrder = sortOrder,
            IsDeleted = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Coefficients.Add(coefficient);
        var displayName = $"{type.Name}: {name}";
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "Coefficient", coefficient.Id.ToString(), "Coefficient.Created",
            _currentUser.GetRequiredUserId(), EntityDisplayName: displayName), ct);
        await _db.SaveChangesAsync(ct);

        return await GetCoefficientListItemAsync(coefficient.Id, ct);
    }

    public async Task<CoefficientListItemDto> UpdateCoefficientAsync(
        UpdateCoefficientRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var name = NormalizeCoefficientName(request.Name);
        var condition = NormalizeOptional(request.ConditionDescription);
        var basis = NormalizeOptional(request.NormativeBasis);

        if (request.Value <= 0m)
            throw new InvalidOperationException("Значение коэффициента должно быть больше нуля.");

        var coefficient = await _db.Coefficients.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.Id, ct)
            ?? throw new InvalidOperationException("Коэффициент не найден. Обновите список и повторите попытку.");

        if (coefficient.IsDeleted)
        {
            throw new InvalidOperationException(
                "Нельзя изменить архивированный коэффициент. Сначала восстановите его.");
        }

        var type = await _db.CoefficientTypes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.CoefficientTypeId, ct);
        if (type == null || type.IsDeleted)
            throw new InvalidOperationException("Тип коэффициента не найден или архивирован.");

        await EnsureCoefficientNameUniqueAsync(type.Id, name, coefficient.Id, ct);

        var now = _time.GetUtcNow().UtcDateTime;
        coefficient.CoefficientTypeId = type.Id;
        coefficient.Name = name;
        coefficient.Value = request.Value;
        coefficient.ConditionDescription = condition;
        coefficient.NormativeBasis = basis;
        coefficient.SortOrder = request.SortOrder;
        coefficient.UpdatedAt = now;

        var displayName = $"{type.Name}: {name}";
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "Coefficient", coefficient.Id.ToString(), "Coefficient.Updated",
            _currentUser.GetRequiredUserId(), EntityDisplayName: displayName), ct);
        await _db.SaveChangesAsync(ct);

        return await GetCoefficientListItemAsync(coefficient.Id, ct);
    }

    public async Task ArchiveCoefficientAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var coefficient = await _db.Coefficients.IgnoreQueryFilters()
            .Include(c => c.CoefficientType)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException("Коэффициент не найден. Обновите список и повторите попытку.");

        if (coefficient.IsDeleted)
            throw new InvalidOperationException("Коэффициент уже архивирован.");

        var now = _time.GetUtcNow().UtcDateTime;
        coefficient.IsDeleted = true;
        coefficient.DeletedAt = now;
        coefficient.UpdatedAt = now;

        var displayName = $"{coefficient.CoefficientType.Name}: {coefficient.Name}";
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "Coefficient", coefficient.Id.ToString(), "Coefficient.Archived",
            _currentUser.GetRequiredUserId(), EntityDisplayName: displayName), ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RestoreCoefficientAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var coefficient = await _db.Coefficients.IgnoreQueryFilters()
            .Include(c => c.CoefficientType)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException("Коэффициент не найден.");

        if (!coefficient.IsDeleted)
            throw new InvalidOperationException("Коэффициент не является архивированным.");

        if (coefficient.CoefficientType.IsDeleted)
            throw new InvalidOperationException(
                $"Невозможно восстановить коэффициент: тип «{coefficient.CoefficientType.Name}» архивирован.");

        var existingWorking = await _db.Coefficients.IgnoreQueryFilters()
            .Where(c => c.CoefficientTypeId == coefficient.CoefficientTypeId
                && !c.IsDeleted && c.Id != coefficient.Id)
            .ToListAsync(ct);

        var key = NormalizeCoefficientKey(coefficient.Name);
        if (existingWorking.Any(c => NormalizeCoefficientKey(c.Name) == key))
            throw new InvalidOperationException(
                $"Невозможно восстановить коэффициент: в типе «{coefficient.CoefficientType.Name}» уже есть рабочий коэффициент «{coefficient.Name}».");

        var now = _time.GetUtcNow().UtcDateTime;
        coefficient.IsDeleted = false;
        coefficient.DeletedAt = null;
        coefficient.UpdatedAt = now;

        var displayName = $"{coefficient.CoefficientType.Name}: {coefficient.Name}";
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "Coefficient", coefficient.Id.ToString(), "Coefficient.Restored",
            _currentUser.GetRequiredUserId(), EntityDisplayName: displayName), ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task EnsureCoefficientNameUniqueAsync(
        Guid typeId, string name, Guid? excludeId, CancellationToken ct)
    {
        var key = NormalizeCoefficientKey(name);
        var allWorking = await _db.Coefficients.IgnoreQueryFilters()
            .Where(c => c.CoefficientTypeId == typeId && !c.IsDeleted && c.Id != excludeId)
            .ToListAsync(ct);

        if (allWorking.Any(c => NormalizeCoefficientKey(c.Name) == key))
            throw new InvalidOperationException(
                $"Коэффициент «{name}» уже существует в типе «{(await _db.CoefficientTypes.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == typeId, ct))?.Name}».");
    }

    private async Task<CoefficientListItemDto> GetCoefficientListItemAsync(Guid id, CancellationToken ct)
    {
        return await _db.Coefficients.AsNoTracking()
            .Include(c => c.CoefficientType)
            .Where(c => c.Id == id)
            .Select(c => new CoefficientListItemDto { Id = c.Id, CoefficientTypeId = c.CoefficientTypeId, CoefficientTypeName = c.CoefficientType.Name, Name = c.Name, Value = c.Value, ConditionDescription = c.ConditionDescription, NormativeBasis = c.NormativeBasis, SortOrder = c.SortOrder, IsDeleted = c.IsDeleted, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt, DeletedAt = c.DeletedAt })
            .FirstAsync(ct);
    }
}
