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

        // Soft-delete используемой марки запрещён: глобальный фильтр скрыл бы её,
        // и строка ХК осталась бы с «невидимым» родителем. Проверяем ЛЮБЫЕ строки
        // независимо от статуса карты (Draft/Approved/Archived/Deleted) — берём
        // скалярный FK, чтобы query-фильтр HKCards не скрыл исторические строки.
        var usedInCards = await _db.HKCardItems
            .Where(i => i.Materials.Any(m => m.GsmMaterialId == id))
            .Select(i => i.HKCardId)
            .Distinct()
            .CountAsync(ct);
        if (usedInCards > 0)
            throw new InvalidOperationException(
                $"Нельзя удалить марку ГСМ: она используется в существующих ХК ({usedInCards}). " +
                "Сначала уберите марку из строк этих карт.");

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

    // ── Reconciliation Gost/Description → Nd/IntendedUse (переходный период) ──

    /// <summary>
    /// Отчёт о расхождениях между прежними полями и их переходными копиями.
    /// <para>
    /// Пока источником истины является <c>Gost</c>/<c>Description</c> (их пишут
    /// UI-сервис и API), <c>Nd</c>/<c>IntendedUse</c> не редактируются вовсе.
    /// Расхождение всегда означает «новое поле отстало». Проверяется в том числе
    /// правило бэкфилла <c>NULLIF(btrim(...), '')</c>, иначе «ГОСТ из одних
    /// пробелов» и <c>Nd = ""</c> сочлись бы разными значениями.
    /// </para>
    /// <para>
    /// Включает soft-deleted марки: их прежние поля тоже участвуют в переносе,
    /// а query-фильтр не должен скрывать расхождения от проверки.
    /// </para>
    /// </summary>
    public async Task<List<GsmTransitionDivergence>> GetTransitionDivergencesAsync(CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        // Отбор — надмножество расхождений: строка, где обе пары пусты, расходиться не может.
        var candidates = await _db.GsmMaterials.IgnoreQueryFilters()
            .Where(m => m.Nd != null || m.Gost != null || m.IntendedUse != null || m.Description != null)
            .AsNoTracking()
            .ToListAsync(ct);

        var result = new List<GsmTransitionDivergence>();
        foreach (var m in candidates)
        {
            var expectedNd = NormalizeLegacyText(m.Gost);
            var expectedIntendedUse = NormalizeLegacyText(m.Description);

            var ndDiffers = !string.Equals(m.Nd, expectedNd, StringComparison.Ordinal);
            var intendedUseDiffers = !string.Equals(m.IntendedUse, expectedIntendedUse, StringComparison.Ordinal);
            if (!ndDiffers && !intendedUseDiffers) continue;

            result.Add(new GsmTransitionDivergence
            {
                Id = m.Id,
                Name = m.Name,
                Type = m.Type,
                IsDeleted = m.IsDeleted,
                Gost = m.Gost,
                Nd = m.Nd,
                NdDiffers = ndDiffers,
                Description = m.Description,
                IntendedUse = m.IntendedUse,
                IntendedUseDiffers = intendedUseDiffers,
                ExpectedNd = expectedNd,
                ExpectedIntendedUse = expectedIntendedUse,
            });
        }

        return result;
    }

    /// <summary>
    /// Однократная сверка переходных полей: <c>Nd := NULLIF(btrim(Gost), '')</c> и
    /// <c>IntendedUse := Description</c> для всех расходящихся марок.
    /// <para>
    /// ВАЖНО: операция однонаправленная и допустима только пока источником истины
    /// остаются прежние поля. Повторять прежний бэкфилл вида
    /// <c>WHERE "Nd" IS NULL</c> бессмысленно — он не обновляет уже заполненное
    /// поле и не увидит расхождения. После переключения на <c>Nd</c> как на
    /// источник истины (PR-5) этот метод, наоборот, затёр бы новые значения
    /// старыми, поэтому он удаляется вместе с переходными полями.
    /// </para>
    /// <para>
    /// Флаг <paramref name="acknowledgeLegacyIsSourceOfTruth"/> — обязательный
    /// предохранитель от случайного вызова: пока новое поле не редактируется
    /// штатно, выигрывает последнее сохранённое прежнее значение.
    /// </para>
    /// </summary>
    public async Task<GsmTransitionReconciliation> ReconcileTransitionFieldsAsync(
        bool acknowledgeLegacyIsSourceOfTruth, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        if (!acknowledgeLegacyIsSourceOfTruth)
            throw new InvalidOperationException(
                "Сверка Nd/IntendedUse перезаписывает новые поля значениями прежних. " +
                "Подтвердите, что Nd/IntendedUse ещё не редактировались штатным UI.");

        var divergences = await GetTransitionDivergencesAsync(ct);
        if (divergences.Count == 0)
            return new GsmTransitionReconciliation();

        var ids = divergences.Select(d => d.Id).ToList();
        var materials = await _db.GsmMaterials.IgnoreQueryFilters()
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(ct);

        var ndFixed = 0;
        var intendedUseFixed = 0;
        var ndCleared = 0;
        var intendedUseCleared = 0;
        var changedIds = new List<Guid>();

        foreach (var m in materials)
        {
            var expectedNd = NormalizeLegacyText(m.Gost);
            var expectedIntendedUse = NormalizeLegacyText(m.Description);
            var changed = false;

            if (!string.Equals(m.Nd, expectedNd, StringComparison.Ordinal))
            {
                if (m.Nd != null && expectedNd == null) ndCleared++;
                else ndFixed++;
                m.Nd = expectedNd;
                changed = true;
            }

            if (!string.Equals(m.IntendedUse, expectedIntendedUse, StringComparison.Ordinal))
            {
                if (m.IntendedUse != null && expectedIntendedUse == null) intendedUseCleared++;
                else intendedUseFixed++;
                m.IntendedUse = expectedIntendedUse;
                changed = true;
            }

            if (changed) changedIds.Add(m.Id);
        }

        await _db.SaveChangesAsync(ct);

        var userId = _currentUser.GetRequiredUserId();
        foreach (var id in changedIds)
            await _audit.LogAsync(new AuditWriteRequest(
                "GsmMaterial",
                id.ToString(),
                "ReconcileTransitionFields",
                userId,
                EntityDisplayName: "Сверка Nd/IntendedUse с Gost/Description"), ct);

        return new GsmTransitionReconciliation
        {
            Inspected = await _db.GsmMaterials.IgnoreQueryFilters().CountAsync(ct),
            NdFixed = ndFixed,
            IntendedUseFixed = intendedUseFixed,
            NdCleared = ndCleared,
            IntendedUseCleared = intendedUseCleared,
            ChangedMaterialIds = changedIds,
        };
    }

    /// <summary>Повторяет правило бэкфилла: обрезка пробелов и пустая строка → null.</summary>
    private static string? NormalizeLegacyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
