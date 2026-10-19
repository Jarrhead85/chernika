using Chernika.Domain.Enums;
using Chernika.Domain.Structure;

namespace Chernika.UnitTests;

/// <summary>
/// Иерархическая нумерация и расчёт итогов собственной структуры.
/// <para>
/// Проверяется чистая доменная механика: без EF, HTTP и БД. Один и тот же
/// механизм принимает рабочее дерево и принятую редакцию, поэтому проверяется
/// он один.
/// </para>
/// </summary>
public class HKStructureTests
{
    private static readonly Guid SectionOils = Guid.NewGuid();
    private static readonly Guid SectionFuels = Guid.NewGuid();

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

    // ── Нумерация без разделов ──

    [Fact]
    public void Numbering_WithoutSections_IsSequential()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 0);
        var assembly = Node(HKObjectLevel.Node, parent: aggregate.Id, sort: 0);

        var rows = HKStructureNumbering.Build(Input(
            nodes: new[] { aggregate, assembly },
            items: new[] { Item(node: assembly.Id) }));

        Assert.Equal(new[] { "1", "1.1", "1.1.1" }, rows.Select(r => r.Number).ToArray());
    }

    /// <summary>
    /// Пропущенный уровень техники не создаёт пустой сегмент: после агрегата
    /// сразу идёт номер 2.1, а не 2.1.1.
    /// </summary>
    [Fact]
    public void Numbering_SkippedLevel_CreatesNoEmptySegment()
    {
        var first = Node(HKObjectLevel.Aggregate, sort: 0);
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 1);

        var rows = HKStructureNumbering.Build(Input(
            nodes: new[] { first, aggregate },
            items: new[] { Item(node: aggregate.Id) }));

        // 1 Агрегат; 2 Агрегат; 2.1 Сборочная единица — без пустого сегмента 2.1.1.
        Assert.Equal(new[] { "1", "2", "2.1" }, rows.Select(r => r.Number).ToArray());
    }

    [Fact]
    public void Numbering_QuantityDoesNotCreateExtraNumbers()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 0, quantity: 5);

        var rows = HKStructureNumbering.Build(Input(
            nodes: new[] { aggregate },
            items: new[] { Item(node: aggregate.Id) }));

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Quantity);
        Assert.Equal(new[] { "1", "1.1" }, rows.Select(r => r.Number).ToArray());
    }

    [Fact]
    public void Numbering_ReorderAndRemoval_Recalculates()
    {
        var first = Node(HKObjectLevel.Aggregate, sort: 0);
        var second = Node(HKObjectLevel.Aggregate, sort: 1);

        var before = HKStructureNumbering.Build(Input(nodes: new[] { first, second }));
        Assert.Equal(new[] { "1", "2" }, before.Select(r => r.Number).ToArray());

        // Перестановка.
        var swapped = HKStructureNumbering.Build(Input(
            nodes: new[]
            {
                Node(HKObjectLevel.Aggregate, parent: null, sort: 1),
                Node(HKObjectLevel.Aggregate, parent: null, sort: 0)
            }));
        Assert.Equal(new[] { "1", "2" }, swapped.Select(r => r.Number).ToArray());

        // Удаление.
        var removed = HKStructureNumbering.Build(Input(nodes: new[] { second }));
        Assert.Equal(new[] { "1" }, removed.Select(r => r.Number).ToArray());
    }

    /// <summary>
    /// Позиции и строки одного родителя имеют ОДИН совместный порядок, а при
    /// одинаковом SortOrder результат детерминирован.
    /// </summary>
    [Fact]
    public void Numbering_EqualSortOrder_IsDeterministic()
    {
        var parent = Node(HKObjectLevel.Aggregate, sort: 0);
        var first = HKStructureNumbering.Build(Input(
            nodes: new[] { parent },
            items: new[] { Item(node: parent.Id) }));
        var second = HKStructureNumbering.Build(Input(
            nodes: new[] { parent },
            items: new[] { Item(node: parent.Id) }));

        Assert.Equal(first.Select(r => r.Number), second.Select(r => r.Number));
        Assert.Equal(new[] { "1", "1.1" }, first.Select(r => r.Number).ToArray());
    }

    // ── Нумерация с разделами ──

    [Fact]
    public void Numbering_WithSections_HeadersSpanFullWidth()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };

        var aggregate = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var assembly = Node(HKObjectLevel.Node, parent: aggregate.Id, sort: 0);

        var rows = HKStructureNumbering.Build(Input(
            sections: sections,
            nodes: new[] { aggregate, assembly },
            items: new[] { Item(node: assembly.Id) }));

        Assert.Equal(HKStructureRowKind.Section, rows[0].Kind);
        Assert.Equal("1", rows[0].Number);
        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(new[] { "1", "1.1", "1.1.1", "1.1.1.1" }, rows.Select(r => r.Number).ToArray());
        Assert.Equal(HKCardSectionType.Oils, rows[1].SectionType);
    }

    [Fact]
    public void Numbering_MixedBranches_SharesOneTopOrder()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };

        var aggregate = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var noSection = Node(HKObjectLevel.Aggregate, sort: 1);

        var rows = HKStructureNumbering.Build(Input(
            sections: sections,
            nodes: new[] { aggregate, noSection },
            items: new[] { Item(node: noSection.Id) }));

        Assert.Equal(HKStructureRowKind.Section, rows[0].Kind);
        Assert.Equal("Масла", rows[0].Name);

        var topLevel = rows.Where(r => r.Depth == 0).Select(r => r.Number).ToArray();
        Assert.Equal(new[] { "1", "2" }, topLevel);
    }

    /// <summary>Вложенная позиция наследует раздел своего корня.</summary>
    [Fact]
    public void Numbering_BranchInheritsSingleSection()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };

        var aggregate = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var child = Node(HKObjectLevel.Node, parent: aggregate.Id, sort: 0);
        var item = Item(node: child.Id);

        var rows = HKStructureNumbering.Build(Input(
            sections: sections, nodes: new[] { aggregate, child }, items: new[] { item }));

        // Все строки ветки принадлежат одному разделу.
        Assert.All(rows, r => Assert.Equal(SectionOils, r.SectionId));
    }

    // ── Расчёты ──

    /// <summary>
    /// Пример из требований: агрегат × 2, узел × 3, сборочная единица × 4,
    /// масса одной единицы 500 г. Ветка даёт 12 кг.
    /// </summary>
    [Fact]
    public void Totals_ExampleTwoTimesThreeTimesFour()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, quantity: 2);
        var assembly = Node(HKObjectLevel.Node, parent: aggregate.Id, quantity: 3);
        var item = Item(node: assembly.Id, quantity: 4, volume: 500m, unit: "г");

        var totals = HKStructureTotals.Calculate(
            Input(nodes: new[] { aggregate, assembly }, items: new[] { item }));

        var overall = Assert.Single(totals.Overall);
        Assert.Equal(12000m, overall.BaseValue);

        Assert.Equal("12 кг (12000 г)", HKTotalFormatter.Format(overall));
    }

    [Fact]
    public void Totals_DistinguishesPerOneAndTotal()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, quantity: 2);
        var assembly = Node(HKObjectLevel.Node, parent: aggregate.Id, quantity: 3);
        var item = Item(node: assembly.Id, quantity: 4, volume: 500m, unit: "г");

        var input = Input(nodes: new[] { aggregate, assembly }, items: new[] { item });
        var totals = HKStructureTotals.Calculate(input);

        // Верхняя позиция — агрегат: её вклад и есть вся ветка.
        var top = Assert.Single(totals.WithoutSection!.Totals);
        Assert.Equal(12000m, top.BaseValue);
    }

    /// <summary>Количество текущей позиции не умножается дважды.</summary>
    [Fact]
    public void Totals_PerOneExcludesOwnQuantity()
    {
        var node = Node(HKObjectLevel.Aggregate, quantity: 3);
        var item = Item(node: node.Id, quantity: 2, volume: 100m, unit: "г");

        var totals = HKStructureTotals.Calculate(Input(nodes: new[] { node }, items: new[] { item }));

        // Одна сборочная единица 100 г; один агрегат 100 × 2 = 200 г;
        // вся ветка 200 × 3 = 600 г.
        var overall = Assert.Single(totals.Overall);
        Assert.Equal(600m, overall.BaseValue);
    }

    [Fact]
    public void Totals_MixedKilogramsAndGrams()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };

        var kg = Item(section: SectionOils, volume: 1.5m, unit: "кг", sort: 0);
        var grams = Item(section: SectionOils, volume: 250m, unit: "г", sort: 1);

        var totals = HKStructureTotals.Calculate(
            Input(sections: sections, items: new[] { kg, grams }));

        var mass = Assert.Single(totals.Overall);
        Assert.Equal(1750m, mass.BaseValue);
        Assert.Equal("1,75 кг (1750 г)", HKTotalFormatter.Format(mass));
    }

    /// <summary>Литры не складываются с килограммами.</summary>
    [Fact]
    public void Totals_LitersAreKeptSeparate()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0),
            new HKStructureSectionInput(SectionFuels, HKCardSectionType.Fuels, 1)
        };

        var oil = Item(section: SectionOils, volume: 2m, unit: "кг");
        var fuel = Item(section: SectionFuels, volume: 120m, unit: "л");

        var totals = HKStructureTotals.Calculate(
            Input(sections: sections, items: new[] { oil, fuel }));

        Assert.Equal(2, totals.Overall.Count);

        var mass = totals.Overall.Single(t => t.Quantity == HKPhysicalQuantity.Mass);
        var liquid = totals.Overall.Single(t => t.Quantity == HKPhysicalQuantity.Liquid);

        Assert.Equal(2000m, mass.BaseValue);
        Assert.Equal(120m, liquid.BaseValue);
        Assert.Equal("120 л", HKTotalFormatter.Format(liquid));
    }

    [Fact]
    public void Totals_SectionTotalsAreSeparateFromOverall()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0),
            new HKStructureSectionInput(SectionFuels, HKCardSectionType.Fuels, 1)
        };

        var totals = HKStructureTotals.Calculate(Input(
            sections: sections,
            items: new[]
            {
                Item(section: SectionOils, volume: 1m, unit: "кг"),
                Item(section: SectionFuels, volume: 10m, unit: "л")
            }));

        var oils = Assert.Single(totals.Sections.Where(s => s.SectionId == SectionOils));
        var fuels = Assert.Single(totals.Sections.Where(s => s.SectionId == SectionFuels));

        Assert.Equal(1000m, Assert.Single(oils.Totals).BaseValue);
        Assert.Equal(10m, Assert.Single(fuels.Totals).BaseValue);
    }

    /// <summary>
    /// Неподдерживаемая единица не превращается в ноль: итог помечается
    /// неполным и не может быть прочитан как достоверная сумма.
    /// </summary>
    [Fact]
    public void Totals_UnsupportedUnit_IsNotZero()
    {
        var totals = HKStructureTotals.Calculate(
            Input(items: new[] { Item(volume: 5m, unit: "шт") }));

        var total = Assert.Single(totals.Overall);

        Assert.False(total.IsComplete);
        Assert.NotEmpty(total.Diagnostics);
        Assert.Contains("итог неполный", HKTotalFormatter.Format(total));
    }

    [Fact]
    public void Totals_EmptyStructure_HasEmptyTotals()
    {
        var totals = HKStructureTotals.Calculate(Input());

        Assert.Empty(totals.Overall);
        Assert.Empty(totals.Sections);
    }

    /// <summary>
    /// Обязательный пример из требований: несколько массовых разделов не
    /// должны приводить ни к исключению, ни к потере слагаемого.
    /// </summary>
    [Fact]
    public void Totals_SectionsWithSeveralMassEntries_AggregateWithoutException()
    {
        var sectionGreases = Guid.NewGuid();
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0),
            new HKStructureSectionInput(sectionGreases, HKCardSectionType.Greases, 1),
            new HKStructureSectionInput(SectionFuels, HKCardSectionType.Fuels, 2)
        };

        var totals = HKStructureTotals.Calculate(Input(
            sections: sections,
            items: new[]
            {
                Item(section: SectionOils, volume: 1m, unit: "кг"),
                Item(section: sectionGreases, volume: 250m, unit: "г"),
                Item(section: SectionFuels, volume: 10m, unit: "л")
            }));

        var mass = Assert.Single(totals.Overall.Where(t => t.Quantity == HKPhysicalQuantity.Mass));
        var liquid = Assert.Single(totals.Overall.Where(t => t.Quantity == HKPhysicalQuantity.Liquid));

        Assert.True(mass.IsComplete);
        Assert.True(liquid.IsComplete);
        Assert.Equal(1250m, mass.BaseValue);
        Assert.Equal(10m, liquid.BaseValue);
    }

    /// <summary>Контракт позиционных итогов: одна запись на верхнюю ветку.</summary>
    [Fact]
    public void Totals_CalculatePositionTotals_ReturnsOneEntryPerTopBranch()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, quantity: 2);
        var assembly = Node(HKObjectLevel.Node, parent: aggregate.Id, quantity: 3);
        var item = Item(node: assembly.Id, quantity: 4, volume: 500m, unit: "г");
        var rootItem = Item(section: null, volume: 1m, unit: "кг");

        var input = Input(nodes: new[] { aggregate, assembly }, items: new[] { item, rootItem });
        var positionTotals = HKStructureTotals.CalculatePositionTotals(input);

        // Две верхние ветки: позиция-агрегат и строка под корнем.
        Assert.Equal(2, positionTotals.Count);

        var branch = positionTotals.Single(t => t.NodeId == aggregate.Id);
        Assert.Null(branch.ItemId);
        // Вся ветка: 500 г × 4 × 3 × 2 = 12000 г.
        Assert.Equal(12000m, Assert.Single(branch.TotalInCard).BaseValue);
        // Один узел: 500 г × 4 × 3 = 6000 г.
        Assert.Equal(6000m, Assert.Single(branch.PerOne).BaseValue);

        var standalone = positionTotals.Single(t => t.ItemId == rootItem.Id);
        Assert.Null(standalone.NodeId);
        Assert.Equal(1000m, Assert.Single(standalone.TotalInCard).BaseValue);
        Assert.Equal(1000m, Assert.Single(standalone.PerOne).BaseValue);

        // Расчёт всей карты строится поверх позиционных итогов.
        var aggregateTotals = HKStructureTotals.Calculate(input);
        Assert.Equal(13000m, aggregateTotals.Overall.Single(t => t.Quantity == HKPhysicalQuantity.Mass).BaseValue);
    }

    /// <summary>
    /// Потомки раздела — позиции и строки под корнем — образуют ОДНУ совместную
    /// последовательность: строка может стоять между позициями.
    /// </summary>
    [Fact]
    public void Numbering_SectionChildren_PositionsAndItemsShareOneOrder()
    {
        var sections = new[]
        {
            new HKStructureSectionInput(SectionOils, HKCardSectionType.Oils, 0)
        };

        var first = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 0);
        var second = Node(HKObjectLevel.Aggregate, section: SectionOils, sort: 2);

        var rows = HKStructureNumbering.Build(Input(
            sections: sections,
            nodes: new[] { first, second },
            items: new[]
            {
                // Строка с SortOrder 1 стоит между двумя позициями раздела.
                Item(section: SectionOils, volume: 1m, unit: "кг", sort: 1)
            }));

        Assert.Equal(HKStructureRowKind.Section, rows[0].Kind);
        Assert.Equal(HKStructureRowKind.Position, rows[1].Kind);
        Assert.Equal(HKStructureRowKind.TerminalRow, rows[2].Kind);
        Assert.Equal(HKStructureRowKind.Position, rows[3].Kind);
        Assert.Equal(new[] { "1", "1.1", "1.2", "1.3" }, rows.Select(r => r.Number).ToArray());
    }

    // ── Единицы ──

    [Theory]
    [InlineData("кг", HKPhysicalQuantity.Mass, 1000)]
    [InlineData("КГ", HKPhysicalQuantity.Mass, 1000)]
    [InlineData("г", HKPhysicalQuantity.Mass, 1)]
    [InlineData("л", HKPhysicalQuantity.Liquid, 1)]
    public void Units_ResolveSupportedValues(
        string unit, HKPhysicalQuantity quantity, decimal factor)
    {
        Assert.True(HKUnitCatalog.TryResolve(unit, out var info));
        Assert.Equal(quantity, info.Quantity);
        Assert.Equal(factor, info.ToBaseFactor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("шт")]
    [InlineData("мм")]
    public void Units_UnsupportedValuesAreNotGuessed(string? unit)
    {
        Assert.False(HKUnitCatalog.TryResolve(unit, out _));
        Assert.Null(HKUnitCatalog.ResolveOrNull(unit));
    }
}