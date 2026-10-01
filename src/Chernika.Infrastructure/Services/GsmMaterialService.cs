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
/// Источник записи и чтения — новые поля (<c>Nd</c>, <c>IntendedUse</c>,
/// <c>GsmMaterialClassifications</c>). Активные потребители (ХК, поиск, аудит,
/// API, Web) переключены на них в PR-5.
/// </para>
/// <para>
/// Прежние <c>Type</c>/<c>Gost</c>/<c>Description</c> остаются физически до PR-6.
/// <c>Gost</c> и <c>Description</c> больше не пишутся: действующих читателей у них
/// не осталось. <c>Type</c> — исключение: колонка NOT NULL, поэтому до PR-6
/// продолжает заполняться детерминированным значением подгруппы.
/// </para>
/// <para>
/// Справочные связи (второй справочник) здесь только читаются: методов их
/// изменения в сервисе нет намеренно.
/// </para>
/// </summary>
public class GsmMaterialService
{
    /// <summary>
    /// Предел длины НД марки.
    /// <para>
    /// До PR-5 предел был 200 символов и держался на двух внешних ограничениях:
    /// прежнем зеркале <c>Gost</c> и снимке ИК (<c>varchar(200)</c>). Оба больше не
    /// действуют: <c>Gost</c> в PR-5 перестаёт заполняться, а модуль ИК
    /// законсервирован и новых снимков не создаёт. Собственная колонка
    /// <c>GsmMaterial.Nd</c> — <c>text</c>, ограничения длины не имеет.
    /// </para>
    /// <para>
    /// Ограничение осталось явным и проверяется ДО записи: усечения не происходит,
    /// лишнее значение отклоняется понятным сообщением. 1000 символов — с запасом
    /// относительно 2000-символьного усечения деталей аудита
    /// (<c>AuditService.LimitLength</c>) и с разумной длиной поля ввода. Узкое
    /// место осталось только в одном пути: предложение из черновика ХК, где НД
    /// приходит в колонку <c>ReferenceProposal.Gost</c> (<c>varchar(200)</c>).
    /// </para>
    /// </summary>
    private const int NdMaxLength = 1000;

    /// <summary>
    /// Предел выдачи справочника выбора марок для формы связи. Защита от
    /// неогранированной загрузки; поиск сужает выдачу на сервере.
    /// </summary>
    private const int RelationSelectionLimit = 200;

    /// <summary>
    /// Предел длины примечания к связи. Поле принадлежит связи, а не марке
    /// (<c>GsmMaterial.Note</c> — отдельное поле, здесь недоступно).
    /// </summary>
    private const int RelationNoteMaxLength = 1000;

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

    /// <summary>
    /// Опубликованные марки для выбора в строках ХК.
    /// </summary>
    /// <remarks>
    /// Переходное ограничение варианта A §4.3 контракта ГСМ: пока
    /// legacy-потребители читают прежний <c>Type</c>, марка должна иметь ровно
    /// одну подгруппу, поэтому ни 0, ни несколько подгрупп в выборе не
    /// предлагаются. Уже сохранённые в карточке марки скрывать нельзя — они
    /// показываются по <c>Id</c> и не требуются для выбора заново. После
    /// переключения потребителей (PR-5) фильтр снимается.
    /// </remarks>
    /// <summary>
    /// Имеющиеся значения групп, подгрупп и индексов НАТО — для селектов фильтров
    /// первого справочника.
    /// <para>
    /// Сами фильтры остаются подстроковыми (<c>ILike</c>): это их действующая и
    /// покрытая тестами семантика, а список лишь предлагает выбрать значение,
    /// которое действительно есть в справочнике.
    /// </para>
    /// </summary>
    public async Task<GsmFilterOptions> GetFilterOptionsAsync(CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var groups = await _db.GsmMaterialClassifications
            .AsNoTracking()
            .Select(c => c.GroupName)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync(ct);

        var subgroups = await _db.GsmMaterialClassifications
            .AsNoTracking()
            .Select(c => c.SubgroupName)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);

        // Пустые и пробельные индексы в справочнике не показываем: фильтровать
        // по ним нечего.
        var nato = await _db.GsmMaterials
            .AsNoTracking()
            .Where(m => m.NatoIndex != null && m.NatoIndex.Trim() != string.Empty)
            .Select(m => m.NatoIndex!)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

        return new GsmFilterOptions
        {
            GroupNames = groups,
            SubgroupNames = subgroups,
            NatoIndexes = nato,
        };
    }

    /// <summary>
    /// Названия марок по идентификаторам — для отображения уже сохранённых строк
    /// ХК. Не зависит от переходного фильтра выбора: марка, уже присутствующая в
    /// документе, остаётся читаемой даже если больше не предлагается для выбора.
    /// <para>
    /// Запрос идёт <c>IgnoreQueryFilters</c>: у <c>GsmMaterial</c> есть глобальный
    /// фильтр по мягкому удалению, и с ним историческая строка ХК показывала бы
    /// «—» вместо названия удалённой марки. Название отображается, пометка об
    /// удалении — дело вызывающего.
    /// </para>
    /// </summary>
    public async Task<Dictionary<Guid, string>> GetNamesAsync(
        IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();

        return await _db.GsmMaterials
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => list.Contains(m.Id))
            .Select(m => new { m.Id, m.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
    }

    /// <summary>
    /// Справочные подсказки по связям для формы ХК: для каждой исходной марки —
    /// список связанных марок с типом связи.
    /// <para>
    /// Направление связи читается в прямом виде (Primary → Related): подсказка
    /// показывается пользователю в соответствующем списке категории, но НИЧЕГО не
    /// добавляет в строки ХК и не меняет их <c>Category</c>. Выбор остаётся за
    /// пользователем (PR-5 §3.3).
    /// </para>
    /// </summary>
    public async Task<Dictionary<Guid, List<GsmRelationHint>>> GetRelationHintsAsync(
        IEnumerable<Guid> primaryMaterialIds, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var ids = primaryMaterialIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var result = new Dictionary<Guid, List<GsmRelationHint>>();
        if (ids.Count == 0) return result;

        var rows = await _db.GsmMaterialRelations
            .AsNoTracking()
            .Where(r => !r.IsDeleted && ids.Contains(r.PrimaryGsmMaterialId))
            .OrderBy(r => r.PrimaryGsmMaterialId)
            .ThenBy(r => r.RelationType)
            .ThenBy(r => r.RelatedGsmMaterial!.Name)
            .Select(r => new GsmRelationHint(
                r.PrimaryGsmMaterialId,
                r.RelatedGsmMaterialId,
                r.RelatedGsmMaterial!.Name,
                r.RelatedGsmMaterial.Nd,
                r.RelationType))
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.PrimaryMaterialId, out var list))
            {
                list = new List<GsmRelationHint>();
                result[row.PrimaryMaterialId] = list;
            }
            list.Add(row);
        }

        return result;
    }

    /// <summary>
    /// Предложения связанных марок для строки ХК по её ОСНОВНЫМ маркам.
    /// <para>
    /// Только активные связи прямого направления <c>Primary → Related</c>:
    /// обратная связь не выдаётся как рекомендация для исходной марки. Категории
    /// строки выведены явным соответствием
    /// <see cref="GsmRelationCategoryMap.CategoriesFor"/>, поэтому
    /// <c>DuplicateAndReserve</c> предлагается в двух категориях независимо.
    /// </para>
    /// <para>
    /// Связь с недоступной маркой (удалённой или ещё не опубликованной) не
    /// выбрасывается молча: она возвращается с <c>IsAddable = false</c> и причиной,
    /// чтобы интерфейс мог объяснить недоступность. Метод ничего не пишет —
    /// добавление выполняется только через <c>HKCardService</c> при сохранении ХК.
    /// </para>
    /// </summary>
    public async Task<List<GsmRelationSuggestionSource>> GetRelatedSuggestionsAsync(
        IEnumerable<Guid> primaryMaterialIds, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var ids = primaryMaterialIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var result = new List<GsmRelationSuggestionSource>();
        if (ids.Count == 0) return result;

        // primaryName: имена нужны для заголовка блока «У марки «X» есть связи».
        var rows = await _db.GsmMaterialRelations
            .IgnoreQueryFilters()
            .Where(r => !r.IsDeleted && ids.Contains(r.PrimaryGsmMaterialId))
            .OrderBy(r => r.PrimaryGsmMaterialId)
            .ThenBy(r => r.RelatedGsmMaterial!.Name)
            .ThenBy(r => r.RelationType)
            .Select(r => new RelationSuggestionRow(
                r.PrimaryGsmMaterialId,
                r.PrimaryGsmMaterial!.Name,
                r.PrimaryGsmMaterial.IsDeleted,
                r.RelatedGsmMaterialId,
                r.RelatedGsmMaterial!.Name,
                r.RelatedGsmMaterial.Nd,
                r.RelatedGsmMaterial.IsDeleted,
                r.RelatedGsmMaterial.IsDraft,
                r.RelationType,
                r.Note))
            .ToListAsync(ct);

        foreach (var group in rows.GroupBy(r => r.PrimaryMaterialId))
        {
            var first = group.First();
            // Основная марка удалена — предложение бессмысленно: рекомендация
            // исходит от марки, которой в справочнике уже нет.
            if (first.PrimaryIsDeleted) continue;

            // Одна марка может прийти несколькими связями: категории объединяются,
            // одинаковые варианты одной категории не множатся.
            var byRelated = new Dictionary<Guid, GsmRelationSuggestionBuilder>();
            foreach (var r in group)
            {
                var categories = GsmRelationCategoryMap.CategoriesFor(r.RelationType);
                if (categories.Count == 0) continue;

                var reason = r.RelatedIsDeleted
                    ? "марка удалена"
                    : r.RelatedIsDraft ? "марка ещё не опубликована" : null;

                if (!byRelated.TryGetValue(r.RelatedMaterialId, out var builder))
                {
                    builder = new GsmRelationSuggestionBuilder(
                        r.RelatedMaterialId, r.RelatedName, r.RelatedNd, reason);
                    byRelated[r.RelatedMaterialId] = builder;
                }

                builder.Add(r.Note, categories, reason);
            }

            var suggestions = byRelated.Values
                .Select(b => b.Build())
                .Where(s => s.Categories.Count > 0)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
            if (suggestions.Count == 0) continue;

            result.Add(new GsmRelationSuggestionSource(
                group.Key, first.PrimaryName, suggestions));
        }

        return result;
    }

    /// <summary>Частичная проекция связи для предложения; собирается в памяти.</summary>
    private sealed record RelationSuggestionRow(
        Guid PrimaryMaterialId,
        string PrimaryName,
        bool PrimaryIsDeleted,
        Guid RelatedMaterialId,
        string RelatedName,
        string? RelatedNd,
        bool RelatedIsDeleted,
        bool RelatedIsDraft,
        GsmRelationType RelationType,
        string? Note);

    /// <summary>
    /// Склейка нескольких связей одной марки: категории объединяются без
    /// повторов, примечания сохраняются. Если хоть одна связь указывает на
    /// недоступную марку, недоступность переносится на объединённую позицию.
    /// </summary>
    private sealed class GsmRelationSuggestionBuilder
    {
        private readonly Guid _id;
        private readonly string _name;
        private readonly string? _nd;
        private string? _unavailable;
        private readonly List<GsmCategory> _categories = new();
        private readonly List<string> _notes = new();

        public GsmRelationSuggestionBuilder(Guid id, string name, string? nd, string? unavailable)
        {
            _id = id;
            _name = name;
            _nd = nd;
            _unavailable = unavailable;
        }

        public void Add(string? note, IReadOnlyList<GsmCategory> categories, string? unavailable)
        {
            foreach (var c in categories)
                if (!_categories.Contains(c))
                    _categories.Add(c);

            // Недоступность накапливается: недоступная марка остаётся
            // недоступной, даже если вторая связь выглядит рабочей.
            if (unavailable is not null)
                _unavailable = _unavailable ?? unavailable;

            if (!string.IsNullOrWhiteSpace(note) && !_notes.Contains(note))
                _notes.Add(note);
        }

        public GsmRelationSuggestion Build() => new(
            _id,
            _name,
            _nd,
            _notes.Count == 0 ? null : string.Join("; ", _notes),
            _categories.OrderBy(c => c).ToList(),
            _unavailable is null,
            _unavailable is null ? null : "Добавить нельзя: " + _unavailable);
    }

    /// <summary>
    /// Выбор марок для СПРАВОЧНЫХ СВЯЗЕЙ (второй справочник, PR-4).
    /// <para>
    /// Отдельный read-only метод намеренно: <see cref="GetActiveForSelectionAsync"/>
    /// после PR-3 фильтрует марки с ровно одной подгруппой специально для строк
    /// ХК. Ограничение «ровно одна подгруппа» к записи справочной связи отношения
    /// не имеет, поэтому здесь предлагается любая ОПУБЛИКОВАННАЯ активная марка
    /// независимо от количества подгрупп (0/1/2). Soft-deleted и Draft не
    /// предлагаются никогда.
    /// </para>
    /// <para>
    /// Выдача ограничена: справочник используется только для выбора в форме связи,
    /// поэтому защищён от неограниченной загрузки.
    /// </para>
    /// </summary>
    public async Task<List<GsmMaterial>> GetSelectableForRelationAsync(
        string? searchText = null, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<GsmMaterial> q = _db.GsmMaterials
            .Where(m => !m.IsDraft && !m.IsDeleted);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            q = q.Where(m => EF.Functions.ILike(m.Name, pattern)
                || (m.Nd != null && EF.Functions.ILike(m.Nd, pattern))
                || (m.NatoIndex != null && EF.Functions.ILike(m.NatoIndex, pattern)));
        }

        // 200 — предел ОДНОЙ выдачи, а не всего справочника: поиск выполняется
        // на сервере, поэтому любая опубликованная марка достижима вводом текста.
        // Сортировка по имени продублирована по Id: уникальности Name нет, и без
        // этого одноимённые марки могли бы «пропадать» между выдачами.
        return await q
            .OrderBy(m => m.Name)
            .ThenBy(m => m.Id)
            .Take(RelationSelectionLimit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Варианты выбора марки для формы связи с готовыми подписями.
    /// <para>
    /// Считаются на сервере, поэтому подписи одинаковы в Web и API. Ключевой
    /// момент: подпись включает НД и классификацию, потому что уникальности
    /// <c>GsmMaterial.Name</c> нет и одноимённые марки иначе неразличимы.
    /// Если отображаемые сведения совпали, подпись дополняется порядковым
    /// номером среди одноимённых — выбор «первой молча» исключён на уровне
    /// представления.
    /// </para>
    /// </summary>
    public async Task<List<GsmRelationMaterialOption>> GetRelationMaterialOptionsAsync(
        string? searchText = null,
        CancellationToken ct = default,
        IEnumerable<Guid>? includeIds = null)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        var include = (includeIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var bySearch = await LoadRelationMaterialOptionsAsync(searchText, ct);

        // Марки уже выбранной связи показываются всегда, даже если не попали в
        // выдачу поиска: при правке выбор не должен «слететь».
        if (include.Count > 0)
        {
            var missing = include.Where(id => bySearch.All(o => o.Id != id)).ToList();
            if (missing.Count > 0)
            {
                var extra = await LoadRelationMaterialOptionsAsync(null, ct, missing);
                bySearch.AddRange(extra);
            }
        }

        return BuildRelationMaterialLabels(bySearch);
    }

    /// <summary>
    /// Подписи вариантов выбора. Одинаковые отображаемые сведения получают
    /// различающий суффикс, чтобы две одноимённые марки не выглядели
    /// одинаковыми и выбор не был неоднозначным.
    /// </summary>
    private static List<GsmRelationMaterialOption> BuildRelationMaterialLabels(
        List<GsmRelationMaterialOption> options)
    {
        var baseLabels = options
            .Select(o => new { Option = o, Label = RelationMaterialOptionLabel(o) })
            .ToList();

        var duplicateCount = baseLabels
            .GroupBy(x => x.Label, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var counters = new Dictionary<string, int>(StringComparer.Ordinal);

        return baseLabels.Select(x =>
        {
            var duplicate = duplicateCount.TryGetValue(x.Label, out var n) && n > 1;

            if (!duplicate)
            {
                x.Option.DisplayLabel = x.Label;
                return x.Option;
            }

            // Одноимённые и равные по прочим сведениям: различаем порядковым
            // номером, а не молча выбираем первую.
            counters.TryGetValue(x.Label, out var seen);
            counters[x.Label] = seen + 1;
            x.Option.DisplayLabel = $"{x.Label} (вариант {seen + 1})";
            return x.Option;
        }).ToList();
    }

    /// <summary>Базовая подпись марки: имя, НД и классификация.</summary>
    private static string RelationMaterialOptionLabel(GsmRelationMaterialOption o)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(o.Name)) parts.Add(o.Name.Trim());
        if (!string.IsNullOrWhiteSpace(o.Nd)) parts.Add($"НД {o.Nd.Trim()}");

        if (!string.IsNullOrWhiteSpace(o.GroupName))
        {
            var subs = o.SubgroupNames.Count == 0
                ? string.Empty
                : $"/{string.Join(", ", o.SubgroupNames)}";
            parts.Add($"{o.GroupName!.Trim()}{subs}");
        }

        if (parts.Count == 0) parts.Add("без наименования");

        var label = string.Join(" — ", parts);
        if (o.IsDeleted) label += " — удалена";
        else if (o.IsDraft) label += " — черновик";
        return label;
    }

    private async Task<List<GsmRelationMaterialOption>> LoadRelationMaterialOptionsAsync(
        string? searchText, CancellationToken ct, IEnumerable<Guid>? forceIds = null)
    {
        IQueryable<GsmMaterial> q = _db.GsmMaterials
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted && !m.IsDraft);

        if (forceIds is not null)
        {
            var forcedIds = forceIds.ToList();
            if (forcedIds.Count == 0) return new List<GsmRelationMaterialOption>();
            q = q.Where(m => forcedIds.Contains(m.Id));
        }
        else if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            q = q.Where(m => EF.Functions.ILike(m.Name, pattern)
                || (m.Nd != null && EF.Functions.ILike(m.Nd, pattern))
                || (m.NatoIndex != null && EF.Functions.ILike(m.NatoIndex, pattern)));
        }

        // Идентификаторы марок нужны вместе с классификацией: она подгружается
        // только для отдаваемой страницы, а не всем справочником.
        var rows = await q
            .OrderBy(m => m.Name)
            .ThenBy(m => m.Id)
            .Take(forceIds is not null ? forceIds.Count() : RelationSelectionLimit)
            .Select(m => new { m.Id, m.Name, m.Nd, m.InGostNomenclature, m.IsDeleted, m.IsDraft })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var classifications = ids.Count == 0
            ? new List<GsmClassificationRef>()
            : (await _db.GsmMaterialClassifications
                .AsNoTracking()
                .Where(c => ids.Contains(c.GsmMaterialId))
                .Select(c => new GsmClassificationRef(c.GsmMaterialId, c.GroupName, c.SubgroupName))
                .ToListAsync(ct));

        return rows.Select(m =>
        {
            var own = classifications.Where(c => c.GsmMaterialId == m.Id).ToList();
            return new GsmRelationMaterialOption
            {
                Id = m.Id,
                Name = m.Name,
                Nd = m.Nd,
                InGostNomenclature = m.InGostNomenclature,
                IsDeleted = m.IsDeleted,
                IsDraft = m.IsDraft,
                GroupName = own.Count == 0 ? null : own[0].GroupName,
                SubgroupNames = own.Select(c => c.SubgroupName).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            };
        }).ToList();
    }

    /// <summary>
    /// Выбор марок для строк ХК.
    /// <para>
    /// Требование к марке — опубликована, не удалена и КЛАССИФИЦИРОВАНА (хотя бы
    /// одна подгруппа). Переходное ограничение PR-3 «ровно одна подгруппа» снято в
    /// PR-5 вместе с переключением активных потребителей на <c>Nd</c> и
    /// классификации: прежнее правило существовало только потому, что прежний
    /// <c>Type</c> хранил одну подгруппу и не мог их различить.
    /// </para>
    /// <para>
    /// Legacy-марка без классификации по-прежнему не предлагается для новой
    /// строки: группа из прежнего <c>Type</c> не выдумывается. Уже сохранённые
    /// исторические строки при этом не перепроверяются.
    /// </para>
    /// </summary>
    public async Task<List<GsmMaterial>> GetActiveForSelectionAsync(
        string? searchText = null,
        CancellationToken ct = default,
        IReadOnlyCollection<Guid>? excludeIds = null)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<GsmMaterial> q = _db.GsmMaterials
            .Where(m => !m.IsDraft && !m.IsDeleted);

        // Минимум одна подгруппа: EXISTS, а не счётчик — несколько подгрупп
        // теперь разрешены, неограниченное их число ограничивается правилами
        // сервиса записи.
        q = q.Where(m => m.Classifications.Any());

        if (excludeIds is { Count: > 0 })
        {
            var excluded = excludeIds.ToList();
            q = q.Where(m => !excluded.Contains(m.Id));
        }

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
            // Классификация нужна выпадающему списку ХК: группировка и поиск идут
            // по ней, а не по прежнему Type.
            .Include(m => m.Classifications)
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

        // Правило совпадает с формой: группа без подгрупп недопустима, а правка
        // изначально неклассифицированной legacy-марки (нет ни группы, ни
        // подгрупп) разрешена — группу не выдумываем и прежний Type не затираем.
        if (fields.GroupName != null && fields.Subgroups.Count == 0)
            throw new InvalidOperationException("Укажите минимум одну подгруппу ГСМ.");
        if (fields.GroupName == null && fields.Subgroups.Count > 0)
            throw new InvalidOperationException(
                "Подгруппы указаны без группы. Укажите группу или уберите подгруппы.");

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

        // Явное удаление классификации у уже классифицированной марки — не
        // поддерживаемая операция: продуктового решения о «возврате марки в
        // неклассифицированные» нет, а молча терять классификацию нельзя. Правка
        // изначально неклассифицированной legacy-марки (existingGroup == null)
        // при этом разрешена и сюда не попадает.
        if (existingGroup != null && fields.GroupName == null)
            throw new InvalidOperationException(
                "Удаление классификации марки не поддерживается. Укажите группу и подгруппу или не меняйте классификацию.");

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

        // Переходное ограничение варианта A §4.3 контракта ГСМ СНЯТО в PR-5.
        // Раньше марка, связанная со строкой ХК, не могла перестать иметь ровно
        // одну подгруппу: прежний Type хранил одну подгруппу и не различал их. Теперь
        // действующие потребители работают с Nd и классификацией, поэтому у
        // используемой в ХК марки набор подгрупп можно менять так же, как у
        // свободной. Существующие строки ХК при этом не трогаются: их Category и
        // GsmMaterialId остаются прежними.

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

    // ── Второй справочник: направленные связи Primary → Related (PR-4) ──────

    /// <summary>
    /// Постраничный список направленных связей. Строка — одна направленная пара,
    /// поэтому счётчик и пагинация считают СВЯЗИ. Имена марок и их признаки
    /// удаления подтягиваются проекцией: soft-deleted марки скрыты обычным
    /// query filter, но в истории удалённых связей имена должны оставаться
    /// корректными.
    /// </summary>
    public async Task<PagedResult<GsmRelationSummary>> GetRelationsPagedAsync(
        GsmRelationQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        // Связи не имеют query-фильтра в модели: статус задаётся явно. Но у
        // GsmMaterial фильтр есть, поэтому навигации на марку он применяется и
        // SKIP-ает soft-deleted марку: связь с удалённой маркой исчезла бы из
        // истории молча. Поэтому IgnoreQueryFilters — чтобы имена оставались
        // корректными; признак удаления отдаётся отдельным полем.
        IQueryable<GsmMaterialRelation> q = _db.GsmMaterialRelations
            .AsNoTracking()
            .IgnoreQueryFilters();

        // Ровно три режима статуса. Прежняя проверка `!= true` смешивала их:
        // «Удалённые» (true) показывали ВСЕ, а «Все» (null) — только активные.
        //   false → только активные; true → только удалённые; null → все.
        if (query.ShowDeleted == false)
            q = q.Where(r => !r.IsDeleted);
        else if (query.ShowDeleted == true)
            q = q.Where(r => r.IsDeleted);

        if (query.RelationType.HasValue)
            q = q.Where(r => r.RelationType == query.RelationType.Value);

        if (query.PrimaryGsmMaterialId is Guid primaryId && primaryId != Guid.Empty)
            q = q.Where(r => r.PrimaryGsmMaterialId == primaryId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            q = q.Where(r => EF.Functions.ILike(r.PrimaryGsmMaterial.Name, pattern)
                || EF.Functions.ILike(r.RelatedGsmMaterial.Name, pattern)
                || (r.Note != null && EF.Functions.ILike(r.Note, pattern)));
        }

        var totalCount = await q.CountAsync(ct);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 200 ? 15 : query.PageSize;

        IOrderedQueryable<GsmMaterialRelation> ordered = query.SortBy switch
        {
            "Primary" => query.SortDescending
                ? q.OrderByDescending(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.RelatedGsmMaterial.Name)
                : q.OrderBy(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.RelatedGsmMaterial.Name),
            "Related" => query.SortDescending
                ? q.OrderByDescending(r => r.RelatedGsmMaterial.Name).ThenBy(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.Id)
                : q.OrderBy(r => r.RelatedGsmMaterial.Name).ThenBy(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.Id),
            "RelationType" => query.SortDescending
                ? q.OrderByDescending(r => r.RelationType).ThenBy(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.Id)
                : q.OrderBy(r => r.RelationType).ThenBy(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.Id),
            _ => query.SortDescending
                ? q.OrderByDescending(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.RelatedGsmMaterial.Name).ThenBy(r => r.Id)
                : q.OrderBy(r => r.PrimaryGsmMaterial.Name).ThenBy(r => r.RelatedGsmMaterial.Name).ThenBy(r => r.Id),
        };

        // ThenBy(Id) во всех ветках: без устойчивого tie-break страницы «прыгают»
        // при равных именах марок, потому что уникальности Name в БД нет.

        var rows = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id,
                r.PrimaryGsmMaterialId,
                PrimaryName = r.PrimaryGsmMaterial.Name,
                PrimaryIsDeleted = r.PrimaryGsmMaterial.IsDeleted,
                r.RelatedGsmMaterialId,
                RelatedName = r.RelatedGsmMaterial.Name,
                RelatedIsDeleted = r.RelatedGsmMaterial.IsDeleted,
                r.RelationType,
                r.Note,
                r.IsDeleted,
                RelatedInGostNomenclature = r.RelatedGsmMaterial.InGostNomenclature,
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new GsmRelationSummary
        {
            Id = r.Id,
            PrimaryGsmMaterialId = r.PrimaryGsmMaterialId,
            PrimaryName = r.PrimaryName,
            PrimaryIsDeleted = r.PrimaryIsDeleted,
            RelatedGsmMaterialId = r.RelatedGsmMaterialId,
            RelatedName = r.RelatedName,
            RelatedIsDeleted = r.RelatedIsDeleted,
            RelationType = r.RelationType,
            Note = r.Note,
            IsDeleted = r.IsDeleted,
            RelatedInGostNomenclature = r.RelatedInGostNomenclature,
        }).ToList();

        return new PagedResult<GsmRelationSummary>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    /// <summary>Карточка связи для формы. null — связи нет.</summary>
    public async Task<GsmRelationEditView?> GetRelationEditViewAsync(
        Guid relationId, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        // Как и в списке: IgnoreQueryFilters, чтобы карточка связи читалась и у
        // soft-deleted марок (иначе навигация вернула бы пустое имя).
        return await _db.GsmMaterialRelations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.Id == relationId)
            .Select(r => new GsmRelationEditView
            {
                Id = r.Id,
                PrimaryGsmMaterialId = r.PrimaryGsmMaterialId,
                PrimaryName = r.PrimaryGsmMaterial.Name,
                RelatedGsmMaterialId = r.RelatedGsmMaterialId,
                RelatedName = r.RelatedGsmMaterial.Name,
                RelationType = r.RelationType,
                Note = r.Note,
                IsDeleted = r.IsDeleted,
            })
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Создание направленной связи. Марки принимаются только по Guid и должны
    /// существовать, быть опубликованными и неудалёнными. Связь направленная:
    /// <c>A → B</c> и <c>B → A</c> — разные пары, но вторая активная запись той
    /// же пары недопустима независимо от типа.
    /// </summary>
    public async Task<GsmRelationEditView> CreateRelationAsync(
        GsmRelationWriteRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var primaryId = request.PrimaryGsmMaterialId;
        var relatedId = request.RelatedGsmMaterialId;

        if (primaryId == Guid.Empty || relatedId == Guid.Empty)
            throw new InvalidOperationException("Укажите основную и связанную марки ГСМ.");
        if (primaryId == relatedId)
            throw new InvalidOperationException("Марка не может быть связана сама с собой.");

        var note = NormalizeRelationNote(request.Note);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Гонка «создание связи ↔ soft-delete марки» закрывается единым порядком
        // блокировок: обе марки блокируются по возрастанию Guid, затем состояние
        // перепроверяется уже под блокировкой. Два независимых AnyAsync без
        // блокировки защиты от гонки не дают.
        await LockMaterialsOrderedAsync(new[] { primaryId, relatedId }, ct);

        var materials = await LoadRelationMaterialsAsync(new[] { primaryId, relatedId }, ct);

        EnsureRelationMaterialsUsable(materials, primaryId, relatedId);

        EnsureForeignAllowed(request.RelationType, relatedId, materials);

        var duplicateActive = await _db.GsmMaterialRelations
            .AsNoTracking()
            .AnyAsync(r => r.PrimaryGsmMaterialId == primaryId
                && r.RelatedGsmMaterialId == relatedId
                && !r.IsDeleted, ct);

        if (duplicateActive)
            throw new InvalidOperationException(
                "Такая связь уже существует. Отредактируйте существующую запись — " +
                "для одной направленной пары активна только одна связь.");

        var relation = new GsmMaterialRelation
        {
            Id = Guid.NewGuid(),
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = relatedId,
            RelationType = request.RelationType,
            Note = note,
            IsDeleted = false,
        };
        _db.GsmMaterialRelations.Add(relation);

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "GsmMaterialRelation",
            relation.Id.ToString(),
            "Create",
            _currentUser.GetRequiredUserId(),
            Details: DescribeRelation(relation, materials),
            EntityDisplayName: FormatRelationDisplayName(relation, materials)), ct);

        await _db.SaveChangesAsync(ct);

        // Ответ строится до commit из уже сохранённого состояния: чтение после
        // commit превратило бы успех в ложный отказ.
        var view = BuildRelationView(relation, materials);

        await tx.CommitAsync(ct);
        return view;
    }

    /// <summary>
    /// Изменение связи. Смена концов пары проходит те же проверки, что и
    /// создание. Физическое удаление запрещено, связь только soft-delete.
    /// </summary>
    public async Task<GsmRelationEditView?> UpdateRelationAsync(
        Guid relationId, GsmRelationWriteRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var primaryId = request.PrimaryGsmMaterialId;
        var relatedId = request.RelatedGsmMaterialId;

        if (primaryId == Guid.Empty || relatedId == Guid.Empty)
            throw new InvalidOperationException("Укажите основную и связанную марки ГСМ.");
        if (primaryId == relatedId)
            throw new InvalidOperationException("Марка не может быть связана сама с собой.");

        var note = NormalizeRelationNote(request.Note);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var relation = await _db.GsmMaterialRelations
            .FirstOrDefaultAsync(r => r.Id == relationId, ct);

        if (relation == null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        if (relation.IsDeleted)
            throw new InvalidOperationException(
                "Удалённую связь изменить нельзя. Создайте новую связь для этой пары марок.");

        await LockMaterialsOrderedAsync(new[] { primaryId, relatedId }, ct);

        var materials = await LoadRelationMaterialsAsync(new[] { primaryId, relatedId }, ct);

        EnsureRelationMaterialsUsable(materials, primaryId, relatedId);
        EnsureForeignAllowed(request.RelationType, relatedId, materials);

        // Концы пары изменились — активная запись той же пары уже должна отсутствовать.
        var endsChanged = relation.PrimaryGsmMaterialId != primaryId
            || relation.RelatedGsmMaterialId != relatedId;

        if (endsChanged)
        {
            var duplicateActive = await _db.GsmMaterialRelations
                .AsNoTracking()
                .AnyAsync(r => r.Id != relation.Id
                    && r.PrimaryGsmMaterialId == primaryId
                    && r.RelatedGsmMaterialId == relatedId
                    && !r.IsDeleted, ct);

            if (duplicateActive)
                throw new InvalidOperationException(
                    "Такая связь уже существует. Отредактируйте существующую запись — " +
                    "для одной направленной пары активна только одна связь.");
        }

        relation.PrimaryGsmMaterialId = primaryId;
        relation.RelatedGsmMaterialId = relatedId;
        relation.RelationType = request.RelationType;
        relation.Note = note;

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "GsmMaterialRelation",
            relation.Id.ToString(),
            "Update",
            _currentUser.GetRequiredUserId(),
            Details: DescribeRelation(relation, materials),
            EntityDisplayName: FormatRelationDisplayName(relation, materials)), ct);

        await _db.SaveChangesAsync(ct);

        var view = BuildRelationView(relation, materials);

        await tx.CommitAsync(ct);
        return view;
    }

    /// <summary>
    /// Мягкое удаление связи. Физическое удаление не выполняется. Повторный
    /// вызов идемпотентен: уже удалённая связь возвращает false.
    /// </summary>
    public async Task<bool> DeleteRelationAsync(Guid relationId, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var relation = await _db.GsmMaterialRelations
            .FirstOrDefaultAsync(r => r.Id == relationId, ct);

        if (relation == null)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        if (relation.IsDeleted)
        {
            // Идемпотентно: повторное удаление уже удалённой связи — успех без записи.
            await tx.CommitAsync(ct);
            return false;
        }

        var materials = await LoadRelationMaterialsAsync(
            new[] { relation.PrimaryGsmMaterialId, relation.RelatedGsmMaterialId }, ct);

        relation.IsDeleted = true;

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "GsmMaterialRelation",
            relation.Id.ToString(),
            "Delete",
            _currentUser.GetRequiredUserId(),
            Details: DescribeRelation(relation, materials),
            EntityDisplayName: FormatRelationDisplayName(relation, materials)), ct);

        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Блокировка строк марок в согласованном порядке (по возрастанию Guid) во
    /// избежание взаимных блокировок. Переиспользуется всеми путями, которые
    /// обязаны согласоваться: создание связи и soft-delete марки.
    /// </summary>
    private async Task LockMaterialsOrderedAsync(IEnumerable<Guid> materialIds, CancellationToken ct)
    {
        var ids = materialIds.Where(id => id != Guid.Empty).Distinct().OrderBy(id => id).ToList();
        if (ids.Count == 0) return;

        await using var cmd = _db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM ""GsmMaterials"" WHERE ""Id"" = ANY(@ids) ORDER BY ""Id"" FOR UPDATE";
        var p = cmd.CreateParameter();
        p.ParameterName = "@ids";
        p.Value = ids.ToArray();
        cmd.Parameters.Add(p);
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Обе марки связи должны существовать, быть опубликованными и неудалёнными.
    /// Проверяется при создании, правке и смене концов существующей связи.
    /// </summary>
    private static void EnsureRelationMaterialsUsable(
        List<RelationMaterialRef> materials, Guid primaryId, Guid relatedId)
    {
        static string NameOf(Guid id, List<RelationMaterialRef> list) =>
            list.FirstOrDefault(m => m.Id == id)?.Name ?? "марка не найдена";

        if (!materials.Any(m => m.Id == primaryId))
            throw new InvalidOperationException(
                $"Основная марка не найдена в справочнике ГСМ: {NameOf(primaryId, materials)}.");
        if (!materials.Any(m => m.Id == relatedId))
            throw new InvalidOperationException(
                $"Связанная марка не найдена в справочнике ГСМ: {NameOf(relatedId, materials)}.");

        // Мягко удалённые и Draft марки для новой связи непригодны. Идентификатор
        // в сообщении не выводится: пользователю показывается имя из справочника.
        var unusable = materials.FirstOrDefault(m => m.IsDeleted || m.IsDraft);
        if (unusable != null)
            throw new InvalidOperationException(
                $"Марка «{unusable.Name}» удалена или не опубликована и не может участвовать в связи.");
    }

    /// <summary>
    /// Правило Foreign: связанная марка не должна быть включена в номенклатуру по
    /// ГОСТ. Проверяется при создании, правке и смене типа на Foreign.
    /// </summary>
    private static void EnsureForeignAllowed(
        GsmRelationType type, Guid relatedId, List<RelationMaterialRef> materials)
    {
        if (type != GsmRelationType.Foreign) return;

        var related = materials.FirstOrDefault(m => m.Id == relatedId);
        if (related == null || !related.InGostNomenclature) return;

        throw new InvalidOperationException(
            $"Марка «{related.Name}» включена в номенклатуру по ГОСТ и не может быть связана как зарубежный аналог. " +
            "Снимите признак у марки или выберите другой тип связи.");
    }

    /// <summary>Ссылка на марку для проверок связи: имя и признаки без EF-навигации.</summary>
    private sealed record RelationMaterialRef(
        Guid Id, string Name, bool InGostNomenclature, bool IsDeleted, bool IsDraft);

    /// <summary>Строка классификации марки для подписи варианта выбора.</summary>
    private sealed record GsmClassificationRef(
        Guid GsmMaterialId, string GroupName, string SubgroupName);

    private async Task<List<RelationMaterialRef>> LoadRelationMaterialsAsync(
        IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new List<RelationMaterialRef>();

        var rows = await _db.GsmMaterials
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => list.Contains(m.Id))
            .Select(m => new { m.Id, m.Name, m.InGostNomenclature, m.IsDeleted, m.IsDraft })
            .ToListAsync(ct);

        return rows
            .Select(m => new RelationMaterialRef(
                m.Id, m.Name, m.InGostNomenclature, m.IsDeleted, m.IsDraft))
            .ToList();
    }

    private static string? NormalizeRelationNote(string? note)
    {
        var value = NormalizeLegacyText(note);
        if (value != null && value.Length > RelationNoteMaxLength)
            throw new InvalidOperationException(
                $"Примечание к связи должно быть не длиннее {RelationNoteMaxLength} символов.");
        return value;
    }

    private static string NameOfRelationMaterial(
        Guid id, List<RelationMaterialRef> materials) =>
        materials.FirstOrDefault(m => m.Id == id)?.Name ?? "марка не найдена";

    private static string DescribeRelation(
        GsmMaterialRelation relation, List<RelationMaterialRef> materials) =>
        $"Связь: основная «{NameOfRelationMaterial(relation.PrimaryGsmMaterialId, materials)}», " +
        $"связанная «{NameOfRelationMaterial(relation.RelatedGsmMaterialId, materials)}», " +
        $"тип {relation.RelationType}" +
        (string.IsNullOrWhiteSpace(relation.Note) ? "" : $", примечание: {relation.Note}");

    private static string FormatRelationDisplayName(
        GsmMaterialRelation relation, List<RelationMaterialRef> materials) =>
        DescribeRelation(relation, materials);

    private static GsmRelationEditView BuildRelationView(
        GsmMaterialRelation relation, List<RelationMaterialRef> materials) => new()
    {
        Id = relation.Id,
        PrimaryGsmMaterialId = relation.PrimaryGsmMaterialId,
        PrimaryName = NameOfRelationMaterial(relation.PrimaryGsmMaterialId, materials),
        RelatedGsmMaterialId = relation.RelatedGsmMaterialId,
        RelatedName = NameOfRelationMaterial(relation.RelatedGsmMaterialId, materials),
        RelationType = relation.RelationType,
        Note = relation.Note,
        IsDeleted = relation.IsDeleted,
    };

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

        // Тот же порядок блокировки, что и в CreateRelationAsync: блокируем
        // строку марки ДО проверок и перепроверяем состояние под блокировкой.
        // Без этого создание связи и soft-delete марки могут оба завершиться
        // успешно, оставив активную связь с удалённой маркой.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        await LockMaterialsOrderedAsync(new[] { id }, ct);

        var material = await _db.GsmMaterials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (material == null || material.IsDeleted)
        {
            await tx.CommitAsync(ct);
            return false;
        }

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

        // До открытия второго справочника марку, участвующую в АКТИВНОЙ
        // направленной связи, удалить нельзя — ни как Primary, ни как Related,
        // даже если в ХК ссылок нет. FK RESTRICT защищает только физическое
        // удаление и soft-delete не ловит. Удалённые связи не блокируют.
        var activeRelation = await _db.GsmMaterialRelations
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                && (r.PrimaryGsmMaterialId == id || r.RelatedGsmMaterialId == id))
            .OrderBy(r => r.PrimaryGsmMaterialId == id ? r.RelatedGsmMaterialId : r.PrimaryGsmMaterialId)
            .Select(r => new
            {
                r.Id,
                r.RelationType,
                PrimaryName = r.PrimaryGsmMaterial.Name,
                RelatedName = r.RelatedGsmMaterial.Name,
            })
            .FirstOrDefaultAsync(ct);

        if (activeRelation != null)
        {
            var role = activeRelation.PrimaryName == material.Name ? "основной" : "связанной";
            throw new InvalidOperationException(
                $"Нельзя удалить марку ГСМ: она является {role} маркой в активной связи «{activeRelation.PrimaryName} → {activeRelation.RelatedName}» " +
                $"({activeRelation.RelationType}). Сначала измените или удалите связь.");
        }

        material.IsDeleted = true;
        material.DeletedAt = _time.GetUtcNow().UtcDateTime;

        // Аудит в той же транзакции: ошибка аудита не должна оставить марку
        // удалённой без записи в журнале.
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "GsmMaterial",
            id.ToString(),
            "Delete",
            _currentUser.GetRequiredUserId(),
            EntityDisplayName: FormatDisplayName(material)), ct);

        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
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
    /// Диагностика расхождения <c>Gost</c>/<c>Nd</c> и
    /// <c>Description</c>/<c>IntendedUse</c>.
    /// <para>
    /// ВАЖНО, смысл отчёта изменился вместе с PR-5. Прежние поля больше не
    /// заполняются: <c>Gost</c> и <c>Description</c> заморожены на последних
    /// значениях и удаляются вместе с колонками в PR-6. Поэтому расхождение
    /// теперь НОРМАЛЬНО для любой марки, изменённой после PR-5, и НЕ является ни
    /// показателем качества данных, ни признаком незавершённого переноса.
    /// </para>
    /// <para>
    /// Отчёт остаётся ради одной проверки перед PR-6: он показывает, какие марки
    /// ещё отличаются, чтобы убедиться, что ни один действующий потребитель не
    /// читает переходные колонки. Данные по нему пересчитывать или «чинить» нельзя.
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

        // НД хранится в собственной колонке text. Предел проверяется здесь, до
        // записи: «поймать ошибку БД и сказать не удалось» не является исправлением.
        // Усечения нет — лишнее значение отклоняется понятным сообщением, а
        // принятое сохраняется полностью.
        var nd = NormalizeLegacyText(request.Nd);
        if (nd?.Length > NdMaxLength)
            throw new InvalidOperationException(
                $"НД не длиннее {NdMaxLength} символов. Укажите НД короче.");

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
    /// Переходные правила для legacy-колонок, которые остаются физически до PR-6.
    /// <list type="bullet">
    /// <item><c>Type</c> = подгруппа, первая по алфавиту. Колонка физически NOT
    /// NULL, поэтому её заполнение прекратить нельзя до PR-6 — это явное
    /// исключение из «полного отказа от записи в legacy-поля». Значение
    /// детерминированное, произвольная группа или фиктивный тип не подставляются.
    /// При нескольких подгруппах берётся первая ПО АЛФАВИТУ.</item>
    /// <item><c>Gost</c> и <c>Description</c> больше НЕ пишутся: после PR-5 ни
    /// один действующий потребитель их не читает. Их прежние значения остаются в
    /// базе как есть и удаляются вместе с колонками в PR-6. Это также убирает
    /// скрытое переполнение: прежнее <c>Gost</c> — <c>varchar(256)</c>, а
    /// <c>Nd</c> — <c>text</c>.</item>
    /// </list>
    /// У марки без классификации прежние значения не выдумываются: остаются
    /// те, что уже были.
    /// <para>
    /// Триггер <c>TRG_GsmMaterials_LegacyFieldSync</c> остаётся до PR-6 как
    /// страховка для отката на старую версию приложения: он срабатывает только
    /// когда <c>Nd</c>/<c>IntendedUse</c> явно не менялись, а значит не может
    /// переписать новое значение, записанное сервисом.
    /// </para>
    /// </summary>
    private static void ApplyLegacyMirrors(GsmMaterial material, NormalizedFields fields)
    {
        material.Nd = fields.Nd;
        material.IntendedUse = fields.IntendedUse;
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
