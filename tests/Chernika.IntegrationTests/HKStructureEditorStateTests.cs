using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Domain.Structure;
using Chernika.Web.Pages.Structure;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Локальная модель редактора собственной структуры.
/// <para>
/// Проверяется поведение состояния без EF, HTTP и БД: правки не трогают
/// загруженный граф, сворачивание не меняет порядок и номера, переносы
/// сохраняют нормы и материалы, удаление затрагивает только выбранное.
/// </para>
/// </summary>
public class HKStructureEditorStateTests
{
    private static HKStructureGraph Graph(
        IEnumerable<HKStructureSectionInput>? sections = null,
        IEnumerable<HKStructureNodeInput>? nodes = null,
        IEnumerable<HKStructureGraphItem>? items = null) =>
        new(
            Guid.NewGuid(), 7u, HKCardStatus.Draft, true, null, Guid.NewGuid(),
            sections?.ToList() ?? new List<HKStructureSectionInput>(),
            nodes?.ToList() ?? new List<HKStructureNodeInput>(),
            items?.ToList() ?? new List<HKStructureGraphItem>());

    private static HKStructureNodeInput Node(
        HKObjectLevel level, Guid? parent = null, int sort = 0, Guid? section = null,
        int quantity = 1, string name = "Позиция") =>
        new(Guid.NewGuid(), parent, level, name, quantity, sort, section);

    private static HKStructureGraphItem Item(
        Guid? node = null, int quantity = 1, decimal volume = 1m, string? unit = "кг",
        int sort = 0, Guid? section = null,
        params (Guid Gsm, GsmCategory Category)[] materials) =>
        new(Guid.NewGuid(), node, Guid.NewGuid(), "АУ-1", null, quantity, volume, unit,
            "12 мес", "примечание", sort, section,
            materials.Select((m, i) => new HKStructureGraphMaterial(
                Guid.NewGuid(), m.Gsm, "Марка", "ТУ", m.Category.ToString(), false)).ToList());

    // ── Независимость от загруженного графа ──

    /// <summary>
    /// Правка редактора не меняет проекцию, из которой состояние построено:
    /// загруженные сущности не редактируются напрямую.
    /// </summary>
    [Fact]
    public void Editing_DoesNotChangeSourceGraph()
    {
        var node = Node(HKObjectLevel.Aggregate, name: "Агрегат");
        var item = Item(node: node.Id, materials: (Guid.NewGuid(), GsmCategory.Reserve));
        var graph = Graph(nodes: new[] { node }, items: new[] { item });

        var state = HKStructureEditorState.FromGraph(graph);
        state.Nodes.Single().Name = "Изменённое имя";
        state.Items.Single().Quantity = 9;
        state.Items.Single().Materials.Clear();
        state.Sections.Add(new EditorSection { Id = Guid.NewGuid(), SectionType = HKCardSectionType.Oils });

        Assert.Equal("Агрегат", graph.Nodes.Single().Name);
        Assert.Equal(1, graph.Items.Single().Quantity);
        Assert.Single(graph.Items.Single().Materials);
        Assert.Empty(graph.Sections);
    }

    /// <summary>Загрузка состояния сама по себе не считается изменением.</summary>
    [Fact]
    public void FromGraph_LeavesStateClean()
    {
        var node = Node(HKObjectLevel.Aggregate);
        var state = HKStructureEditorState.FromGraph(
            Graph(nodes: new[] { node }, items: new[] { Item(node: node.Id) }));

        Assert.False(state.IsDirty);
        Assert.Equal(7u, state.LoadedRowVersion);
    }

    /// <summary>Любая правка содержания помечает состояние как изменённое.</summary>
    [Fact]
    public void FieldEdit_MarksDirty()
    {
        var state = HKStructureEditorState.FromGraph(
            Graph(nodes: new[] { Node(HKObjectLevel.Aggregate) }));

        Assert.False(state.IsDirty);

        state.Nodes.Single().Quantity = 4;
        Assert.True(state.IsDirty);

        state.MarkClean();
        Assert.False(state.IsDirty);

        state.AddTopPosition(HKObjectLevel.Node);
        Assert.True(state.IsDirty);
    }

    /// <summary>
    /// Сворачивание — просмотр, а не правка: ни признак изменений, ни порядок,
    /// ни номера, ни итоги от него не зависят.
    /// </summary>
    [Fact]
    public void Collapse_DoesNotChangeOrderNumbersOrTotals()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, quantity: 2, sort: 0);
        var node = Node(HKObjectLevel.Node, parent: aggregate.Id, sort: 0);
        var item = Item(node: node.Id, quantity: 4, volume: 500m, unit: "г");

        var state = HKStructureEditorState.FromGraph(
            Graph(nodes: new[] { aggregate, node }, items: new[] { item }));

        var numbersBefore = state.Rows().Select(r => r.Number).ToArray();
        var totalsBefore = state.Totals().Overall.Single().BaseValue;

        state.ToggleCollapse(aggregate.Id);
        Assert.True(state.IsCollapsed(aggregate.Id));
        Assert.False(state.IsDirty);

        Assert.Equal(numbersBefore, state.Rows().Select(r => r.Number).ToArray());
        Assert.Equal(totalsBefore, state.Totals().Overall.Single().BaseValue);

        state.ToggleCollapse(aggregate.Id);
        Assert.False(state.IsCollapsed(aggregate.Id));
    }

    // ── Порядок и соседи ──

    /// <summary>
    /// Позиции и строки одного контейнера — соседи по одному списку: перенос
    /// строки вверх меняет местами её с предыдущей позицией.
    /// </summary>
    [Fact]
    public void Move_SectionItem_UpSwapsWithPosition()
    {
        var section = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Oils, 0);
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 0, section: section.Id);
        var line = Item(section: section.Id, sort: 1);

        var state = HKStructureEditorState.FromGraph(Graph(
            sections: new[] { section }, nodes: new[] { aggregate }, items: new[] { line }));

        Assert.True(state.CanMoveItem(line.Id, -1));
        Assert.True(state.MoveItem(line.Id, -1));

        var normalized = state.BuildInput();
        Assert.Equal(0, normalized.Items.Single(i => i.Id == line.Id).SortOrder);
        Assert.Equal(1, normalized.Nodes.Single(n => n.Id == aggregate.Id).SortOrder);
    }

    /// <summary>У первого соседа нет переноса вверх: действие недоступно.</summary>
    [Fact]
    public void Move_FirstSibling_HasNoUp()
    {
        var first = Node(HKObjectLevel.Aggregate, sort: 0);
        var second = Node(HKObjectLevel.Aggregate, sort: 1);

        var state = HKStructureEditorState.FromGraph(Graph(nodes: new[] { first, second }));

        Assert.False(state.CanMovePosition(first.Id, -1));
        Assert.False(state.MovePosition(first.Id, -1));
        Assert.True(state.CanMovePosition(second.Id, -1));
    }

    /// <summary>
    /// Нормализация ведёт отдельные шкалы: дети раздела не попадают в
    /// корневой счётчик.
    /// </summary>
    [Fact]
    public void Normalize_KeepsSectionChildrenOutOfRootCounter()
    {
        var section = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Oils, 3);
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 9, section: section.Id);
        var line = Item(section: section.Id, sort: 4);
        var standalone = Item(sort: 1);

        var state = HKStructureEditorState.FromGraph(Graph(
            sections: new[] { section },
            nodes: new[] { aggregate },
            items: new[] { line, standalone }));

        var input = state.BuildInput();

        // Корневой контейнер: самостоятельная строка 0, раздел 1.
        Assert.Equal(0, input.Items.Single(i => i.Id == standalone.Id).SortOrder);
        Assert.Equal(1, input.Sections.Single().SortOrder);
        // Контейнер раздела — своя шкала.
        Assert.Equal(0, input.Items.Single(i => i.Id == line.Id).SortOrder);
        Assert.Equal(1, input.Nodes.Single(n => n.Id == aggregate.Id).SortOrder);
    }

    // ── Переносы ──

    /// <summary>
    /// Перенос строки под другую позицию сохраняет идентификатор, норму и
    /// назначенные марки.
    /// </summary>
    [Fact]
    public void MoveItem_UnderOtherNode_KeepsData()
    {
        var first = Node(HKObjectLevel.Aggregate, sort: 0);
        var second = Node(HKObjectLevel.Aggregate, sort: 1);
        var line = Item(node: first.Id, quantity: 5, volume: 750m, unit: "г",
            materials: (Guid.NewGuid(), GsmCategory.Reserve));

        var state = HKStructureEditorState.FromGraph(
            Graph(nodes: new[] { first, second }, items: new[] { line }));

        Assert.True(state.TryMoveItem(line.Id, second.Id, out var error));
        Assert.Null(error);

        var moved = state.Items.Single();
        Assert.Equal(line.Id, moved.Id);
        Assert.Equal(second.Id, moved.NodeId);
        Assert.Equal(5, moved.Quantity);
        Assert.Equal(750m, moved.Volume);
        Assert.Equal("г", moved.UnitOfMeasure);
        Assert.Equal("12 мес", moved.Periodicity);
        Assert.Equal("примечание", moved.Notes);
        Assert.Single(moved.Materials);
    }

    /// <summary>Перенос строки под корень снимает с неё собственный раздел.</summary>
    [Fact]
    public void MoveItem_UnderRoot_ClearsSection()
    {
        var section = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Oils, 0);
        var line = Item(section: section.Id);

        var state = HKStructureEditorState.FromGraph(
            Graph(sections: new[] { section }, items: new[] { line }));

        Assert.True(state.TryMoveItem(line.Id, null, out _));
        Assert.Null(state.Items.Single().SectionId);
    }

    /// <summary>Цикл запрещён: позицию нельзя вложить в её собственного потомка.</summary>
    [Fact]
    public void MovePosition_IntoOwnDescendant_IsRefused()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 0);
        var child = Node(HKObjectLevel.Node, parent: aggregate.Id, sort: 0);

        var state = HKStructureEditorState.FromGraph(Graph(nodes: new[] { aggregate, child }));

        Assert.False(state.TryMovePosition(aggregate.Id, child.Id, out var error));
        Assert.NotNull(error);
        // Перенос отвергнут: верхняя позиция осталась верхней.
        Assert.Null(state.Nodes.Single(n => n.Id == aggregate.Id).ParentId);
    }

    /// <summary>Недопустимая вложенность отвергается тем же правилом уровней.</summary>
    [Fact]
    public void MovePosition_DisallowedLevel_IsRefused()
    {
        var equipment = Node(HKObjectLevel.EquipmentModel, sort: 0);
        var aggregate = Node(HKObjectLevel.Aggregate, sort: 1);

        var state = HKStructureEditorState.FromGraph(Graph(nodes: new[] { equipment, aggregate }));

        // Под изделием допустимы агрегат и узел.
        Assert.True(state.TryMovePosition(aggregate.Id, equipment.Id, out var error));
        Assert.Null(error);

        // Под узлом дочерних позиций нет: агрегат туда не вложить.
        var childNode = state.AddChildPosition(equipment.Id, HKObjectLevel.Node);
        Assert.False(state.TryMovePosition(aggregate.Id, childNode.Id, out var refusal));
        Assert.NotNull(refusal);
        Assert.Equal(equipment.Id, state.Nodes.Single(n => n.Id == aggregate.Id).ParentId);
    }

    /// <summary>Раздел ветки наследуется целиком: у вложенной позиции его нет.</summary>
    [Fact]
    public void NewBranchInheritsSection_WithoutOwnSection()
    {
        var sectionId = Guid.NewGuid();
        var state = HKStructureEditorState.FromGraph(Graph(
            sections: new[]
            {
                new HKStructureSectionInput(sectionId, HKCardSectionType.Oils, 0)
            }));

        state.NewBranchSectionType = HKCardSectionType.Oils;
        var top = state.AddTopPosition(HKObjectLevel.Aggregate);
        var child = state.AddChildPosition(top.Id, HKObjectLevel.Node);

        Assert.Equal(sectionId, top.SectionId);
        Assert.Null(child.SectionId);
    }

    // ── Разделы ──

    /// <summary>Пустой раздел удаляется, непустой — с понятным отказом.</summary>
    [Fact]
    public void RemoveSection_EmptySucceeds_NonEmptyRefused()
    {
        var oils = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Oils, 0);
        var fuels = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Fuels, 1);
        var aggregate = Node(HKObjectLevel.Aggregate, section: oils.Id, sort: 0);

        var state = HKStructureEditorState.FromGraph(Graph(
            sections: new[] { oils, fuels }, nodes: new[] { aggregate }));

        Assert.False(state.TryRemoveSection(oils.Id, out var refusal));
        Assert.Contains("перенесите", refusal);

        Assert.True(state.TryRemoveSection(fuels.Id, out var error));
        Assert.Null(error);
        Assert.Single(state.Sections);
    }

    /// <summary>
    /// После переноса содержимого опустевший раздел удаляется тем же
    /// сохранением — отдельного шага удаления содержимого не требуется.
    /// </summary>
    [Fact]
    public void RemoveSection_AfterTransfer_Succeeds()
    {
        var oils = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Oils, 0);
        var aggregate = Node(HKObjectLevel.Aggregate, section: oils.Id, sort: 0);

        var state = HKStructureEditorState.FromGraph(
            Graph(sections: new[] { oils }, nodes: new[] { aggregate }));

        var branch = state.Nodes.Single();
        branch.SectionId = null;
        state.MarkDirty();

        Assert.True(state.TryRemoveSection(oils.Id, out _));
        Assert.Empty(state.Sections);
        Assert.Null(branch.SectionId);
    }

    /// <summary>Раздел каждого вида на карте единственный.</summary>
    [Fact]
    public void AddSection_KeepsSingleSectionPerType()
    {
        var state = HKStructureEditorState.FromGraph(Graph());

        var first = state.AddSection(HKCardSectionType.Oils);
        var again = state.AddSection(HKCardSectionType.Oils);

        Assert.Equal(first.Id, again.Id);
        Assert.Single(state.Sections);

        state.AddSection(HKCardSectionType.Greases);
        Assert.Equal(2, state.Sections.Count);
    }

    /// <summary>Панель выбора влияет только на новую ветку.</summary>
    [Fact]
    public void NewBranchSection_AppliesOnlyToNewBranches()
    {
        var existing = Node(HKObjectLevel.Aggregate, sort: 0, section: null);
        var state = HKStructureEditorState.FromGraph(Graph(nodes: new[] { existing }));

        // Выбор только запоминает вид раздела: сам раздел создаётся вместе
        // с первой веткой.
        state.NewBranchSectionType = HKCardSectionType.Oils;
        Assert.Empty(state.Sections);

        var created = state.AddTopPosition(HKObjectLevel.Aggregate);

        Assert.Equal(HKCardSectionType.Oils,
            state.Sections.Single().SectionType);
        Assert.Equal(state.Sections.Single().Id, created.SectionId);
        Assert.Null(state.Nodes.Single(n => n.Id == existing.Id).SectionId);
    }

    // ── Удаление ──

    /// <summary>Удаление ветки затрагивает только её содержимое.</summary>
    [Fact]
    public void RemovePosition_DeletesOnlyItsBranch()
    {
        var keep = Node(HKObjectLevel.Aggregate, sort: 0);
        var doomed = Node(HKObjectLevel.Aggregate, sort: 1);
        var doomedChild = Node(HKObjectLevel.Node, parent: doomed.Id, sort: 0);
        var doomedLine = Item(node: doomedChild.Id);
        var keepLine = Item(node: keep.Id);

        var state = HKStructureEditorState.FromGraph(Graph(
            nodes: new[] { keep, doomed, doomedChild },
            items: new[] { doomedLine, keepLine }));

        var impact = state.RemovePosition(doomed.Id);

        Assert.Contains("конечных строк: 1", impact);
        Assert.Equal(new[] { keep.Id }, state.Nodes.Select(n => n.Id).ToArray());
        Assert.Equal(new[] { keepLine.Id }, state.Items.Select(i => i.Id).ToArray());
    }

    /// <summary>
    /// До подтверждения удаления данные на месте: подтверждение — отдельное
    /// действие, отмена ничего не меняет.
    /// </summary>
    [Fact]
    public void Removal_HappensOnlyWhenConfirmed()
    {
        var node = Node(HKObjectLevel.Aggregate, name: "Агрегат");
        var line = Item(node: node.Id, quantity: 3, volume: 250m, unit: "г",
            materials: (Guid.NewGuid(), GsmCategory.Primary));

        var state = HKStructureEditorState.FromGraph(Graph(nodes: new[] { node }, items: new[] { line }));

        // Описание содержимого запрашивается ДО удаления и само ничего не меняет.
        var impact = state.PositionImpactText(node.Id);
        Assert.Contains("конечных строк: 1", impact);
        Assert.Single(state.Nodes);
        Assert.Single(state.Items);
        Assert.False(state.IsDirty);

        state.RemovePosition(node.Id);
        Assert.True(state.IsDirty);
        Assert.Empty(state.Nodes);
        Assert.Empty(state.Items);
    }

    /// <summary>Удаление строки описывает её содержимое и не трогает соседние.</summary>
    [Fact]
    public void RemoveItem_DescribesAndKeepsOthers()
    {
        var keep = Item(quantity: 1);
        var doomed = Item(quantity: 7, volume: 2m, unit: "л",
            materials: (Guid.NewGuid(), GsmCategory.Reserve));

        var state = HKStructureEditorState.FromGraph(Graph(items: new[] { keep, doomed }));

        var impact = state.RemoveItem(doomed.Id);

        Assert.Contains("марок ГСМ — 1", impact);
        Assert.Equal(new[] { keep.Id }, state.Items.Select(i => i.Id).ToArray());
    }

    // ── Команда сохранения ──

    /// <summary>
    /// Команда строится из локальной модели: идентификаторы стабильны, раздел
    /// есть только у верхней позиции и у строки под корнем.
    /// </summary>
    [Fact]
    public void BuildCommand_CarriesStableIdsAndInheritedSections()
    {
        var section = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Greases, 0);
        var aggregate = Node(HKObjectLevel.Aggregate, section: section.Id, sort: 0, name: "Агрегат");
        var child = Node(HKObjectLevel.Node, parent: aggregate.Id, sort: 0, name: "Узел");
        var line = Item(node: child.Id, sort: 0, materials: (Guid.NewGuid(), GsmCategory.Duplicate));

        var state = HKStructureEditorState.FromGraph(Graph(
            sections: new[] { section }, nodes: new[] { aggregate, child }, items: new[] { line }));

        var command = state.BuildCommand();

        Assert.Equal(7u, command.ExpectedRowVersion);
        Assert.Equal(section.Id, Assert.Single(command.Sections).Id);

        var top = command.Positions.Single(p => p.Id == aggregate.Id);
        Assert.Equal(section.Id, top.SectionId);
        Assert.Equal("Агрегат", top.Name);

        var nested = command.Positions.Single(p => p.Id == child.Id);
        Assert.Null(nested.SectionId);

        var item = Assert.Single(command.Items);
        Assert.Equal(line.Id, item.Id);
        Assert.Null(item.SectionId);
        Assert.Equal(GsmCategory.Duplicate, Assert.Single(item.Materials).Category);
    }

    /// <summary>Новая карта: разделы не создаются сами, состояние пустое и чистое.</summary>
    [Fact]
    public void NewCard_HasNoSectionsAndNoDirtyFlag()
    {
        var state = HKStructureEditorState.FromGraph(Graph());

        Assert.Empty(state.Sections);
        Assert.Empty(state.Nodes);
        Assert.Empty(state.Items);
        Assert.False(state.IsDirty);
        Assert.Empty(state.Rows());
        Assert.Empty(state.Totals().Overall);
    }

    /// <summary>
    /// Итог выбранной позиции доступен и на один экземпляр, и по всем
    /// экземплярам; итог ребёнка не суммируется с итогом родителя дважды.
    /// </summary>
    [Fact]
    public void SelectedTotals_DistinguishPerOneAndCard()
    {
        var aggregate = Node(HKObjectLevel.Aggregate, quantity: 2);
        var node = Node(HKObjectLevel.Node, parent: aggregate.Id, quantity: 3);
        var line = Item(node: node.Id, quantity: 4, volume: 500m, unit: "г");

        var state = HKStructureEditorState.FromGraph(
            Graph(nodes: new[] { aggregate, node }, items: new[] { line }));

        state.Selected = (HKStructureRowKind.Position, aggregate.Id);
        var (position, _) = state.SelectedTotals();

        Assert.True(position is not null,
            "Позиционный итог верхней ветки должен находиться по её Id.");
        // Один агрегат: 500 г × 4 × 3 = 6000 г; вся ветка 12000 г.
        Assert.Equal(6000m, Assert.Single(position!.PerOne).BaseValue);
        Assert.Equal(12000m, Assert.Single(position.TotalInCard).BaseValue);

        state.Selected = (HKStructureRowKind.TerminalRow, line.Id);
        var (_, item) = state.SelectedTotals();

        Assert.NotNull(item);
        Assert.Equal(500m, Assert.Single(item!.PerOne).BaseValue);
    }

    /// <summary>Порядок разделов меняется кнопками перемещения.</summary>
    [Fact]
    public void MoveSection_ReordersAmongSections()
    {
        var oils = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Oils, 0);
        var fuels = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Fuels, 1);
        var greases = new HKStructureSectionInput(Guid.NewGuid(), HKCardSectionType.Greases, 2);

        var state = HKStructureEditorState.FromGraph(
            Graph(sections: new[] { oils, fuels, greases }));

        state.MoveSection(greases.Id, -1);

        var order = state.Sections.OrderBy(s => s.SortOrder).Select(s => s.Id).ToArray();
        Assert.Equal(new[] { oils.Id, greases.Id, fuels.Id }, order);
    }
}