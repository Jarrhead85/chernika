using Chernika.Domain.Enums;
using Chernika.Domain.Structure;
using Chernika.Infrastructure.Services;
using Chernika.Web.Components;
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

    /// <summary>Снимок с произвольным составом: разделы, позиции и строки.</summary>
    private static HKAcceptedStructureDto SnapshotWith(
        IReadOnlyList<HKAcceptedNodeDto> nodes,
        IReadOnlyList<HKAcceptedItemDto> items,
        IReadOnlyList<HKAcceptedSectionDto> sections) =>
        new(
            Guid.NewGuid(), 1, DateTime.UtcNow,
            "Карточка ХК", "ХК-2026-1", "v1026", HKObjectLevel.Aggregate,
            null, null, null, null, null, false,
            nodes, items, sections,
            DateTime.UtcNow, "user-1");

    private static HKAcceptedNodeDto Node(
        Guid id, Guid? parentId, string name, int sortOrder = 0,
        Guid? sectionId = null, HKObjectLevel level = HKObjectLevel.Aggregate) =>
        new(id, parentId, level, name, 1, sortOrder, sectionId);

    private static HKAcceptedItemDto Item(
        Guid id, Guid? nodeId, int sortOrder = 0, Guid? sectionId = null) =>
        new(id, nodeId, Guid.NewGuid(), "АС-005", "Деталь",
            1, 100m, "г", null, null, sortOrder, sectionId,
            new List<HKAcceptedMaterialDto>());

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

    // ── Группировка «Без раздела» ──

    /// <summary>
    /// Виртуальная группа открывается перед веткой без раздела и не
    /// появляется внутри неё: потомки ветки остаются в группе.
    /// </summary>
    [Fact]
    public void Build_WithoutSectionBranch_OpensGroupBeforeBranch_AndKeepsDescendantsInside()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var item = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(root, null, "Верхняя позиция", 0),
                Node(child, root, "Узел", 0, level: HKObjectLevel.Node)
            },
            new List<HKAcceptedItemDto> { Item(item, child) },
            new List<HKAcceptedSectionDto>()));

        var group = Assert.Single(view.Rows.Where(r => r.Kind == HKStructureRowKind.Section));
        Assert.Equal("Без раздела", group.Name);

        // Заголовок стоит перед веткой, а не внутри её.
        var groupIndex = view.Rows.ToList().IndexOf(group);
        var rootIndex = view.Rows.ToList().FindIndex(r => r.NodeId == root);
        Assert.True(groupIndex < rootIndex, "Заголовок группы должен идти перед веткой.");

        // Внутри ветки своих заголовков нет: их ровно один на весь блок.
        Assert.Equal(1, view.Rows.Count(r => r.Kind == HKStructureRowKind.Section));

        // Нумерация не изменилась: виртуальная группа номера не занимает.
        Assert.Equal("1", view.Rows.Single(r => r.NodeId == root).Number);
        Assert.Equal("1.1", view.Rows.Single(r => r.NodeId == child).Number);
        Assert.Equal("1.1.1", view.Rows.Single(r => r.ItemId == item).Number);
    }

    /// <summary>Соседние ветки без раздела образуют один блок с одной группой.</summary>
    [Fact]
    public void Build_AdjacentBranchesWithoutSection_ShareOneGroup()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(first, null, "Первая ветка", 0),
                Node(second, null, "Вторая ветка", 1)
            },
            new List<HKAcceptedItemDto>(),
            new List<HKAcceptedSectionDto>()));

        var group = Assert.Single(view.Rows.Where(r => r.Kind == HKStructureRowKind.Section));
        Assert.Equal("Без раздела", group.Name);

        // Обе ветки идут после заголовка группы.
        var groupIndex = view.Rows.ToList().IndexOf(group);
        Assert.True(groupIndex < view.Rows.ToList().FindIndex(r => r.NodeId == first));
        Assert.True(view.Rows.ToList().FindIndex(r => r.NodeId == first) < view.Rows.ToList().FindIndex(r => r.NodeId == second));
    }

    /// <summary>
    /// Блоки без раздела, разделённые другим разделом, показываются каждый
    /// со своей группой.
    /// </summary>
    [Fact]
    public void Build_WithoutSectionBlocksSeparatedBySection_RepeatGroup()
    {
        var sectionId = Guid.NewGuid();
        var before = Guid.NewGuid();
        var inSection = Guid.NewGuid();
        var after = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(before, null, "Ветка до раздела", 0),
                Node(inSection, null, "Позиция раздела", 1, sectionId),
                Node(after, null, "Ветка после раздела", 2)
            },
            new List<HKAcceptedItemDto>(),
            new List<HKAcceptedSectionDto>
            {
                new(sectionId, HKCardSectionType.Oils, "Масла", 1)
            }));

        var groups = view.Rows.Where(r => r.Kind == HKStructureRowKind.Section).ToList();

        // Два блока без раздела — две группы, и у каждой свой ключ.
        var withoutSectionGroups = groups.Where(g => g.Name == "Без раздела").ToList();
        Assert.Equal(2, withoutSectionGroups.Count);
        Assert.NotEqual(withoutSectionGroups[0].GroupKey, withoutSectionGroups[1].GroupKey);
        Assert.All(withoutSectionGroups, g => Assert.False(string.IsNullOrWhiteSpace(g.GroupKey)));

        // Вторая группа стоит после раздела.
        var sectionIndex = view.Rows.ToList().FindIndex(r => r.Kind == HKStructureRowKind.Section && r.SectionId == sectionId);
        var secondGroupIndex = view.Rows.ToList().IndexOf(withoutSectionGroups[1]);
        Assert.True(sectionIndex < secondGroupIndex);
    }

    /// <summary>
    /// Ключи строк дерева уникальны и не зависят от фильтра: повторяющиеся
    /// @key в дереве Blazor недопустимы.
    /// </summary>
    [Fact]
    public void Build_RowKeys_AreUnique_AndIndependentOfFiltering()
    {
        var sectionId = Guid.NewGuid();
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var item = Guid.NewGuid();
        var lonely = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(root, null, "Верхняя позиция", 0, sectionId),
                Node(child, root, "Узел", 0, level: HKObjectLevel.Node),
                Node(lonely, null, "Ветка без раздела", 1)
            },
            new List<HKAcceptedItemDto> { Item(item, child) },
            new List<HKAcceptedSectionDto>
            {
                new(sectionId, HKCardSectionType.Oils, "Масла", 0)
            }));

        var keys = view.Rows.Select(HKCompositionRowKey.Of).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));

        // Поиск по названию не меняет идентичность строк.
        var filtered = view.Rows.Where(r => r.Name?.Contains("Узел") == true).ToList();
        Assert.NotEmpty(filtered);
        Assert.All(filtered, r => Assert.Contains(HKCompositionRowKey.Of(r), keys));
    }

    // ── Эффективный раздел и счётчики ──

    /// <summary>
    /// Вложенная позиция наследует раздел корня, даже если родитель стоит в
    /// наборе позже потомка.
    /// </summary>
    [Fact]
    public void Build_ParentAfterChildInInput_StillInheritsSection()
    {
        var sectionId = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();

        // Родитель идёт позже потомка и позже по SortOrder: прежний обход
        // считал потомка безраздельным.
        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(child, parent, "Узел", 0, level: HKObjectLevel.Node),
                Node(parent, null, "Верхняя позиция", 1, sectionId)
            },
            new List<HKAcceptedItemDto>(),
            new List<HKAcceptedSectionDto>
            {
                new(sectionId, HKCardSectionType.Oils, "Масла", 0)
            }));

        var section = Assert.Single(view.Sections.Where(s => s.SectionId == sectionId));
        Assert.Equal(2, section.PositionCount);
        Assert.Equal(0, section.ItemCount);
        Assert.DoesNotContain(view.Sections, s => s.Name == "Без раздела");
    }

    /// <summary>
    /// Счётчики раздела включают и корневые записи раздела, и записи под
    /// позициями любой вложенности.
    /// </summary>
    [Fact]
    public void Build_SectionCounters_CountRootAndNestedRecords()
    {
        var sectionId = Guid.NewGuid();
        var rootPosition = Guid.NewGuid();
        var nestedPosition = Guid.NewGuid();
        var itemUnderPosition = Guid.NewGuid();
        var rootItem = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(rootPosition, null, "Верхняя позиция", 0, sectionId),
                Node(nestedPosition, rootPosition, "Узел", 0, level: HKObjectLevel.Node)
            },
            new List<HKAcceptedItemDto>
            {
                Item(itemUnderPosition, nestedPosition),
                Item(rootItem, null, 1, sectionId)
            },
            new List<HKAcceptedSectionDto>
            {
                new(sectionId, HKCardSectionType.Oils, "Масла", 0)
            }));

        var section = Assert.Single(view.Sections.Where(s => s.SectionId == sectionId));

        // Две позиции: верхняя и вложенная. Две строки: под позицией и
        // корневая строка СЕ самого раздела.
        Assert.Equal(2, section.PositionCount);
        Assert.Equal(2, section.ItemCount);

        // Сумма счётчиков равна общему количеству записей редакции.
        Assert.Equal(view.PositionCount, view.Sections.Sum(s => s.PositionCount));
        Assert.Equal(view.ItemCount, view.Sections.Sum(s => s.ItemCount));
    }

    /// <summary>
    /// В группу «Без раздела» входят и вложенные записи её веток.
    /// </summary>
    [Fact]
    public void Build_WithoutSectionCounters_IncludeNestedRecords()
    {
        var root = Guid.NewGuid();
        var nested = Guid.NewGuid();
        var item = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(root, null, "Верхняя позиция", 0),
                Node(nested, root, "Узел", 0, level: HKObjectLevel.Node)
            },
            new List<HKAcceptedItemDto> { Item(item, nested) },
            new List<HKAcceptedSectionDto>()));

        var withoutSection = Assert.Single(view.Sections.Where(s => s.Name == "Без раздела"));

        Assert.Equal(2, withoutSection.PositionCount);
        Assert.Equal(1, withoutSection.ItemCount);

        Assert.Equal(view.PositionCount, view.Sections.Sum(s => s.PositionCount));
        Assert.Equal(view.ItemCount, view.Sections.Sum(s => s.ItemCount));
    }

    /// <summary>
    /// Повреждённая структура не выдаётся за корректную: отсутствующий
    /// родитель попадает в диагностику, а итог считается неполным.
    /// </summary>
    [Fact]
    public void Build_DamagedStructure_ReportsDiagnostic_AndIsNotComplete()
    {
        var orphan = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(orphan, Guid.NewGuid(), "Сирота", 0)
            },
            new List<HKAcceptedItemDto>(),
            new List<HKAcceptedSectionDto>()));

        Assert.False(view.IsComplete);
        Assert.Contains(view.Diagnostics, d => d.Contains("родитель"));
    }

    /// <summary>
    /// Исправление группировки и счётчиков не меняет нормативные итоги:
    /// масса карты остаётся той же.
    /// </summary>
    [Fact]
    public void Build_CounterFix_DoesNotChangeMassTotals()
    {
        var sectionId = Guid.NewGuid();
        var root = Guid.NewGuid();
        var nested = Guid.NewGuid();
        var item = Guid.NewGuid();

        var view = HKAcceptedCompositionProjection.Build(Guid.NewGuid(), SnapshotWith(
            new List<HKAcceptedNodeDto>
            {
                Node(root, null, "Верхняя позиция", 0, sectionId),
                Node(nested, root, "Узел", 0, level: HKObjectLevel.Node)
            },
            new List<HKAcceptedItemDto> { Item(item, nested) },
            new List<HKAcceptedSectionDto>
            {
                new(sectionId, HKCardSectionType.Oils, "Масла", 0)
            }));

        // Одна строка по 100 г: итог карты — 100 г в базовой единице.
        Assert.Equal(100m, Mass(view.Overall));

        var section = Assert.Single(view.Sections.Where(s => s.SectionId == sectionId));
        Assert.Equal(100m, Mass(section.Totals));
    }

    // ── Уровень карты по принятой структуре ──

    /// <summary>
    /// Уровень карты выводится по старшей верхней позиции: верхний агрегат даёт
    /// ХК изделия, верхний узел требует явного выбора уровня корня.
    /// </summary>
    [Fact]
    public void CardLevel_TopAggregate_GivesEquipmentModelLevel()
    {
        var snapshot = Snapshot();

        var level = HKAcceptedCompositionProjection.CardLevel(snapshot.Nodes, snapshot.Items);

        // Верхняя позиция карты — агрегат, значит карта уровня изделия.
        Assert.Equal(HKObjectLevel.EquipmentModel, level);
    }

    [Fact]
    public void CardLevel_TopNode_WithoutChoice_IsNotDetermined()
    {
        var node = Guid.NewGuid();
        var snapshot = new HKAcceptedStructureDto(
            Guid.NewGuid(), 1, DateTime.UtcNow, "Карточка", "ХК-1", "v1", HKObjectLevel.Undefined,
            null, null, null, null, null, false,
            new List<HKAcceptedNodeDto>
            {
                new(node, null, HKObjectLevel.Node, "Узел", 1, 0, null)
            },
            new List<HKAcceptedItemDto>
            {
                new(Guid.NewGuid(), node, Guid.NewGuid(), "АС-1", "Деталь",
                    1, 1m, "кг", null, null, 0, null, new List<HKAcceptedMaterialDto>())
            },
            new List<HKAcceptedSectionDto>(),
            DateTime.UtcNow, "user-1");

        // Без явного выбора уровень корня не определяется.
        Assert.Null(HKAcceptedCompositionProjection.CardLevel(snapshot.Nodes, snapshot.Items));

        // С явным выбором уровень карты известен.
        Assert.Equal(
            HKObjectLevel.Aggregate,
            HKAcceptedCompositionProjection.CardLevel(
                snapshot.Nodes, snapshot.Items, HKObjectLevel.Aggregate));
    }

    [Fact]
    public void CardLevel_EmptyStructure_IsNotDetermined()
    {
        var snapshot = new HKAcceptedStructureDto(
            Guid.NewGuid(), 1, DateTime.UtcNow, "Карточка", "ХК-1", "v1", HKObjectLevel.Undefined,
            null, null, null, null, null, false,
            new List<HKAcceptedNodeDto>(),
            new List<HKAcceptedItemDto>(),
            new List<HKAcceptedSectionDto>(),
            DateTime.UtcNow, "user-1");

        Assert.Null(HKAcceptedCompositionProjection.CardLevel(snapshot.Nodes, snapshot.Items));
    }
}
