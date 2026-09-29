using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Единственная точка бизнес-логики марок ГСМ и их классификаций.
/// <para>
/// Источник записи — новые поля (<c>Nd</c>, <c>IntendedUse</c>,
/// <c>GsmMaterialClassifications</c>). Прежние <c>Type</c>/<c>Gost</c>/
/// <c>Description</c> остаются физически и поддерживаются как ЗЕРКАЛА для
/// legacy-потребителей (поиск, PDF/XLSX, старые ХК) до PR-5, поэтому обновляются
/// в той же транзакции из тех же данных, а не редактируются независимо.
/// </para>
/// <para>
/// Справочные связи (второй справочник) здесь только читаются: методов их
/// изменения в сервисе нет намеренно.
/// </para>
/// </summary>
public class GsmMaterialService
{
    /// <summary>
    /// Предел длины НД, зеркалируемого в прежний <c>Gost</c> и копируемого в
    /// <c>IndividualCardItemMaterialSnapshots</c> (там <c>varchar(200)</c>).
    /// Проверяется до записи, значение не усекается.
    /// </summary>
    private const int LegacySnapshotLengthLimit = 200;

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

    // ── Чтение: сводный справочник ─────────────────────────────────────────

    /// <summary>
    /// Постраничный сводный список марок. Одна строка на <c>GsmMaterial.Id</c>:
    /// фильтры, счётчик и пагинация считают МАРКИ, а подгруппы и связи
    /// подгружаются отдельными ограниченными проекциями только для выбранной
    /// страницы. Поиск по группе/подгруппе идёт через EXISTS, поэтому марка с
    /// двумя совпавшими подгруппами не возвращается дважды.
    /// </summary>
    public async Task<PagedResult<GsmMaterialSummary>> GetPagedAsync(GsmMaterialQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<GsmMaterial> q = BuildBaseQuery(query, includeDrafts: query.OnlyUnclassified == true);

        if (query.InGostNomenclature == true)
            q = q.Where(m => m.InGostNomenclature);

        if (query.SuitabilityAny == true)
            q = q.Where(m => m.SuitabilityGround || m.SuitabilityAir || m.SuitabilitySea);

        if (!string.IsNullOrWhiteSpace(query.NatoIndex))
        {
            var nato = query.NatoIndex.Trim();
            q = q.Where(m => m.NatoIndex != null && EF.Functions.ILike(m.NatoIndex, $"%{nato}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.GroupName))
        {
            var group = query.GroupName.Trim();
            q = q.Where(m => m.Classifications.Any(c =>
                EF.Functions.ILike(c.GroupName, $"%{group}%")));
        }

        if (!string.IsNullOrWhiteSpace(query.SubgroupName))
        {
            var sub = query.SubgroupName.Trim();
            q = q.Where(m => m.Classifications.Any(c =>
                EF.Functions.ILike(c.SubgroupName, $"%{sub}%")));
        }

        if (query.OnlyUnclassified == true)
            q = q.Where(m => !m.Classifications.Any());

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var pattern = $"%{term}%";
            q = q.Where(m => EF.Functions.ILike(m.Name, pattern)
                || (m.Nd != null && EF.Functions.ILike(m.Nd, pattern))
                || (m.NatoIndex != null && EF.Functions.ILike(m.NatoIndex, pattern))
                || (m.Note != null && EF.Functions.ILike(m.Note, pattern))
                || m.Classifications.Any(c =>
                    EF.Functions.ILike(c.GroupName, pattern)
                    || EF.Functions.ILike(c.SubgroupName, pattern)));
        }

        var totalCount = await q.CountAsync(ct);

        IOrderedQueryable<GsmMaterial> ordered = query.SortBy switch
        {
            "Nd" => query.SortDescending
                ? q.OrderByDescending(m => m.Nd).ThenBy(m => m.Name)
                : q.OrderBy(m => m.Nd).ThenBy(m => m.Name),
            "NatoIndex" => query.SortDescending
                ? q.OrderByDescending(m => m.NatoIndex).ThenBy(m => m.Name)
                : q.OrderBy(m => m.NatoIndex).ThenBy(m => m.Name),
            "Group" => query.SortDescending
                ? q.OrderByDescending(m => m.Classifications.OrderBy(c => c.GroupName).Select(c => c.GroupName).FirstOrDefault())
                    .ThenBy(m => m.Name)
                : q.OrderBy(m => m.Classifications.OrderBy(c => c.GroupName).Select(c => c.GroupName).FirstOrDefault())
                    .ThenBy(m => m.Name),
            _ => query.SortDescending
                ? q.OrderByDescending(m => m.Name).ThenBy(m => m.Nd)
                : q.OrderBy(m => m.Name).ThenBy(m => m.Nd),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var pageRows = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new { m.Id, m.Name, m.IsDeleted, m.IsDraft, m.Nd, m.InGostNomenclature, m.IntendedUse, m.SuitabilityGround, m.SuitabilityAir, m.SuitabilitySea, m.NatoIndex, m.Note })
            .ToListAsync(ct);

        var pageIds = pageRows.Select(r => r.Id).ToList();

        // Подгруппы отдельной ограниченной проекцией — только для страницы.
        var classifications = await _db.GsmMaterialClassifications
            .AsNoTracking()
            .Where(c => pageIds.Contains(c.GsmMaterialId))
            .OrderBy(c => c.GroupName).ThenBy(c => c.SubgroupName)
            .Select(c => new { c.GsmMaterialId, c.GroupName, c.SubgroupName })
            .ToListAsync(ct);

        // Связи: только активные, с именами марок вместо GUID.
        var relations = await _db.GsmMaterialRelations
            .AsNoTracking()
            .Where(r => !r.IsDeleted && pageIds.Contains(r.PrimaryGsmMaterialId))
            .Select(r => new { r.PrimaryGsmMaterialId, r.RelatedGsmMaterialId, r.RelationType })
            .ToListAsync(ct);

        var relatedIds = relations.Select(r => r.RelatedGsmMaterialId).Distinct().ToList();
        var relatedNames = await _db.GsmMaterials
            .AsNoTracking()
            .Where(m => relatedIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Name })
            .ToListAsync();
        var nameById = relatedNames.ToDictionary(x => x.Id, x => x.Name);

        var items = pageRows.Select(r =>
        {
            var own = classifications.Where(c => c.GsmMaterialId == r.Id).ToList();
            var ownRelations = relations.Where(x => x.PrimaryGsmMaterialId == r.Id).ToList();

            List<string> Names(Func<GsmRelationType, bool> match)
            {
                var names = new List<string>();
                foreach (var rel in ownRelations.Where(rel => match(rel.RelationType)))
                {
                    if (nameById.TryGetValue(rel.RelatedGsmMaterialId, out var name))
                        names.Add(name);
                }
                return names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            }

            return new GsmMaterialSummary
            {
                Id = r.Id,
                Name = r.Name,
                IsDeleted = r.IsDeleted,
                IsDraft = r.IsDraft,
                GroupName = own.Count == 0 ? null : own[0].GroupName,
                SubgroupNames = own.Select(c => c.SubgroupName).ToList(),
                Nd = r.Nd,
                InGostNomenclature = r.InGostNomenclature,
                IntendedUse = r.IntendedUse,
                SuitabilityGround = r.SuitabilityGround,
                SuitabilityAir = r.SuitabilityAir,
                SuitabilitySea = r.SuitabilitySea,
                NatoIndex = r.NatoIndex,
                Note = r.Note,
                DuplicateNames = Names(t => t == GsmRelationType.Duplicate || t == GsmRelationType.DuplicateAndReserve),
                ReserveNames = Names(t => t == GsmRelationType.Reserve || t == GsmRelationType.DuplicateAndReserve),
                ForeignNames = Names(t => t == GsmRelationType.Foreign),
            };
        }).ToList();

        return new PagedResult<GsmMaterialSummary>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    private IQueryable<GsmMaterial> BuildBaseQuery(GsmMaterialQuery query, bool includeDrafts)
    {
        IQueryable<GsmMaterial> q = _db.GsmMaterials;

        if (!includeDrafts)
            q = q.Where(m => !m.IsDraft);

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

        return q;
    }

    /// <summary>Полная карточка марки для формы редактирования.</summary>
    public async Task<GsmMaterialEditView?> GetEditViewAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var material = await _db.GsmMaterials.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (material == null) return null;

        var classification = await _db.GsmMaterialClassifications
            .AsNoTracking()
            .Where(c => c.GsmMaterialId == id)
            .OrderBy(c => c.SubgroupName)
            .Select(c => new { c.GroupName, c.SubgroupName })
            .ToListAsync(ct);

        return new GsmMaterialEditView
        {
            Id = material.Id,
            Name = material.Name,
            IsDeleted = material.IsDeleted,
            IsDraft = material.IsDraft,
            Nd = material.Nd,
            InGostNomenclature = material.InGostNomenclature,
            IntendedUse = material.IntendedUse,
            SuitabilityGround = material.SuitabilityGround,
            SuitabilityAir = material.SuitabilityAir,
            SuitabilitySea = material.SuitabilitySea,
            NatoIndex = material.NatoIndex,
            Note = material.Note,
            GroupName = classification.Count == 0 ? null : classification[0].GroupName,
            SubgroupNames = classification.Select(c => c.SubgroupName).ToList(),
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
            var pattern = $"%{term}%";
            q = q.Where(m => EF.Functions.ILike(m.Name, pattern)
                || (m.Nd != null && EF.Functions.ILike(m.Nd, pattern))
                || (m.NatoIndex != null && EF.Functions.ILike(m.NatoIndex, pattern))
                || m.Classifications.Any(c =>
                    EF.Functions.ILike(c.GroupName, pattern)
                    || EF.Functions.ILike(c.SubgroupName, pattern)));
        }

        return await q
            .OrderBy(m => m.Name)
            .ThenBy(m => m.Nd)
            .Take(200)
            .ToListAsync(ct);
    }

    // ── Запись: марка + классификация атомарно ─────────────────────────────

    /// <summary>
    /// Создание опубликованной марки. Требуется одна группа и минимум одна
    /// подгруппа: опубликованная марка без классификации больше не создаётся
    /// НИ ОДНИМ путём (сервис, API, quick-create, предложение).
    /// </summary>
    public async Task<GsmMaterialEditView> CreateAsync(GsmMaterialWriteRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var fields = NormalizeRequest(request);

        // Обязательная классификация для новой опубликованной марки.
        if (fields.GroupName == null)
            throw new InvalidOperationException(
                "Укажите группу ГСМ: новая марка публикуется с одной группой и минимум одной подгруппой.");
        if (fields.Subgroups.Count == 0)
            throw new InvalidOperationException(
                "Укажите минимум одну подгруппу ГСМ.");

        var material = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = fields.Name,
            Nd = fields.Nd,
            InGostNomenclature = fields.InGostNomenclature,
            IntendedUse = fields.IntendedUse,
            SuitabilityGround = fields.SuitabilityGround,
            SuitabilityAir = fields.SuitabilityAir,
            SuitabilitySea = fields.SuitabilitySea,
            NatoIndex = fields.NatoIndex,
            Note = fields.Note,
            IsDeleted = false,
            IsDraft = false,
            DeletedAt = null,
        };
        ApplyLegacyMirrors(material, fields);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.GsmMaterials.Add(material);
        foreach (var subgroup in fields.Subgroups)
        {
            _db.GsmMaterialClassifications.Add(new GsmMaterialClassification
            {
                Id = Guid.NewGuid(),
                GsmMaterialId = material.Id,
                GroupName = fields.GroupName,
                SubgroupName = subgroup,
            });
        }

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "GsmMaterial",
            material.Id.ToString(),
            "Create",
            _currentUser.GetRequiredUserId(),
            Details: DescribeWrite(fields),
            EntityDisplayName: FormatDisplayName(material)), ct);

        await _db.SaveChangesAsync(ct);

        // Ответ строится ДО commit из уже сохранённого in-memory состояния.
        // Запрос после commit означал бы, что сбой чтения вернёт вызывающему
        // ошибку «не удалось сохранить» при уже записанных марке и audit.
        var view = BuildEditView(material, fields.GroupName, fields.Subgroups);

        await tx.CommitAsync(ct);
        return view;
    }

    /// <summary>
    /// Обновление марки и её классификации одной транзакцией. Родительская строка
    /// блокируется, а набор подгрупп заменяется двухфазно (сначала удаление,
    /// затем добавление) в той же транзакции: иначе EF может вставить раньше
    /// удаления, и DB-триггер «одна группа у марки» отклонит промежуточное
    /// состояние из двух разных групп.
    /// </summary>
    public async Task<GsmMaterialEditView?> UpdateAsync(Guid id, GsmMaterialWriteRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var fields = NormalizeRequest(request);
        if (fields.GroupName != null && fields.Subgroups.Count == 0)
            throw new InvalidOperationException("Укажите минимум одну подгруппу ГСМ.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Блокировка родительской марки: сериализует параллельные правки набора
        // подгрупп. У EF нет API для блокировки строки, поэтому это единственный
        // прямой SQL в сервисе.
        await LockMaterialRowAsync(id, ct);

        var material = await _db.GsmMaterials
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (material == null || material.IsDeleted)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        // Запрет включения в номенклатуру по ГОСТ, если марка — зарубежный
        // аналог в активной связи. DB-триггер остаётся последней защитой.
        if (fields.InGostNomenclature && !material.InGostNomenclature)
            await EnsureNotForeignAnalogAsync(id, ct);

        var existingClassifications = await _db.GsmMaterialClassifications
            .Where(c => c.GsmMaterialId == id)
            .ToListAsync(ct);

        var existingGroup = existingClassifications.Count == 0
            ? null
            : existingClassifications[0].GroupName;
        var existingSubgroups = existingClassifications.Select(c => c.SubgroupName).ToList();

        material.Name = fields.Name;
        material.Nd = fields.Nd;
        material.InGostNomenclature = fields.InGostNomenclature;
        material.IntendedUse = fields.IntendedUse;
        material.SuitabilityGround = fields.SuitabilityGround;
        material.SuitabilityAir = fields.SuitabilityAir;
        material.SuitabilitySea = fields.SuitabilitySea;
        material.NatoIndex = fields.NatoIndex;
        material.Note = fields.Note;
        ApplyLegacyMirrors(material, fields);

        // Черновик предложения публикуется только с классификацией.
        if (material.IsDraft && fields.GroupName != null)
            material.IsDraft = false;

        var groupChanged = !string.Equals(existingGroup, fields.GroupName, StringComparison.OrdinalIgnoreCase);
        var subgroupsChanged = groupChanged
            || !existingSubgroups.OrderBy(s => s, StringComparer.Ordinal)
                .SequenceEqual(fields.Subgroups.OrderBy(s => s, StringComparer.Ordinal));

        if (subgroupsChanged)
        {
            // Фаза 1: убрать прежний набор. При смене группы это обязательно
            // происходит ДО добавления нового, иначе две группы сосуществуют.
            if (existingClassifications.Count > 0)
            {
                _db.GsmMaterialClassifications.RemoveRange(existingClassifications);
                await _db.SaveChangesAsync(ct);
            }

            // Фаза 2: добавить новый набор.
            foreach (var subgroup in fields.Subgroups)
            {
                _db.GsmMaterialClassifications.Add(new GsmMaterialClassification
                {
                    Id = Guid.NewGuid(),
                    GsmMaterialId = id,
                    GroupName = fields.GroupName!,
                    SubgroupName = subgroup,
                });
            }
        }

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "GsmMaterial",
            id.ToString(),
            "Update",
            _currentUser.GetRequiredUserId(),
            Details: DescribeWrite(fields),
            EntityDisplayName: FormatDisplayName(material)), ct);

        await _db.SaveChangesAsync(ct);

        // Ответ строится ДО commit из in-memory состояния (см. CreateAsync):
        // после commit запросов, способных превратить успешное сохранение в
        // исключение, не выполняется.
        var subgroupsForView = subgroupsChanged ? fields.Subgroups : existingSubgroups;
        var groupForView = subgroupsChanged ? fields.GroupName : existingGroup;
        var view = BuildEditView(material, groupForView, subgroupsForView);

        await tx.CommitAsync(ct);
        return view;
    }

    private async Task LockMaterialRowAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = _db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM ""GsmMaterials"" WHERE ""Id"" = @id FOR UPDATE";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;
        cmd.Parameters.Add(p);

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();

        await cmd.ExecuteScalarAsync(ct);
    }

    private async Task EnsureNotForeignAnalogAsync(Guid id, CancellationToken ct)
    {
        var primaryIds = await _db.GsmMaterialRelations
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                && r.RelationType == GsmRelationType.Foreign
                && r.RelatedGsmMaterialId == id)
            .Select(r => r.PrimaryGsmMaterialId)
            .ToListAsync(ct);

        if (primaryIds.Count == 0) return;

        var foreignName = await _db.GsmMaterials
            .AsNoTracking()
            .Where(m => primaryIds.Contains(m.Id))
            .OrderBy(m => m.Name)
            .Select(m => m.Name)
            .FirstOrDefaultAsync(ct);

        if (foreignName != null)
            throw new InvalidOperationException(
                $"Нельзя включить марку в номенклатуру по ГОСТ: она является зарубежным аналогом марки «{foreignName}». " +
                "Сначала измените или удалите связь.");
    }

    // ── Soft-delete / restore ──────────────────────────────────────────────

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var material = await _db.GsmMaterials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (material == null || material.IsDeleted) return false;

        // Прямой FK без навигации через HKCardItems: строки материалов не имеют
        // собственного query-фильтра, поэтому видны все — независимо от статуса
        // карты (Draft/Approved/Archived/Deleted) и скрытых родителей.
        var usedInCards = await _db.HKCardItemMaterials
            .Where(r => r.GsmMaterialId == id)
            .Join(_db.HKCardItems, r => r.HKCardItemId, i => i.Id, (r, i) => new { r.Id, i.HKCardId })
            .Select(x => x.HKCardId)
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

    // ── Отчёт незавершённости переноса legacy-полей ───────────────────────

    /// <summary>
    /// Диагностика незавершённости переноса <c>Gost → Nd</c> и
    /// <c>Description → IntendedUse</c>.
    /// <para>
    /// ВАЖНО: с момента переключения источника истины на новые поля (PR-3) это
    /// НЕ показатель качества данных. Прежние поля теперь ведутся как зеркала и
    /// обновляются из новых, поэтому расхождение означает лишь незакрытый хвост
    /// переноса, а не «правильные» старые значения. Сверять по этому отчёту
    /// после переключения нельзя.
    /// </para>
    /// </summary>
    public async Task<List<GsmTransitionDivergence>> GetTransitionDivergencesAsync(CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);
        return await FindTransitionDivergencesAsync(ct);
    }

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
    /// Карточка марки собирается из уже сохранённой сущности и выбранного набора
    /// классификации — БД после этого не читается. Подгруппы упорядочены так же,
    /// как в <see cref="GetEditViewAsync"/>, чтобы ответ совпадал с последующим
    /// чтением из БД.
    /// </summary>
    private static GsmMaterialEditView BuildEditView(
        GsmMaterial material, string? groupName, IReadOnlyList<string> subgroupNames) => new()
    {
        Id = material.Id,
        Name = material.Name,
        IsDeleted = material.IsDeleted,
        IsDraft = material.IsDraft,
        Nd = material.Nd,
        InGostNomenclature = material.InGostNomenclature,
        IntendedUse = material.IntendedUse,
        SuitabilityGround = material.SuitabilityGround,
        SuitabilityAir = material.SuitabilityAir,
        SuitabilitySea = material.SuitabilitySea,
        NatoIndex = material.NatoIndex,
        Note = material.Note,
        GroupName = groupName,
        SubgroupNames = subgroupNames.OrderBy(s => s, StringComparer.Ordinal).ToList(),
    };

    // ── Нормализация и валидация ───────────────────────────────────────────

    private sealed record NormalizedFields(
        string Name,
        string? Nd,
        bool InGostNomenclature,
        string? IntendedUse,
        bool SuitabilityGround,
        bool SuitabilityAir,
        bool SuitabilitySea,
        string? NatoIndex,
        string? Note,
        string? GroupName,
        List<string> Subgroups);

    private static NormalizedFields NormalizeRequest(GsmMaterialWriteRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new InvalidOperationException("Укажите наименование марки ГСМ.");
        if (name.Length > 256)
            throw new InvalidOperationException("Наименование должно быть не длиннее 256 символов.");

        var natoIndex = NormalizeLegacyText(request.NatoIndex);
        if (natoIndex?.Length > 50)
            throw new InvalidOperationException("Индекс НАТО должен быть не длиннее 50 символов.");

        // НД зеркалируется в прежний Gost и копируется в снимок ИК, поэтому его
        // длина ограничена САМЫМ узким из этих мест, а не длиной колонки.
        // Предел проверяется здесь, до записи: «поймать ошибку БД и сказать
        // не удалось» не является исправлением. Усечения нет — лишнее значение
        // отклоняется понятным сообщением, а принятое сохраняется полностью.
        var nd = NormalizeLegacyText(request.Nd);
        if (nd?.Length > LegacySnapshotLengthLimit)
            throw new InvalidOperationException(
                $"НД не длиннее {LegacySnapshotLengthLimit} символов: прежнее поле ГОСТ и снимок ИК ограничены этой длиной. " +
                "Укажите НД короче или дождитесь переключения потребителей на новое поле.");

        var group = NormalizeLegacyText(request.GroupName);
        if (group?.Length > 200)
            throw new InvalidOperationException("Группа ГСМ должна быть не длиннее 200 символов.");

        // Подгруппы нормализуются тем же правилом, что и DB expression-index
        // (lower(btrim(...))): пустые отбрасываются, дубли схлопываются.
        var subgroups = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in request.SubgroupNames ?? new List<string>())
        {
            var value = NormalizeLegacyText(raw);
            if (value == null) continue;
            if (value.Length > 200)
                throw new InvalidOperationException("Подгруппа ГСМ должна быть не длиннее 200 символов.");
            if (seen.Add(NormalizeKey(value)))
                subgroups.Add(value);
        }

        return new NormalizedFields(
            name,
            nd,
            request.InGostNomenclature,
            NormalizeLegacyText(request.IntendedUse),
            request.SuitabilityGround,
            request.SuitabilityAir,
            request.SuitabilitySea,
            natoIndex,
            NormalizeLegacyText(request.Note),
            group,
            subgroups);
    }

    /// <summary>Ключ сравнения «как в БД»: обрезка пробелов и регистронезависимость.</summary>
    private static string NormalizeKey(string value) => value.Trim().ToLowerInvariant();

    /// <summary>Обрезка пробелов и пустая строка → null.</summary>
    private static string? NormalizeLegacyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Переходные правила для legacy-колонок, которые остаются физически до PR-5:
    /// <list type="bullet">
    /// <item><c>Type</c> = подгруппа, первая по алфавиту. Произвольную
    /// «первую случайную подгруппу» не выбираем: правило детерминированное.
    /// Марки с несколькими подгруппами дополнительно запрещены в новых строках
    /// ХК, пока не переключены legacy-потребители (вариант A §4.3).</item>
    /// <item><c>Gost</c> = <c>Nd</c>, <c>Description</c> = <c>IntendedUse</c> —
    /// зеркала для поиска, PDF/XLSX и старых ХК. Пишутся в той же транзакции из
    /// тех же данных, поэтому расхождение невозможно.</item>
    /// </list>
    /// У марки без классификации прежние значения не выдумываются: остаются
    /// те, что уже были.
    /// </summary>
    private static void ApplyLegacyMirrors(GsmMaterial material, NormalizedFields fields)
    {
        material.Nd = fields.Nd;
        material.Gost = fields.Nd;
        material.IntendedUse = fields.IntendedUse;
        material.Description = fields.IntendedUse;
        // Legacy Type = подгруппа (§4.3 контракта: SubgroupName = btrim("Type")).
        // При нескольких подгруппах берётся первая ПО АЛФАВИТУ — правило
        // детерминированное и документированное, а не «первая попавшаяся».
        // Пустой набор означает «классификации нет»: исторический Type
        // сохраняется, группа в него не пишется никогда.
        if (fields.Subgroups.Count > 0)
            material.Type = fields.Subgroups.OrderBy(s => s, StringComparer.Ordinal).First();
    }

    private static string DescribeWrite(NormalizedFields fields)
    {
        var parts = new List<string>();
        if (fields.GroupName != null)
            parts.Add($"Группа: \"{fields.GroupName}\"");
        if (fields.Subgroups.Count > 0)
            parts.Add($"Подгруппы: {string.Join(", ", fields.Subgroups)}");
        return parts.Count > 0 ? string.Join("; ", parts) : "Классификация не задана";
    }

    private static string FormatDisplayName(GsmMaterial material) =>
        string.IsNullOrWhiteSpace(material.Nd)
            ? material.Name
            : $"{material.Name} — {material.Nd}";
}
