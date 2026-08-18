using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class GsmMaterialService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _time;
    private readonly IPermissionService _permissions;

    public GsmMaterialService(
        AppDbContext db,
        AuditService audit,
        ICurrentUserService currentUser,
        TimeProvider time,
        IPermissionService permissions)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
        _time = time;
        _permissions = permissions;
    }

    public async Task<PagedResult<GsmMaterial>> GetPagedAsync(GsmMaterialQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<GsmMaterial> q = _db.GsmMaterials.Where(m => !m.IsDraft);

        if (query.ShowDeleted == null)
        {
            q = q.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            q = q.IgnoreQueryFilters().Where(m => m.IsDeleted);
        }
        else
        {
            q = q.Where(m => !m.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(m => EF.Functions.ILike(m.Name, $"%{term}%")
                || EF.Functions.ILike(m.Type, $"%{term}%")
                || EF.Functions.ILike(m.Gost ?? "", $"%{term}%"));
        }

        var totalCount = await q.CountAsync(ct);

        IOrderedQueryable<GsmMaterial> ordered = query.SortBy switch
        {
            "Type" => query.SortDescending
                ? q.OrderByDescending(m => m.Type).ThenByDescending(m => m.Name)
                : q.OrderBy(m => m.Type).ThenBy(m => m.Name),
            "Gost" => query.SortDescending
                ? q.OrderByDescending(m => m.Gost).ThenByDescending(m => m.Name)
                : q.OrderBy(m => m.Gost).ThenBy(m => m.Name),
            _ => query.SortDescending
                ? q.OrderByDescending(m => m.Name).ThenByDescending(m => m.Type)
                : q.OrderBy(m => m.Name).ThenBy(m => m.Type),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<GsmMaterial>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<GsmMaterial?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);
        return await _db.GsmMaterials.FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<List<GsmMaterial>> GetActiveForSelectionAsync(string? searchText = null, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<GsmMaterial> q = _db.GsmMaterials
            .Where(m => !m.IsDraft && !m.IsDeleted);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var term = searchText.Trim();
            q = q.Where(m => EF.Functions.ILike(m.Name, $"%{term}%")
                || EF.Functions.ILike(m.Type, $"%{term}%")
                || EF.Functions.ILike(m.Gost ?? "", $"%{term}%"));
        }

        return await q
            .OrderBy(m => m.Name)
            .ThenBy(m => m.Type)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<GsmMaterial> CreateAsync(GsmMaterial material, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        Validate(material);

        material.Id = Guid.NewGuid();
        material.IsDraft = false;
        material.IsDeleted = false;
        material.DeletedAt = null;
        _db.GsmMaterials.Add(material);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "GsmMaterial",
            material.Id.ToString(),
            "Create",
            _currentUser.GetRequiredUserId(),
            EntityDisplayName: FormatDisplayName(material)), ct);

        return material;
    }

    public async Task<bool> UpdateAsync(GsmMaterial material, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        Validate(material);

        var existing = await _db.GsmMaterials
            .FirstOrDefaultAsync(m => m.Id == material.Id && !m.IsDeleted, ct);
        if (existing == null) return false;

        existing.Name = material.Name;
        existing.Type = material.Type;
        existing.Gost = material.Gost;
        existing.Description = material.Description;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "GsmMaterial",
            existing.Id.ToString(),
            "Update",
            _currentUser.GetRequiredUserId(),
            EntityDisplayName: FormatDisplayName(existing)), ct);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var material = await _db.GsmMaterials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (material == null || material.IsDeleted) return false;

        material.IsDeleted = true;
        material.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "GsmMaterial",
            id.ToString(),
            "Delete",
            _currentUser.GetRequiredUserId(),
            EntityDisplayName: FormatDisplayName(material)), ct);

        return true;
    }

    public async Task<bool> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var material = await _db.GsmMaterials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (material == null || !material.IsDeleted) return false;

        material.IsDeleted = false;
        material.DeletedAt = null;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "GsmMaterial",
            id.ToString(),
            "Restore",
            _currentUser.GetRequiredUserId(),
            EntityDisplayName: FormatDisplayName(material)), ct);

        return true;
    }

    private static void Validate(GsmMaterial material)
    {
        material.Name = material.Name?.Trim() ?? "";
        material.Type = material.Type?.Trim() ?? "";
        material.Gost = string.IsNullOrWhiteSpace(material.Gost) ? null : material.Gost.Trim();
        material.Description = string.IsNullOrWhiteSpace(material.Description) ? null : material.Description.Trim();

        if (string.IsNullOrWhiteSpace(material.Name))
            throw new InvalidOperationException("Укажите наименование.");
        if (string.IsNullOrWhiteSpace(material.Type))
            throw new InvalidOperationException("Укажите тип.");
        if (material.Name.Length > 250)
            throw new InvalidOperationException("Наименование должно быть не длиннее 250 символов.");
        if (material.Type.Length > 250)
            throw new InvalidOperationException("Тип должен быть не длиннее 250 символов.");
        if (material.Gost?.Length > 250)
            throw new InvalidOperationException("ГОСТ должен быть не длиннее 250 символов.");
        if (material.Description?.Length > 2000)
            throw new InvalidOperationException("Описание должно быть не длиннее 2000 символов.");
    }

    private static string FormatDisplayName(GsmMaterial material) =>
        string.IsNullOrWhiteSpace(material.Gost)
            ? material.Name
            : $"{material.Name} — {material.Gost}";
}
