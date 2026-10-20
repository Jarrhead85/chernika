using Chernika.Domain.Enums;
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
    IReadOnlyList<HKAcceptedCompositionContributionDto> Contributions);

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
    int ItemCount);

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

            if (row.Kind == HKStructureRowKind.Position && row.SectionId is null && row.Depth == 0)
            {
                // (группа «Без раздела» открывается ниже)
                // Самостоятельная ветка без раздела: перед ней видна группа
                // «Без раздела». Это только группировка показа — фиктивной
                // записи в БД у неё нет, нумерация не меняется.
                if (!withoutSectionOpen)
                {
                    withoutSectionOpen = true;
                    result.Add(new HKAcceptedCompositionRowDto(
                        string.Empty, row.Depth, HKStructureRowKind.Section, null, null, null,
                        "Без раздела", null, null, 0,
                        Array.Empty<HKMeasuredTotal>(),
                        Array.Empty<HKMeasuredTotal>(),
                        Array.Empty<HKAcceptedCompositionContributionDto>()));
                }
            }
            else if (row.Kind != HKStructureRowKind.Section)
            {
                withoutSectionOpen = false;
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

                // Группа «Без раздела» — видимая виртуальная группировка:
                // фиктивной записи в БД у неё нет, нумерация не меняется.
                if (row.SectionId is null)
                {
                    if (!withoutSectionOpen)
                    {
                        withoutSectionOpen = true;
                        result.Add(new HKAcceptedCompositionRowDto(
                            string.Empty, row.Depth, HKStructureRowKind.Section, null, null, null,
                            "Без раздела", null, null, 0,
                            Array.Empty<HKMeasuredTotal>(),
                            Array.Empty<HKMeasuredTotal>(),
                            Array.Empty<HKAcceptedCompositionContributionDto>()));
                    }
                }
                else
                {
                    withoutSectionOpen = false;
                }

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

        // Эффективный раздел: вложенная позиция наследует раздел своего
        // корня, поэтому счётчики раздела включают и вложенные позиции.
        var effectiveSectionByNode = new Dictionary<Guid, Guid?>();
        foreach (var node in revision.Nodes.OrderBy(n => n.SortOrder))
        {
            if (node.SectionId is { } own)
            {
                effectiveSectionByNode[node.Id] = own;
                continue;
            }

            Guid? parentSection = null;
            var parentId = node.ParentId;
            var guard = 0;
            while (parentId is { } id && guard++ <= revision.Nodes.Count)
            {
                if (effectiveSectionByNode.TryGetValue(id, out var known))
                {
                    parentSection = known;
                    break;
                }

                var step = revision.Nodes.FirstOrDefault(n => n.Id == id);
                if (step is null)
                    break;

                parentId = step.ParentId;
            }

            effectiveSectionByNode[node.Id] = parentSection;
        }

        var positionCountBySection = revision.Nodes
            .Where(n => effectiveSectionByNode.TryGetValue(n.Id, out var section) && section is not null)
            .GroupBy(n => effectiveSectionByNode[n.Id]!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var itemCountBySection = revision.Items
            .Where(i => i.NodeId is { } nodeId && effectiveSectionByNode.TryGetValue(nodeId, out var section) && section is not null)
            .GroupBy(i => effectiveSectionByNode[i.NodeId!.Value]!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var withoutSectionPositions = revision.Nodes
            .Count(n => effectiveSectionByNode.TryGetValue(n.Id, out var section) && section is null);
        var withoutSectionItems = revision.Items
            .Count(i => i.NodeId is null && i.SectionId is null);

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
        var hasWithoutSection = revision.Nodes.Any(n => n.ParentId is null && n.SectionId is null)
            || revision.Items.Any(i => i.NodeId is null && i.SectionId is null);

        if (hasWithoutSection)
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
            .Distinct()
            .ToList();

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
            totals.Overall.All(t => t.IsComplete),
            diagnostics,
            result,
            revision.Items);
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
