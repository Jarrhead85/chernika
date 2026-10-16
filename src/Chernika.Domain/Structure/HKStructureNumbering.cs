using Chernika.Domain.Enums;

namespace Chernika.Domain.Structure;

/// <summary>Вид строки в иерархическом представлении.</summary>
public enum HKStructureRowKind
{
    /// <summary>Заголовок раздела: визуальная группа без количества и норм.</summary>
    Section,

    /// <summary>Структурная позиция: изделие, агрегат или узел.</summary>
    Position,

    /// <summary>Конечная строка сборочной единицы.</summary>
    TerminalRow
}

/// <summary>Одна строка иерархического представления вместе со своим номером.</summary>
public sealed record HKStructureRow(
    string Number,
    int Depth,
    HKStructureRowKind Kind,
    Guid? NodeId,
    Guid? ItemId,
    Guid? SectionId,
    HKCardSectionType? SectionType,
    string Name,
    int Quantity);

/// <summary>
/// Иерархическая нумерация структуры.
/// <para>
/// Номер отражает фактический путь: раздел, если есть, → структурные родители →
/// конечная строка. Пропущенный уровень техники не создаёт пустой сегмент.
/// </para>
/// <para>
/// Номера вычисляются из структуры и порядка, не редактируются вручную и не
/// используются вместо идентификаторов. Количество позиции дополнительных
/// номеров не создаёт.
/// </para>
/// </summary>
public static class HKStructureNumbering
{
    /// <summary>
    /// Строит строки представления в отображаемом порядке.
    /// <para>
    /// Прямые потомки одного родителя — и структурные позиции, и конечные строки —
    /// упорядочиваются ОДНИМ списком, иначе визуальная последовательность
    /// менялась бы. При одинаковом SortOrder порядок детерминирован: позиции
    /// раньше строк, затем по идентификатору.
    /// </para>
    /// </summary>
    public static IReadOnlyList<HKStructureRow> Build(HKStructureInput input)
    {
        var rows = new List<HKStructureRow>();

        var sections = input.Sections
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToList();
        var sectionIds = sections.Select(s => s.Id).ToHashSet();

        var topNodes = input.Nodes.Where(n => n.ParentId is null).ToList();
        var topItems = input.Items.Where(i => i.NodeId is null).ToList();

        // Верхний порядок общий для разделов и самостоятельных веток без раздела:
        // раздел и ветка занимают места в одной последовательности.
        var tops = new List<TopEntry>();
        tops.AddRange(sections.Select(s => new TopEntry(s.SortOrder, s.Id, null, null)));

        // Позиция, уже принадлежащая разделу, самостоятельной веткой не является:
        // иначе она выводилась бы повторно под собственным номером.
        var standaloneNodes = topNodes
            .Where(n => n.SectionId is null || !sectionIds.Contains(n.SectionId.Value))
            .ToList();
        var standaloneItems = topItems
            .Where(i => i.SectionId is null || !sectionIds.Contains(i.SectionId.Value))
            .ToList();

        tops.AddRange(standaloneNodes.Select(n => new TopEntry(n.SortOrder, null, n.Id, null)));
        tops.AddRange(standaloneItems.Select(i => new TopEntry(i.SortOrder, null, null, i.Id)));

        // Разделы и самостоятельные ветки без раздела делят ОДНУ верхнюю
        // последовательность: иначе порядок зависел бы от того, отсортированы
        // ли они двумя независимыми списками.
        tops = tops
            .OrderBy(t => t.Order)
            .ThenBy(t => t.SectionId is not null ? 0 : 1)
            .ThenBy(t => t.SectionId ?? t.NodeId ?? t.ItemId)
            .ToList();

        var sectionById = sections.ToDictionary(s => s.Id);
        var nodeById = input.Nodes.ToDictionary(n => n.Id);
        var itemsByNode = input.Items
            .Where(i => i.NodeId is not null)
            .GroupBy(i => i.NodeId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var index = 0;
        foreach (var top in tops)
        {
            index++;

            if (top.SectionId is { } sectionKey)
            {
                var section = sectionById[sectionKey];

                // Потомки раздела берутся из его верхних позиций и строк под корнем.
                var childNodes = topNodes.Where(n => n.SectionId == section.Id).ToList();
                var childItems = topItems.Where(i => i.SectionId == section.Id).ToList();

                rows.Add(new HKStructureRow(
                    index.ToString(), 0, HKStructureRowKind.Section, null, null,
                    section.Id, section.SectionType,
                    HKSectionNames.NameOf(section.SectionType), 1));

                var childIndex = 0;
                foreach (var node in OrderNodes(childNodes))
                {
                    childIndex++;
                    Walk(node, $"{index}.{childIndex}", 1, section.Id, section.SectionType,
                        rows, nodeById, itemsByNode);
                }

                foreach (var item in OrderItems(childItems))
                {
                    childIndex++;
                    rows.Add(TerminalRow(item, $"{index}.{childIndex}", 1, section.Id, section.SectionType));
                }

                continue;
            }

            if (top.NodeId is { } nodeId)
            {
                Walk(nodeById[nodeId], index.ToString(), 0, null, null,
                    rows, nodeById, itemsByNode);
                continue;
            }

            var rootItem = topItems.First(i => i.Id == top.ItemId!.Value);
            rows.Add(TerminalRow(rootItem, index.ToString(), 0, null, null));
        }

        return rows;
    }

    /// <summary>Потомок одного родителя: позиция либо конечная строка.</summary>
    private sealed record ChildEntry(
        int Order, HKStructureNodeInput? Node, HKStructureItemInput? Item);

    /// <summary>Запись верхнего уровня: раздел либо самостоятельная ветка.</summary>
    private sealed record TopEntry(int Order, Guid? SectionId, Guid? NodeId, Guid? ItemId);

    private static void Walk(
        HKStructureNodeInput node, string number, int depth, Guid? sectionId,
        HKCardSectionType? sectionType,
        List<HKStructureRow> rows,
        Dictionary<Guid, HKStructureNodeInput> nodeById,
        Dictionary<Guid, List<HKStructureItemInput>> itemsByNode)
    {
        rows.Add(new HKStructureRow(
            number, depth, HKStructureRowKind.Position, node.Id, null,
            sectionId, sectionType, node.Name, node.Quantity));

        // Потомки позиции наследуют её раздел; собственный раздел у вложенной
        // позиции не хранится.
        var children = nodeById.Values
            .Where(n => n.ParentId == node.Id)
            .OrderBy(n => n.SortOrder)
            .ThenBy(n => n.Id)
            .ToList();

        var childItems = itemsByNode.TryGetValue(node.Id, out var list)
            ? list.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList()
            : new List<HKStructureItemInput>();

        // Совместный порядок позиций и строк одного родителя: это один список,
        // а не две независимо отсортированные последовательности.
        var ordered = new List<ChildEntry>();
        ordered.AddRange(children.Select(n => new ChildEntry(n.SortOrder, n, null)));
        ordered.AddRange(childItems.Select(i => new ChildEntry(i.SortOrder, null, i)));

        ordered = ordered
            .OrderBy(t => t.Order)
            .ThenBy(t => t.Node is not null ? 0 : 1)
            .ThenBy(t => t.Node?.Id ?? t.Item!.Id)
            .ToList();

        var index = 0;
        foreach (var entry in ordered)
        {
            index++;

            if (entry.Node is not null)
            {
                Walk(entry.Node!, $"{number}.{index}", depth + 1, sectionId, sectionType,
                    rows, nodeById, itemsByNode);
            }
            else
            {
                rows.Add(TerminalRow(entry.Item!, $"{number}.{index}", depth + 1,
                    sectionId, sectionType));
            }
        }
    }

    private static List<HKStructureNodeInput> OrderNodes(IEnumerable<HKStructureNodeInput> nodes) =>
        nodes.OrderBy(n => n.SortOrder).ThenBy(n => n.Id).ToList();

    private static List<HKStructureItemInput> OrderItems(IEnumerable<HKStructureItemInput> items) =>
        items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList();

    private static HKStructureRow TerminalRow(
        HKStructureItemInput item, string number, int depth,
        Guid? sectionId, HKCardSectionType? sectionType) =>
        new(number, depth, HKStructureRowKind.TerminalRow, null, item.Id,
            sectionId, sectionType, item.UnitOfMeasure ?? string.Empty, item.Quantity);
}

/// <summary>Отображаемые названия разделов.</summary>
public static class HKSectionNames
{
    public static string NameOf(HKCardSectionType type) => type switch
    {
        HKCardSectionType.Oils => "Масла",
        HKCardSectionType.Greases => "Смазки",
        HKCardSectionType.Fuels => "Топлива",
        _ => type.ToString()
    };
}