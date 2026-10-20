using Chernika.Domain.Enums;
using Chernika.Domain.Structure;
using Chernika.Infrastructure.Services;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Проекция принятой редакции: дерево, итоги и расшифровка.
/// <para>
/// Проверяется без БД: проекция строится из снимка принятой редакции, и все
/// её величины считаются доменными механизмами.
/// </para>
/// </summary>
public class HKAcceptedCompositionProjectionTests
{
    private static decimal Mass(IReadOnlyList<HKMeasuredTotal> totals) =>
        totals.First(t => t.Quantity == HKPhysicalQuantity.Mass).BaseValue;

    private static HKAcceptedStructureDto Snapshot(
        Guid? topSectionId = null,
        bool withMaterials = false)
    {
        var top = Guid.NewGuid();
        var node = Guid.NewGuid();
        var item = Guid.NewGuid();

        var materials = withMaterials
            ? new List<HKAcceptedMaterialDto>
            {
                new(Guid.NewGuid(), GsmCategory.Primary, "Марка основная", "ТУ 1"),
                new(Guid.NewGuid(), GsmCategory.Reserve, "Марка резервная", "ТУ 2")
            }
            : new List<HKAcceptedMaterialDto>();

        return new HKAcceptedStructureDto(
            Guid.NewGuid(), 3, DateTime.UtcNow,
            "Карточка ХК", "ХК-2026-1", "v1026", HKObjectLevel.Aggregate,
            "Назначение", null, "Примечание", null, null, false,
            new List<HKAcceptedNodeDto>
            {
                new(top, null, HKObjectLevel.Aggregate, "Верхняя позиция", 2, 0, topSectionId),
                new(node, top, HKObjectLevel.Node, "Узел", 3, 1, null)
            },
            new List<HKAcceptedItemDto>
            {
                new(item, node, Guid.NewGuid(), "АС-005", "Редуктор",
                    4, 500m, "г", "12 мес", "примечание", 0, null, materials)
            },
            topSectionId is null
                ? new List<HKAcceptedSectionDto>()
                : new List<HKAcceptedSectionDto>
                {
                    new(topSectionId.Value, HKCardSectionType.Oils, "Масла", 0)
                },
            DateTime.UtcNow, "user-1");
    }

    [Fact]
    public void Build_TreeAndTotals_ReferToTheSameRevision()
    {
        var snapshot = Snapshot();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), snapshot);

        Assert.Equal(snapshot.RevisionId, view.RevisionId);
        Assert.Equal(3, view.RevisionNumber);
        // Контрольный пример: 2 × 3 × 4 × 500 г = 12 кг.
        Assert.Equal(12000m, Mass(view.Overall));
    }

    [Fact]
    public void Build_Rows_StartFromCardStructure_AndKeepNumbering()
    {
        var snapshot = Snapshot();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), snapshot);

        var position = view.Rows.First(r => r.NodeId is not null);
        Assert.Equal("1", position.Number);
        Assert.Equal("Верхняя позиция", position.Name);
        Assert.Equal("Агрегат", position.LevelTitle);
        Assert.Equal(2, position.QuantityInParent);
        Assert.Equal(2, position.InstancesInCard);

        var nested = view.Rows.First(r => r.Kind == HKStructureRowKind.Position && r.Name == "Узел");
        Assert.Equal("1.1", nested.Number);

        var item = view.Rows.First(r => r.ItemId is not null);
        Assert.Equal("1.1.1", item.Number);
        Assert.Equal("АС-005 — Редуктор", item.Name);
        Assert.Equal(24, item.InstancesInCard);
    }

    [Fact]
    public void Build_WithoutSection_ShowsVisibleGroup_WithoutChangingNumbers()
    {
        var withSection = Snapshot(Guid.NewGuid());
        var withoutSection = Snapshot();

        var sectionView = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), withSection);
        var noSectionView = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), withoutSection);

        // Виртуальная группа не занимает номер: позиция без раздела остаётся
        // первой веткой карты и нумеруется «1».
        var positionNumber = noSectionView.Rows.First(r => r.NodeId is not null).Number;
        Assert.Equal("1", positionNumber);

        // В карте с разделом раздел занимает первый номер, и позиция под ним
        // получает «1.1» — это существующий механизм нумерации, а не следствие
        // виртуальной группы.
        Assert.Equal("1", sectionView.Rows.First(r => r.Kind == HKStructureRowKind.Section).Number);
        Assert.Equal("1.1", sectionView.Rows.First(r => r.NodeId is not null).Number);

        // Видимая группа — заголовок без своего номера и без фиктивной записи.
        var group = noSectionView.Rows.First(r => r.Kind == HKStructureRowKind.Section);
        Assert.Equal("Без раздела", group.Name);
        Assert.Equal(string.Empty, group.Number);
        Assert.Null(group.SectionId);
    }

    [Fact]
    public void Build_MaterialCategories_DoNotDoubleCountTheRow()
    {
        var withoutMaterials = HKAcceptedCompositionProjection.Build(
            Guid.NewGuid(), Snapshot());

        var withMaterials = HKAcceptedCompositionProjection.Build(
            Guid.NewGuid(), Snapshot(withMaterials: true));

        // Две марки разных категорий: строка учитывается один раз.
        Assert.Equal(Mass(withoutMaterials.Overall), Mass(withMaterials.Overall));
    }

    [Fact]
    public void Build_UsesSnapshotNames_NotWorkingReferences()
    {
        var snapshot = Snapshot(withMaterials: true);

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), snapshot);

        // Название и код СЕ берутся из снимка: переименование справочной записи
        // не меняет отображение принятой структуры.
        Assert.Equal("ХК-2026-1", view.Code);
        Assert.Equal("Карточка ХК", view.Name);
        Assert.Equal("АС-005 — Редуктор", view.Rows.First(r => r.ItemId is not null).Name);
        Assert.Equal("ТУ 1", view.Items.Single().Materials.First(m => m.Category == GsmCategory.Primary).Nd);
    }

    [Fact]
    public void Build_Breakdown_ShowsNormAndMultiplier()
    {
        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), Snapshot());

        var top = view.Rows.First(r => r.NodeId is not null);
        var contribution = Assert.Single(top.Contributions);

        Assert.Equal(500m, contribution.Volume);
        Assert.Equal("г", contribution.UnitOfMeasure);
        Assert.Equal(4, contribution.Quantity);
        Assert.Equal(12m, contribution.Multiplier);
        Assert.Equal(6000m, contribution.BaseValue);
    }
}
