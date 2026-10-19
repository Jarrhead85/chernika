using Chernika.Domain.Enums;

namespace Chernika.Domain.Structure;

/// <summary>Элемент контейнера соседей: раздел, позиция или конечная строка.</summary>
public sealed record HKStructureSibling(
    Guid? NodeId, Guid? ItemId, Guid? SectionId, int SortOrder);

/// <summary>
/// Единая работа с порядком структуры: контейнеры соседей, перемещение и
/// нормализация.
/// <para>
/// Контейнер соседей — одно из трёх понятий: корень ХК, конкретный раздел или
/// конкретная структурная позиция. Внутри контейнера позиции и конечные строки
/// имеют ОДНУ совместную последовательность; раздел в корневом контейнере
/// делит порядок с самостоятельными ветками.
/// </para>
/// <para>
/// Раскрытие и сворачивание ветки сюда не входят: это состояние показа, а не
/// нормативного порядка, и на перемещения, номера и итоги оно не влияет.
/// </para>
/// </summary>
public static class HKStructureOrdering
{
    /// <summary>
    /// Соседи указанной записи в её контейнере. Раздел и самостоятельные ветки
    /// делят корневой порядок; потомки раздела и потомки позиции образуют один
    /// смешанный список.
    /// </summary>
    public static IReadOnlyList<HKStructureSibling> SiblingsOf(
        HKStructureInput input, Guid? nodeId, Guid? itemId)
    {
        var sectionIds = input.Sections.Select(s => s.Id).ToHashSet();

        if (nodeId is { } id)
        {
            var node = input.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is null)
                return Array.Empty<HKStructureSibling>();

            // Раздел у позиции допустим только у верхней позиции: вложенная
            // позиция наследует раздел корня и в контейнер раздела не входит.
            if (node.ParentId is null && node.SectionId is { } nodeSection
                && sectionIds.Contains(nodeSection))
            {
                return SiblingsOfSection(input, nodeSection);
            }

            return node.ParentId is { } parent
                ? SiblingsOfNode(input, parent)
                : SiblingsOfRoot(input);
        }

        if (itemId is { } itemId2)
        {
            var item = input.Items.FirstOrDefault(i => i.Id == itemId2);
            if (item is null)
                return Array.Empty<HKStructureSibling>();

            // Раздел у строки допустим только у строки под корнем.
            if (item.NodeId is null && item.SectionId is { } itemSection
                && sectionIds.Contains(itemSection))
            {
                return SiblingsOfSection(input, itemSection);
            }

            return item.NodeId is { } parent
                ? SiblingsOfNode(input, parent)
                : SiblingsOfRoot(input);
        }

        return Array.Empty<HKStructureSibling>();
    }

    /// <summary>Корневой контейнер: разделы и самостоятельные ветки, один список.</summary>
    public static IReadOnlyList<HKStructureSibling> SiblingsOfRoot(HKStructureInput input)
    {
        var sectionIds = input.Sections.Select(s => s.Id).ToHashSet();

        var siblings = new List<HKStructureSibling>();
        siblings.AddRange(input.Sections.Select(s =>
            new HKStructureSibling(null, null, s.Id, s.SortOrder)));

        siblings.AddRange(input.Nodes
            .Where(n => n.IsStandalone(sectionIds))
            .Select(n => new HKStructureSibling(n.Id, null, null, n.SortOrder)));

        siblings.AddRange(input.Items
            .Where(i => i.NodeId is null && i.IsStandalone(sectionIds))
            .Select(i => new HKStructureSibling(null, i.Id, null, i.SortOrder)));

        return siblings
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.SectionId is not null ? 0 : (s.NodeId is not null ? 1 : 2))
            .ThenBy(s => s.SectionId ?? s.NodeId ?? s.ItemId)
            .ToList();
    }

    /// <summary>Контейнер раздела: его верхние позиции и строки под корнем.</summary>
    public static IReadOnlyList<HKStructureSibling> SiblingsOfSection(
        HKStructureInput input, Guid sectionId)
    {
        var siblings = new List<HKStructureSibling>();

        siblings.AddRange(input.Nodes
            .Where(n => n.ParentId is null && n.SectionId == sectionId)
            .Select(n => new HKStructureSibling(n.Id, null, null, n.SortOrder)));

        siblings.AddRange(input.Items
            .Where(i => i.NodeId is null && i.SectionId == sectionId)
            .Select(i => new HKStructureSibling(null, i.Id, null, i.SortOrder)));

        return siblings
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.NodeId is not null ? 0 : 1)
            .ThenBy(s => s.NodeId ?? s.ItemId)
            .ToList();
    }

    /// <summary>Контейнер позиции: дочерние позиции и её конечные строки.</summary>
    public static IReadOnlyList<HKStructureSibling> SiblingsOfNode(
        HKStructureInput input, Guid nodeId)
    {
        var siblings = new List<HKStructureSibling>();

        siblings.AddRange(input.Nodes
            .Where(n => n.ParentId == nodeId)
            .Select(n => new HKStructureSibling(n.Id, null, null, n.SortOrder)));

        siblings.AddRange(input.Items
            .Where(i => i.NodeId == nodeId)
            .Select(i => new HKStructureSibling(null, i.Id, null, i.SortOrder)));

        return siblings
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.NodeId is not null ? 0 : 1)
            .ThenBy(s => s.NodeId ?? s.ItemId)
            .ToList();
    }

    /// <summary>
    /// Перемещение записи внутри её контейнера на <paramref name="delta"/>
    /// позиций (отрицательное — вверх). Возвращает обновлённый вход; сам вход
    /// не меняется. Переносы между контейнерами здесь не выполняются.
    /// </summary>
    public static HKStructureInput Move(
        HKStructureInput input, Guid? nodeId, Guid? itemId, int delta)
    {
        if (delta == 0)
            return input;

        var siblings = SiblingsOf(input, nodeId, itemId).ToList();
        if (siblings.Count < 2)
            return input;

        var index = siblings.FindIndex(s =>
            (nodeId is not null && s.NodeId == nodeId) ||
            (itemId is not null && s.ItemId == itemId));
        if (index < 0)
            return input;

        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= siblings.Count)
            return input;

        var current = siblings[index];
        var target = siblings[targetIndex];

        var nodes = new List<HKStructureNodeInput>(input.Nodes);
        var items = new List<HKStructureItemInput>(input.Items);
        var sections = new List<HKStructureSectionInput>(input.Sections);

        // Обмен значениями SortOrder пары соседей: остальной порядок контейнера
        // не трогается, номера стабильны.
        SwapOrder(current, target, nodes, items, sections);

        return input with { Sections = sections, Nodes = nodes, Items = items };
    }

    /// <summary>
    /// Приводит SortOrder к плотной последовательности 0..N-1 ОТДЕЛЬНО в каждом
    /// контейнере: корневом (разделы вместе с самостоятельными ветками), внутри
    /// каждого раздела и внутри каждой позиции. Дети раздела в корневой счётчик
    /// не попадают: у корневого контейнера и контейнера раздела свои шкалы.
    /// </summary>
    public static HKStructureInput Normalize(HKStructureInput input)
    {
        var nodes = new List<HKStructureNodeInput>(input.Nodes);
        var items = new List<HKStructureItemInput>(input.Items);
        var sections = new List<HKStructureSectionInput>(input.Sections);
        int order;

        // Корневой контейнер: один совместный счётчик.
        order = 0;
        foreach (var sibling in SiblingsOfRoot(input))
        {
            order = Assign(sibling, order, sections, nodes, items);
        }

        // Контейнеры разделов: своя шкала у каждого.
        foreach (var section in input.Sections)
        {
            order = 0;
            foreach (var sibling in SiblingsOfSection(input, section.Id))
            {
                order = Assign(sibling, order, sections, nodes, items);
            }
        }

        // Контейнеры позиций: своя шкала у каждой.
        foreach (var node in input.Nodes)
        {
            order = 0;
            foreach (var sibling in SiblingsOfNode(input, node.Id))
            {
                order = Assign(sibling, order, sections, nodes, items);
            }
        }

        return input with { Sections = sections, Nodes = nodes, Items = items };
    }

    private static int Assign(
        HKStructureSibling sibling, int order,
        List<HKStructureSectionInput> sections,
        List<HKStructureNodeInput> nodes,
        List<HKStructureItemInput> items)
    {
        if (sibling.SectionId is { } sectionId)
        {
            var i = sections.FindIndex(s => s.Id == sectionId);
            if (i >= 0)
                sections[i] = sections[i] with { SortOrder = order };
        }
        else if (sibling.NodeId is { } nodeId)
        {
            var i = nodes.FindIndex(n => n.Id == nodeId);
            if (i >= 0)
                nodes[i] = nodes[i] with { SortOrder = order };
        }
        else if (sibling.ItemId is { } itemId)
        {
            var i = items.FindIndex(x => x.Id == itemId);
            if (i >= 0)
                items[i] = items[i] with { SortOrder = order };
        }

        return order + 1;
    }

    private static void SwapOrder(
        HKStructureSibling a, HKStructureSibling b,
        List<HKStructureNodeInput> nodes, List<HKStructureItemInput> items,
        List<HKStructureSectionInput> sections)
    {
        foreach (var (from, to) in new[] { (a, b), (b, a) })
        {
            if (from.SectionId is { } sectionId)
            {
                var i = sections.FindIndex(s => s.Id == sectionId);
                sections[i] = sections[i] with { SortOrder = to.SortOrder };
            }
            else if (from.NodeId is { } nodeId)
            {
                var i = nodes.FindIndex(n => n.Id == nodeId);
                nodes[i] = nodes[i] with { SortOrder = to.SortOrder };
            }
            else if (from.ItemId is { } itemId)
            {
                var i = items.FindIndex(x => x.Id == itemId);
                items[i] = items[i] with { SortOrder = to.SortOrder };
            }
        }
    }

    private static bool IsStandalone(
        this HKStructureNodeInput node, HashSet<Guid> sectionIds) =>
        node.ParentId is null && (node.SectionId is null || !sectionIds.Contains(node.SectionId.Value));

    private static bool IsStandalone(
        this HKStructureItemInput item, HashSet<Guid> sectionIds) =>
        item.NodeId is null && (item.SectionId is null || !sectionIds.Contains(item.SectionId.Value));

}
