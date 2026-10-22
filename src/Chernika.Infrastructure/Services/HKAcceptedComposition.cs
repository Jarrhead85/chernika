using Chernika.Domain.Enums;
using Chernika.Domain.Leveling;
using Chernika.Domain.Structure;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Вклад конечной строки в итог позиции — расшифровка расчёта.
/// <para>
/// Исходные значения, множитель и результат в базовой единице. Всё берётся
/// из снимка принятой редакции.
/// </para>
/// </summary>
public sealed record HKAcceptedCompositionContributionDto(
    string? Number,
    string? Name,
    decimal Volume,
    string? UnitOfMeasure,
    int Quantity,
    decimal Multiplier,
    decimal BaseValue,
    bool IsComplete,
    IReadOnlyList<string> Diagnostics);

/// <summary>Строка дерева принятой редакции.</summary>
public sealed record HKAcceptedCompositionRowDto(
    string Number,
    int Depth,
    HKStructureRowKind Kind,
    Guid? NodeId,
    Guid? ItemId,
    Guid? SectionId,
    string? Name,
    string? LevelTitle,
    int? QuantityInParent,
    int InstancesInCard,
    IReadOnlyList<HKMeasuredTotal> PerOne,
    IReadOnlyList<HKMeasuredTotal> TotalInCard,
    IReadOnlyList<HKAcceptedCompositionContributionDto> Contributions,
    /// <summary>
    /// Ключ виртуальной группы «Без раздела».
    /// <para>
    /// У каждой группы свой ключ — он указывает на первую самостоятельную ветку
    /// блока. Общий ключ у всех групп означал бы одинаковые @key в дереве, а
    /// поиск и сворачивание не должны менять идентичность строк.
    /// </para>
    /// </summary>
    string? GroupKey = null);

/// <summary>Итог раздела принятой редакции.</summary>
public sealed record HKAcceptedCompositionSectionDto(
    Guid SectionId,
    string Name,
    HKCardSectionType? SectionType,
    IReadOnlyList<HKMeasuredTotal> Totals,
    bool IsComplete,
    /// <summary>Структурных позиций в разделе.</summary>
    int PositionCount,
    /// <summary>Конечных строк СЕ в разделе.</summary>
    int ItemCount);

/// <summary>
/// Принятая редакция карты целиком: реквизиты снимка, дерево и итоги.
/// <para>
/// Дерево и итоги относятся к ОДНОЙ редакции: указатель читается один раз,
/// иначе при смене указателя между чтениями смешались бы разные редакции.
/// </para>
/// </summary>
public sealed record HKAcceptedCompositionDto(
    Guid CardId,
    Guid RevisionId,
    int RevisionNumber,
    string? Code,
    string? Name,
    string? Version,
    DateTime? AcceptedAtUtc,
    string? AcceptedByUserId,
    int PositionCount,
    int ItemCount,
    IReadOnlyList<HKAcceptedCompositionSectionDto> Sections,
    IReadOnlyList<HKMeasuredTotal> Overall,
    bool IsComplete,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<HKAcceptedCompositionRowDto> Rows,
    /// <summary>
    /// Конечные строки снимка: код и наименование СЕ, норма, периодичность,
    /// примечание и марки ГСМ по категориям.
    /// <para>
    /// Названия и НД берутся ИЗ СНИМКА: переименование справочной СЕ или
    /// марки ГСМ не должно менять отображение принятой структуры.
    /// </para>
    /// </summary>
    IReadOnlyList<HKAcceptedItemDto> Items);

/// <summary>Строка реестра составов ХК.</summary>
public sealed record HKAcceptedCompositionListItemDto(
    Guid CardId,
    string? Code,
    string? Name,
    string? Version,
    int RevisionNumber,
    DateTime? AcceptedAtUtc,
    IReadOnlyList<HKMeasuredTotal> Totals,
    int PositionCount,
    int ItemCount,
    /// <summary>
    /// Уровень карты.
    /// <para>
    /// У автономной карты он выводится из ПРИНЯТОЙ структуры: уровень карты
    /// на ступень выше уровня её старшей верхней позиции. У карты со
    /// справочным объектом — уровень этого объекта.
    /// </para>
    /// </summary>
    HKObjectLevel? Level,
    bool IsAutonomous);

/// <summary>
/// Построение read-only представления принятой редакции.
/// <para>
/// Расчётные механизмы — те же, что и у рабочего дерева: нумерация, итоги и
/// единицы берутся из доменного слоя, формулы не дублируются.
/// </para>
/// </summary>
public static class HKAcceptedCompositionProjection
{
    /// <summary>Собирает представление принятой редакции по её снимку.</summary>
    public static HKAcceptedCompositionDto Build(
        Guid cardId, HKAcceptedStructureDto revision)
    {
        var input = ToInput(revision);
        var rows = HKStructureNumbering.Build(input);
        var totals = HKStructureTotals.Calculate(input);

        var nodesById = revision.Nodes.ToDictionary(n => n.Id);
        var itemsById = revision.Items.ToDictionary(i => i.Id);
        var sectionNames = revision.Sections.ToDictionary(s => s.SectionId, s => s.Name);

        var sectionTotals = totals.Sections.ToDictionary(s => s.SectionId, s => s.Totals);
        var withoutSectionTotals = totals.WithoutSection?.Totals ?? Array.Empty<HKMeasuredTotal>();

        var result = new List<HKAcceptedCompositionRowDto>();
        var withoutSectionOpen = false;

        foreach (var row in rows)
        {
            if (row.Kind == HKStructureRowKind.Section)
            {
                // Раздел закрывает блок «Без раздела»: следующий блок веток
                // без раздела получит собственную виртуальную группу.
                withoutSectionOpen = false;

                // У раздела нет собственного количества и нормы на экземпляр:
                // его итог — это итог раздела целиком.
                result.Add(new HKAcceptedCompositionRowDto(
                    row.Number, row.Depth, row.Kind, null, null, row.SectionId,
                    row.Name, null, null, 0,
                    Array.Empty<HKMeasuredTotal>(),
                    sectionTotals.TryGetValue(row.SectionId!.Value, out var sectionTotal)
                        ? sectionTotal
                        : Array.Empty<HKMeasuredTotal>(),
                    Array.Empty<HKAcceptedCompositionContributionDto>()));

                continue;
            }

            // Самостоятельная ветка без раздела: перед ней открывается группа
            // «Без раздела». Это только группировка показа — фиктивной записи
            // в БД у неё нет, нумерация не меняется.
            //
            // Группа открывается только на верхнем уровне: вложенные строки
            // такой ветки остаются внутри неё и своих заголовков не создают.
            if (row.SectionId is null && row.Depth == 0 && !withoutSectionOpen)
            {
                withoutSectionOpen = true;
                result.Add(WithoutSectionGroup(row));
            }

            if (row.Kind == HKStructureRowKind.Position && row.NodeId is { } nodeId)
            {
                var node = nodesById[nodeId];
                var position = HKStructureTotals.CalculateNode(input, nodeId);

                result.Add(new HKAcceptedCompositionRowDto(
                    row.Number, row.Depth, row.Kind, row.NodeId, null, row.SectionId,
                    node.Name,
                    HKLevelTitles.NameOf(node.Level),
                    node.Quantity,
                    HKStructureTotals.InstancesOfNode(input, nodeId),
                    position.PerOne,
                    position.TotalInCard,
                    BuildContributions(
                        HKStructureTotals.ContributionsOfNode(input, nodeId),
                        revision, rows, itemsById)));

                continue;
            }

            if (row.ItemId is { } itemId)
            {
                var item = itemsById[itemId];
                var itemTotals = HKStructureTotals.CalculateItem(input, itemId);

                result.Add(new HKAcceptedCompositionRowDto(
                    row.Number, row.Depth, row.Kind, null, row.ItemId, row.SectionId,
                    ItemTitle(item),
                    "СЕ",
                    item.Quantity,
                    HKStructureTotals.InstancesOfItem(input, itemId),
                    itemTotals.PerOne,
                    itemTotals.TotalInCard,
                    BuildContributions(
                        HKStructureTotals.ContributionsOfItem(input, itemId),
                        revision, rows, itemsById)));
            }
        }

        // Эффективный раздел записи выводится по родителям, а не по порядку
        // записей: верхняя позиция и корневая строка СЕ берут свой раздел,
        // вложенные наследуют раздел своего корня. Отсутствие родителя в
        // промежуточном кэше не доказывает отсутствие раздела.
        var effective = ResolveEffectiveSections(
            revision.Nodes,
            revision.Items,
            revision.Sections.Select(s => s.SectionId).ToHashSet());

        // Счётчики разделов: каждая снимковая позиция и каждая конечная
        // строка учитываются ровно один раз. Это количество записей
        // структуры, а не сумма Quantity и не число экземпляров в ХК.
        var positionCountBySection = new Dictionary<Guid, int>();
        var itemCountBySection = new Dictionary<Guid, int>();
        var withoutSectionPositions = 0;
        var withoutSectionItems = 0;

        foreach (var node in revision.Nodes)
        {
            var section = effective.NodeSections.TryGetValue(node.Id, out var own) ? own : null;
            if (section is { } sectionId)
                positionCountBySection[sectionId] = positionCountBySection.GetValueOrDefault(sectionId) + 1;
            else
                withoutSectionPositions++;
        }

        foreach (var item in revision.Items)
        {
            var section = effective.ItemSections.TryGetValue(item.Id, out var own) ? own : null;
            if (section is { } sectionId)
                itemCountBySection[sectionId] = itemCountBySection.GetValueOrDefault(sectionId) + 1;
            else
                withoutSectionItems++;
        }

        var sections = new List<HKAcceptedCompositionSectionDto>();
        foreach (var section in revision.Sections.OrderBy(s => s.SortOrder).ThenBy(s => s.SectionId))
        {
            var sectionTotal = sectionTotals.TryGetValue(section.SectionId, out var t)
                ? t
                : Array.Empty<HKMeasuredTotal>();

            sections.Add(new HKAcceptedCompositionSectionDto(
                section.SectionId,
                section.Name,
                section.SectionType,
                sectionTotal,
                sectionTotal.All(t => t.IsComplete),
                positionCountBySection.TryGetValue(section.SectionId, out var positions) ? positions : 0,
                itemCountBySection.TryGetValue(section.SectionId, out var items) ? items : 0));
        }

        // «Без раздела» показывается, только если есть соответствующие ветки.
        // В группу входят и вложенные позиции таких веток: они тоже остались
        // без раздела.
        if (withoutSectionPositions > 0 || withoutSectionItems > 0)
        {
            sections.Add(new HKAcceptedCompositionSectionDto(
                Guid.Empty, "Без раздела", null, withoutSectionTotals,
                withoutSectionTotals.All(t => t.IsComplete),
                withoutSectionPositions,
                withoutSectionItems));
        }

        var diagnostics = totals.Overall
            .Where(t => !t.IsComplete)
            .SelectMany(t => t.Diagnostics)
            .Concat(effective.Diagnostics)
            .Distinct()
            .ToList();

        // Повреждённая структура не выдаётся за корректную: итог помечается
        // неполным, а причины перечисляются в диагностике.
        var isComplete = totals.Overall.All(t => t.IsComplete) && effective.Diagnostics.Count == 0;

        return new HKAcceptedCompositionDto(
            cardId,
            revision.RevisionId,
            revision.RevisionNumber,
            revision.Code,
            revision.Name,
            revision.Version,
            revision.AcceptedAtUtc,
            revision.AcceptedByUserId,
            revision.Nodes.Count,
            revision.Items.Count,
            sections,
            totals.Overall,
            isComplete,
            diagnostics,
            result,
            revision.Items);
    }

    /// <summary>
    /// Виртуальная группа «Без раздела» перед блоком самостоятельных веток.
    /// <para>
    /// Заголовок не имеет номера и своей записи в БД: это только группировка
    /// показа. Ключ группы указывает на первую ветку блока — у каждой группы
    /// он свой.
    /// </para>
    /// </summary>
    private static HKAcceptedCompositionRowDto WithoutSectionGroup(HKStructureRow row) =>
        new(string.Empty, row.Depth, HKStructureRowKind.Section, null, null, null,
            "Без раздела", null, null, 0,
            Array.Empty<HKMeasuredTotal>(),
            Array.Empty<HKMeasuredTotal>(),
            Array.Empty<HKAcceptedCompositionContributionDto>(),
            GroupKey: row.NodeId is { } nodeId ? $"node:{nodeId}" : $"item:{row.ItemId}");

    /// <summary>Эффективные разделы записей снимка и диагностики повреждений.</summary>
    private sealed record EffectiveSections(
        IReadOnlyDictionary<Guid, Guid?> NodeSections,
        IReadOnlyDictionary<Guid, Guid?> ItemSections,
        IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// Раздел каждой позиции и каждой конечной строки снимка.
    /// <para>
    /// Раздел выводится по родителям и не зависит от порядка записей: верхняя
    /// позиция и корневая строка СЕ берут свой раздел, вложенные наследуют
    /// раздел своего корня. Повреждённая структура — цикл или отсутствующий
    /// родитель — не превращается молча в корректную группу «Без раздела»:
    /// она перечисляется в диагностике.
    /// </para>
    /// </summary>
    private static EffectiveSections ResolveEffectiveSections(
        IReadOnlyList<HKAcceptedNodeDto> nodes,
        IReadOnlyList<HKAcceptedItemDto> items,
        IReadOnlySet<Guid> sectionIds)
    {
        var nodesById = nodes.ToDictionary(n => n.Id);
        var nodeSections = new Dictionary<Guid, Guid?>();
        var diagnostics = new List<string>();

        foreach (var node in nodes)
        {
            if (nodeSections.ContainsKey(node.Id))
                continue;

            ResolveNodeSection(
                node, nodesById, sectionIds, nodeSections, diagnostics, new HashSet<Guid>());
        }

        var itemSections = new Dictionary<Guid, Guid?>();

        foreach (var item in items)
        {
            Guid? section;

            if (item.NodeId is { } nodeId)
            {
                // Строка под позицией наследует раздел этой позиции.
                if (nodesById.ContainsKey(nodeId))
                    section = nodeSections.TryGetValue(nodeId, out var nodeSection) ? nodeSection : null;
                else
                {
                    diagnostics.Add(
                        $"Конечная строка «{ItemTitle(item)}» ссылается на позицию, "
                        + "которой нет в снимке принятой редакции.");
                    section = null;
                }
            }
            else
            {
                // Корневая строка СЕ использует собственный раздел.
                section = OwnSection(item.SectionId, sectionIds);
            }

            itemSections[item.Id] = section;
        }

        return new EffectiveSections(nodeSections, itemSections, diagnostics);
    }

    /// <summary>
    /// Раздел позиции: собственный у верхней, унаследованный у вложенной.
    /// <para>
    /// Обход защищён от цикла и от отсутствующего родителя: повреждённая
    /// структура помечается диагностикой, а не молча считается безраздельной.
    /// </para>
    /// </summary>
    private static Guid? ResolveNodeSection(
        HKAcceptedNodeDto node,
        Dictionary<Guid, HKAcceptedNodeDto> nodesById,
        IReadOnlySet<Guid> sectionIds,
        Dictionary<Guid, Guid?> nodeSections,
        List<string> diagnostics,
        HashSet<Guid> visiting)
    {
        if (nodeSections.TryGetValue(node.Id, out var known))
            return known;

        if (!visiting.Add(node.Id))
        {
            diagnostics.Add($"Позиция «{node.Name}» замыкает цикл в структуре снимка.");
            nodeSections[node.Id] = null;
            return null;
        }

        Guid? section;

        if (node.ParentId is null)
        {
            section = OwnSection(node.SectionId, sectionIds);
        }
        else if (!nodesById.TryGetValue(node.ParentId.Value, out var parent))
        {
            diagnostics.Add(
                $"У позиции «{node.Name}» отсутствует родитель в снимке принятой редакции.");
            section = null;
        }
        else
        {
            section = ResolveNodeSection(
                parent, nodesById, sectionIds, nodeSections, diagnostics, visiting);
        }

        visiting.Remove(node.Id);
        nodeSections[node.Id] = section;

        return section;
    }

    /// <summary>
    /// Собственный раздел записи, если он есть среди разделов снимка.
    /// <para>
    /// Ссылка на несуществующий раздел не превращается в раздел: такая запись
    /// остаётся без раздела, как её показывает нумерация и считают итоги.
    /// </para>
    /// </summary>
    private static Guid? OwnSection(Guid? sectionId, IReadOnlySet<Guid> sectionIds) =>
        sectionId is { } id && sectionIds.Contains(id) ? id : null;

    /// <summary>
    /// Уровень карты по её принятой структуре.
    /// <para>
    /// Используется то же доменное правило, что и при сохранении структуры:
    /// уровень карты на ступень выше уровня её старшей верхней позиции
    /// (верхнее изделие даёт ХК комплекса, верхний агрегат — ХК изделия).
    /// Старший верхний узел требует явного выбора уровня корня.
    /// </para>
    /// <para>
    /// null — уровень однозначно не определяется: пустая структура,
    /// недопустимая структура или не сделанный выбор уровня корня.
    /// </para>
    /// </summary>
    public static HKObjectLevel? CardLevel(
        IReadOnlyList<HKAcceptedNodeDto> nodes,
        IReadOnlyList<HKAcceptedItemDto> items,
        HKObjectLevel? nodeRootChoice = null)
    {
        var shape = new HKTreeShape(
            nodes.Select(n => new HKTreePosition(n.Id, n.ParentId, n.Level)).ToList(),
            items.Count(i => i.NodeId is null));

        var resolved = HKCardLevelRule.Resolve(shape, nodeRootChoice);

        return resolved.Status == HKCardLevelStatus.Determined ? resolved.CardLevel : null;
    }

    /// <summary>Название конечной строки: код и наименование СЕ из снимка.</summary>
    private static string ItemTitle(HKAcceptedItemDto item)
    {
        var code = string.IsNullOrWhiteSpace(item.AssemblyUnitCode) ? null : item.AssemblyUnitCode;
        var name = string.IsNullOrWhiteSpace(item.AssemblyUnitName) ? null : item.AssemblyUnitName;

        if (code is null && name is null)
            return "Сборочная единица";

        return code is null ? name! : name is null ? code : $"{code} — {name}";
    }

    private static IReadOnlyList<HKAcceptedCompositionContributionDto> BuildContributions(
        IReadOnlyList<HKContribution> contributions,
        HKAcceptedStructureDto revision,
        IReadOnlyList<HKStructureRow> rows,
        IReadOnlyDictionary<Guid, HKAcceptedItemDto> itemsById)
    {
        var numberByItem = rows
            .Where(r => r.ItemId is not null)
            .ToDictionary(r => r.ItemId!.Value, r => r.Number);

        return contributions
            .Select(c =>
            {
                var item = itemsById.TryGetValue(c.ItemId, out var found) ? found : null;

                return new HKAcceptedCompositionContributionDto(
                    numberByItem.TryGetValue(c.ItemId, out var number) ? number : null,
                    item is null ? null : ItemTitle(item),
                    c.Volume,
                    c.UnitOfMeasure,
                    c.Quantity,
                    c.Multiplier,
                    c.BaseValue,
                    c.IsComplete,
                    c.Diagnostics);
            })
            .ToList();
    }

    private static HKStructureInput ToInput(HKAcceptedStructureDto revision)
    {
        var sections = revision.Sections
            .Select(s => new HKStructureSectionInput(s.SectionId, s.SectionType, s.SortOrder))
            .ToList();

        var nodes = revision.Nodes
            .Select(n => new HKStructureNodeInput(
                n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder, n.SectionId))
            .ToList();

        var items = revision.Items
            .Select(i => new HKStructureItemInput(
                i.Id, i.NodeId, i.Quantity, i.Volume, i.UnitOfMeasure, i.SortOrder, i.SectionId))
            .ToList();

        return new HKStructureInput(sections, nodes, items);
    }
}
