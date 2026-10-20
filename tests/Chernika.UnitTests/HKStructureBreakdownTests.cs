using Chernika.Domain.Enums;
using Chernika.Domain.Structure;
using Xunit;

namespace Chernika.UnitTests;

/// <summary>
/// Прозрачный расчёт массы и объёма: контрольный пример и правила множителей.
/// <para>
/// Расчётные механизмы те же, что и в редакторе и в принятой редакции: одни
/// и те же формулы, разные входные данные.
/// </para>
/// </summary>
public class HKStructureBreakdownTests
{
    private static decimal Mass(IReadOnlyList<HKMeasuredTotal> totals) =>
        totals.First(t => t.Quantity == HKPhysicalQuantity.Mass).BaseValue;

    /// <summary>
    /// Контрольный пример: позиция ×2 → узел ×3 → СЕ ×4 → норма 500 г.
    /// </summary>
    private static HKStructureInput ControlExample()
    {
        var top = Guid.NewGuid();
        var node = Guid.NewGuid();

        return new HKStructureInput(
            Array.Empty<HKStructureSectionInput>(),
            new[]
            {
                new HKStructureNodeInput(
                    top, null, HKObjectLevel.Aggregate, "Верхняя позиция", 2, 0, null),
                new HKStructureNodeInput(
                    node, top, HKObjectLevel.Node, "Узел", 3, 0, null)
            },
            new[]
            {
                new HKStructureItemInput(
                    Guid.NewGuid(), node, 4, 500m, "г", 0, null)
            });
    }

    [Fact]
    public void ControlExample_OneAssemblyUnit_IsFiveHundredGrams()
    {
        var input = ControlExample();
        var itemId = input.Items.Single().Id;

        var totals = HKStructureTotals.CalculateItem(input, itemId);

        // Собственная норма строки: количество в неё не входит.
        Assert.Equal(500m, Mass(totals.PerOne));
    }

    [Fact]
    public void ControlExample_OneNode_IsFourTimesFiveHundredGrams()
    {
        var input = ControlExample();
        var nodeId = input.Nodes.Single(n => n.ParentId is not null).Id;

        var totals = HKStructureTotals.CalculateNode(input, nodeId);

        // Количество строки (4) входит в итог узла, количество самого узла — нет.
        Assert.Equal(2000m, Mass(totals.PerOne));
    }

    [Fact]
    public void ControlExample_OneTopPosition_IsThreeTimesNode()
    {
        var input = ControlExample();
        var topId = input.Nodes.Single(n => n.ParentId is null).Id;

        var totals = HKStructureTotals.CalculateNode(input, topId);

        Assert.Equal(6000m, Mass(totals.PerOne));
    }

    [Fact]
    public void ControlExample_AssemblyUnitsInCard_IsTwentyFour()
    {
        var input = ControlExample();
        var itemId = input.Items.Single().Id;

        Assert.Equal(24, HKStructureTotals.InstancesOfItem(input, itemId));
    }

    [Fact]
    public void ControlExample_BranchContribution_IsTwelveKilograms()
    {
        var input = ControlExample();
        var itemId = input.Items.Single().Id;

        var totals = HKStructureTotals.CalculateItem(input, itemId);

        // 24 экземпляра по 500 г = 12 кг в базовых граммах.
        Assert.Equal(12000m, Mass(totals.TotalInCard));
    }

    [Fact]
    public void ControlExample_CardOverall_IsTwelveKilograms()
    {
        var input = ControlExample();

        var totals = HKStructureTotals.Calculate(input);

        Assert.Equal(12000m, Mass(totals.Overall));
        Assert.True(totals.Overall.All(t => t.IsComplete));
    }

    [Fact]
    public void ControlExample_BreakdownShowsNormAndMultiplier()
    {
        var input = ControlExample();
        var topId = input.Nodes.Single(n => n.ParentId is null).Id;

        var contributions = HKStructureTotals.ContributionsOfNode(input, topId);

        var contribution = Assert.Single(contributions);
        Assert.Equal(500m, contribution.Volume);
        Assert.Equal("г", contribution.UnitOfMeasure);
        Assert.Equal(4, contribution.Quantity);
        // Множитель вклада: количество строки и количества позиций строго под
        // рассматриваемой позицией.
        Assert.Equal(12m, contribution.Multiplier);
        Assert.Equal(6000m, contribution.BaseValue);
    }

    [Fact]
    public void UnknownUnit_GivesIncompleteTotal_NotZero()
    {
        var nodeId = Guid.NewGuid();
        var input = new HKStructureInput(
            Array.Empty<HKStructureSectionInput>(),
            new[] { new HKStructureNodeInput(nodeId, null, HKObjectLevel.Aggregate, "Позиция", 1, 0, null) },
            new[] { new HKStructureItemInput(Guid.NewGuid(), nodeId, 1, 5m, "неведомая", 0, null) });

        var totals = HKStructureTotals.Calculate(input);

        Assert.False(totals.Overall.All(t => t.IsComplete));
        Assert.Contains(totals.Overall, t => t.Diagnostics.Count > 0);
    }

    [Fact]
    public void MassAndLiquid_AreSeparateQuantities()
    {
        var nodeId = Guid.NewGuid();
        var input = new HKStructureInput(
            Array.Empty<HKStructureSectionInput>(),
            new[] { new HKStructureNodeInput(nodeId, null, HKObjectLevel.Aggregate, "Позиция", 1, 0, null) },
            new[]
            {
                new HKStructureItemInput(Guid.NewGuid(), nodeId, 1, 500m, "г", 0, null),
                new HKStructureItemInput(Guid.NewGuid(), nodeId, 1, 2m, "л", 1, null)
            });

        var totals = HKStructureTotals.Calculate(input);

        var mass = totals.Overall.First(t => t.Quantity == HKPhysicalQuantity.Mass);
        var liquid = totals.Overall.First(t => t.Quantity == HKPhysicalQuantity.Liquid);

        Assert.Equal(500m, mass.BaseValue);
        Assert.Equal(2m, liquid.BaseValue);
    }
}
