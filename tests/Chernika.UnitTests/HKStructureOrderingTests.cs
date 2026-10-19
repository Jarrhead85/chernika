using Chernika.Domain.Enums;
using Chernika.Domain.Structure;

namespace Chernika.UnitTests;

/// <summary>
/// Единая механика порядка: контейнеры соседей, перемещение и нормализация.
/// <para>
/// Проверяется чистая доменная механика: список настоящих соседей определяет
/// и возможность переноса, и его результат; раскрытие и сворачивание ветки на
/// порядок не влияют и в этих тестах не участвуют.
/// </para>
/// </summary>
public class HKStructureOrderingTests
{
    private static readonly Guid SectionOils = Guid.NewGuid();

    private static HKStructureInput Input(
        IEnumerable<HKStructureSectionInput>? sections = null,
        IEnumerable<HKStructureNodeInput>? nodes = null,
        IEnumerable<HKStructureItemInput>? items = null) =>
        new(
            sections?.ToList() ?? new List<HKStructureSectionInput>(),
            nodes?.ToList() ?? new List<HKStructureNodeInput>(),
            items?.ToList() ?? new List<HKStructureItemInput>());

    private static HKStructureNodeInput Node(
        HKObjectLevel level, Guid? parent = null, int sort = 0,
        Guid? section = null, int quantity = 1, string name = "Позиция") =>
        new(Guid.NewGuid(), parent, level, name, quantity, sort, section);

    private static HKStructureItemInput Item(
        Guid? node = null, int quantity = 1, decimal volume = 1m,
        string? unit = "кг", int sort = 0, Guid? section = null) =>
        new(Guid.NewGuid(), node, quantity, volume, unit, sort, section);

    // ── Контейнеры соседей ──

    /// <summary>Верхний контейнер: разделы и самостоятельные ветки, один список.</summary>
    [Fact]
    public void SiblingsOfRoot_MixesSectionsAndStandaloneBranches()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 1);
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };
        var input = Input(sections, new[] { aggregate }, new[] { Item(sort: 2) });

        var siblings = HKStructureOrdering.SiblingsOfRoot(input);

        // Раздел, позиция и строка под корнем: один совместный счётчик.
        Assert.Equal(3, siblings.Count);
        Assert.Equal(SectionOils, siblings[0].SectionId);
        Assert.Equal(aggregate.Id, siblings[1].NodeId);
        Assert.Equal(siblings[2].ItemId, input.Items.Single().Id);
    }

    /// <summary>
    /// Соседи элемента раздела — верхние позиции и строки под корнем этого
    /// раздела, а НЕ плоский порядок видимого дерева.
    /// </summary>
    [Fact]
    public void SiblingsOf_SectionChild_ReturnsOnlySectionContainer()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };
        var inSection = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var standalone = Node(HKObjectLevel.Aggregate, sort: 1);
        var input = Input(sections, new[] { inSection, standalone });

        var siblings = HKStructureOrdering.SiblingsOf(input, inSection.Id, null);

        var sibling = Assert.Single(siblings);
        Assert.Equal(inSection.Id, sibling.NodeId);
    }

    /// <summary>
    /// Соседи строки, стоящей между позициями раздела, — элементы контейнера
    /// раздела, и перенос идёт по этому списку.
    /// </summary>
    [Fact]
    public void SiblingsOf_ItemBetweenSectionPositions_UsesSectionContainer()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };
        var first = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var second = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 2);
        var middle = Item(section: SectionOils, sort: 1);
        var input = Input(sections, new[] { first, second }, new[] { middle });

        var siblings = HKStructureOrdering.SiblingsOf(input, null, middle.Id);

        Assert.Equal(3, siblings.Count);
        Assert.Equal(middle.Id, siblings[1].ItemId);
    }

    /// <summary>Соседи строки под позицией — потомки этой позиции.</summary>
    [Fact]
    public void SiblingsOf_ItemUnderNode_UsesNodeContainer()
    {
        var parent = Node(HKObjectLevel.Aggregate);
        var child = Node(HKObjectLevel.Node, parent: parent.Id, sort: 1);
        var line = Item(node: parent.Id, sort: 0);
        var input = Input(null, new[] { parent, child }, new[] { line });

        var siblings = HKStructureOrdering.SiblingsOf(input, null, line.Id);

        Assert.Equal(2, siblings.Count);
        Assert.Equal(line.Id, siblings[0].ItemId);
        Assert.Equal(child.Id, siblings[1].NodeId);
    }

    /// <summary>Соседи вложенной позиции — потомки её родителя.</summary>
    [Fact]
    public void SiblingsOf_NestedNode_UsesParentContainer()
    {
        var parent = Node(HKObjectLevel.Aggregate);
        var child = Node(HKObjectLevel.Node, parent: parent.Id, sort: 0);
        var input = Input(null, new[] { parent, child });

        var siblings = HKStructureOrdering.SiblingsOf(input, child.Id, null);

        var sibling = Assert.Single(siblings);
        Assert.Equal(child.Id, sibling.NodeId);
    }

    // ── Перемещение ──

    /// <summary>Перемещение меняет местами только пару соседей.</summary>
    [Fact]
    public void Move_SwapsNeighborsOnly()
    {
        var first = Node(HKObjectLevel.Aggregate, sort: 0);
        var second = Node(HKObjectLevel.Aggregate, sort: 1);
        var third = Node(HKObjectLevel.Aggregate, sort: 2);
        var input = Input(nodes: new[] { first, second, third });

        var moved = HKStructureOrdering.Move(input, second.Id, null, -1);

        Assert.Equal(1, moved.Nodes.Single(n => n.Id == first.Id).SortOrder);
        Assert.Equal(0, moved.Nodes.Single(n => n.Id == second.Id).SortOrder);
        Assert.Equal(2, moved.Nodes.Single(n => n.Id == third.Id).SortOrder);
        // Исходный вход неизменяем: у второго на входе остаётся SortOrder 1.
        Assert.Equal(1, input.Nodes.Single(n => n.Id == second.Id).SortOrder);
    }

    /// <summary>Строка между позициями раздела переносится по общему списку.</summary>
    [Fact]
    public void Move_SectionItem_UpSwapsWithPrecedingPosition()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };
        var position = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var line = Item(section: SectionOils, sort: 1);
        var input = Input(sections, new[] { position }, new[] { line });

        var moved = HKStructureOrdering.Move(input, null, line.Id, -1);

        Assert.Equal(1, moved.Nodes.Single(n => n.Id == position.Id).SortOrder);
        Assert.Equal(0, moved.Items.Single(i => i.Id == line.Id).SortOrder);
    }

    /// <summary>Крайних соседей нет: перенос за границу ничего не меняет.</summary>
    [Fact]
    public void Move_OutsideContainer_ReturnsUnchanged()
    {
        var first = Node(HKObjectLevel.Aggregate, sort: 0);
        var input = Input(nodes: new[] { first });

        var moved = HKStructureOrdering.Move(input, first.Id, null, -1);

        Assert.Same(input, moved);
    }

    /// <summary>Потомки не влияют: перенос позиции не меняет порядок детей.</summary>
    [Fact]
    public void Move_Parent_DoesNotTouchChildren()
    {
        var first = Node(HKObjectLevel.Aggregate, sort: 0);
        var second = Node(HKObjectLevel.Aggregate, sort: 1);
        var child = Node(HKObjectLevel.Node, parent: second.Id, sort: 0);
        var input = Input(nodes: new[] { first, second, child });

        var moved = HKStructureOrdering.Move(input, first.Id, null, 1);

        Assert.Equal(0, moved.Nodes.Single(n => n.Id == child.Id).SortOrder);
        Assert.Equal(0, moved.Nodes.Single(n => n.Id == second.Id).SortOrder);
        // Переместившаяся позиция получила порядок соседа.
        Assert.Equal(1, moved.Nodes.Single(n => n.Id == first.Id).SortOrder);
    }

    // ── Нормализация ──

    /// <summary>
    /// Нормализация ведёт отдельные шкалы: корневой контейнер, каждый раздел и
    /// каждая позиция. Дети раздела в корневой счётчик не попадают.
    /// </summary>
    [Fact]
    public void Normalize_ProducesDenseOrdersPerContainer()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };
        var aggregate = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var child = Node(HKObjectLevel.Node, parent: aggregate.Id, sort: 7);
        // Строка под корнем, принадлежащая разделу: второй элемент его контейнера.
        var sectionLine = Item(section: SectionOils, sort: 1);
        var standalone = Item(sort: 1);
        var input = Input(
            sections,
            new[] { aggregate, child },
            new[] { sectionLine, standalone });

        var normalized = HKStructureOrdering.Normalize(input);

        // Корневой счётчик: раздел 0, самостоятельная строка 1.
        Assert.Equal(0, normalized.Sections.Single().SortOrder);
        Assert.Equal(1, normalized.Items.Single(i => i.Id == standalone.Id).SortOrder);

        // Счётчик раздела «Масла»: позиция и строка под корнем — 0 и 1.
        Assert.Equal(0, normalized.Nodes.Single(n => n.Id == aggregate.Id).SortOrder);
        Assert.Equal(1, normalized.Items.Single(i => i.Id == sectionLine.Id).SortOrder);

        // Счётчик позиции: вложенный узел — 0.
        Assert.Equal(0, normalized.Nodes.Single(n => n.Id == child.Id).SortOrder);
    }
}
