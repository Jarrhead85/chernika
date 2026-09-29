using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
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
        return await FindTransitionDivergencesAsync(ct);
    }

    /// <summary>Поиск расхождений без проверки прав: её выполняет вызывающий.
    /// Сверка уже потребовала Reference.Edit, поэтому повторное требование
    /// Reference.View изнутри связало бы две независимые проверки прав.</summary>
    private async Task<List<GsmTransitionDivergence>> FindTransitionDivergencesAsync(CancellationToken ct)
    {
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
    /// Данные и журнал аудита сохраняются <b>атомарно</b>: одна явная транзакция,
    /// одна операция <c>SaveChangesAsync</c>, <c>Commit</c> — только после успеха.
    /// Ошибка на любом шаге (в том числе при записи журнала) откатывает и данные,
    /// и аудит, поэтому «исправленная марка без записи в журнале» невозможна.
    /// </para>
    /// <para>
    /// ВАЖНО: операция однонаправленная и допустима только пока источником истины
    /// остаются прежние поля. Повторять прежний бэкфилл вида
    /// <c>WHERE "Nd" IS NULL</c> бессмысленно — он не обновляет уже заполненное
    /// поле и не увидит расхождение. После переключения на <c>Nd</c> как на
    /// источник истины (PR-5) этот метод, наоборот, затёр бы новые значения
    /// старыми, поэтому он удаляется вместе с переходными полями (PR-6).
    /// </para>
    /// <para>
    /// Параметр <paramref name="acknowledge"/> — не «гарантия», а точка отзыва:
    /// он ссылается на <see cref="GsmLegacySourceOfTruth"/>, единственный член
    /// которого удаляется вместе с этим методом. После удаления члена любой
    /// оставшийся вызов перестаёт компилироваться, и запустить сверку, затирающую
    /// новые значения, уже нельзя.
    /// </para>
    /// </summary>
    public async Task<GsmTransitionReconciliation> ReconcileTransitionFieldsAsync(
        GsmLegacySourceOfTruth acknowledge, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        if (acknowledge != GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth)
            throw new InvalidOperationException(
                "Сверка Nd/IntendedUse перезаписывает новые поля значениями прежних. " +
                "Допустима только пока Gost/Description остаются источником истины.");

        var userId = _currentUser.GetRequiredUserId();

        // Всё чтение и вся запись — в одной транзакции: отчёт, повторная сверка
        // актуальных значений, запись данных и журнала.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Отчёт перечитывается здесь, а не берётся извне: решение принимается по
        // фактическому состоянию на момент операции.
        var divergences = await FindTransitionDivergencesAsync(ct);

        // Счётчик просмотренных марок берём ДО commit. Запрос после успешного
        // commit упал бы уже после фиксации изменений, и вызывающий получил бы
        // ошибку там, где сверка фактически выполнена.
        var inspected = await _db.GsmMaterials.IgnoreQueryFilters().CountAsync(ct);

        if (divergences.Count == 0)
        {
            await tx.CommitAsync(ct);
            return new GsmTransitionReconciliation { Inspected = inspected };
        }

        var ids = divergences.Select(d => d.Id).ToList();
        var materials = await _db.GsmMaterials.IgnoreQueryFilters()
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(ct);

        var ndFixed = 0;
        var intendedUseFixed = 0;
        var ndCleared = 0;
        var intendedUseCleared = 0;
        var changedIds = new List<Guid>();
        var changes = new Dictionary<Guid, string>();

        foreach (var m in materials)
        {
            // Значения пересчитываются по фактическому содержимому строки, а не по
            // отчёту: если марку правили между отчётом и записью, приводим ровно к
            // тому, что сейчас лежит в прежних полях.
            var expectedNd = NormalizeLegacyText(m.Gost);
            var expectedIntendedUse = NormalizeLegacyText(m.Description);
            var notes = new List<string>();

            if (!string.Equals(m.Nd, expectedNd, StringComparison.Ordinal))
            {
                if (m.Nd != null && expectedNd == null) ndCleared++;
                else ndFixed++;
                notes.Add($"Nd: \"{m.Nd}\" → \"{expectedNd}\"");
                m.Nd = expectedNd;
            }

            if (!string.Equals(m.IntendedUse, expectedIntendedUse, StringComparison.Ordinal))
            {
                if (m.IntendedUse != null && expectedIntendedUse == null) intendedUseCleared++;
                else intendedUseFixed++;
                notes.Add($"IntendedUse: \"{m.IntendedUse}\" → \"{expectedIntendedUse}\"");
                m.IntendedUse = expectedIntendedUse;
            }

            if (notes.Count == 0) continue;
            changedIds.Add(m.Id);
            changes[m.Id] = string.Join("; ", notes);
        }

        if (changedIds.Count == 0)
        {
            // Отчёт устарел: к моменту записи расхождений уже нет. Ничего не пишем
            // и не создаём пустых записей журнала.
            await tx.CommitAsync(ct);
            return new GsmTransitionReconciliation { Inspected = inspected };
        }

        // CreateLogAsync только добавляет запись в контекст: журнал уходит в БД тем
        // же SaveChanges, что и данные, поэтому разойтись они не могут.
        foreach (var id in changedIds)
        {
            var material = materials.First(m => m.Id == id);
            await _audit.CreateLogAsync(new AuditWriteRequest(
                "GsmMaterial",
                id.ToString(),
                "ReconcileTransitionFields",
                userId,
                Details: changes[id],
                EntityDisplayName: material.Name), ct);
        }

        await _db.SaveChangesAsync(ct);

        // Контроль результата до commit: если строку успели изменить извне, наши
        // значения оказались устаревшими. Откатываем вместо того, чтобы оставить
        // Nd, не соответствующий Gost.
        var verification = await _db.GsmMaterials.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => changedIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Nd, m.Gost, m.IntendedUse, m.Description })
            .ToListAsync(ct);

        foreach (var row in verification)
        {
            if (!string.Equals(row.Nd, NormalizeLegacyText(row.Gost), StringComparison.Ordinal) ||
                !string.Equals(row.IntendedUse, NormalizeLegacyText(row.Description), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Сверка прервана: переходные поля марки изменились извне во время операции. " +
                    "Повторите сверку.");
            }
        }

        await tx.CommitAsync(ct);

        // Счётчики возвращаются только после commit; счётчик просмотра получен раньше.
        return new GsmTransitionReconciliation
        {
            Inspected = inspected,
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
