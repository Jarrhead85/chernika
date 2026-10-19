using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Domain.Structure;

namespace Chernika.Web.Pages.Structure;

/// <summary>
/// Независимое состояние редактора собственной структуры.
/// <para>
/// Модель не содержит EF-сущностей и не ссылается на ChangeTracker: она
/// строится глубоким копированием read-only проекции графа. Добавление,
/// перенос, удаление и ввод полей до сохранения меняют только её.
/// </para>
/// <para>
/// Идентификаторы стабильны: номер позиции и номер строки не являются
/// идентификаторами и в выпадающих списках не используются.
/// </para>
/// </summary>
public sealed class HKStructureEditorState
{
    /// <summary>Карта, которой принадлежит состояние.</summary>
    public Guid CardId { get; private set; }

    public List<EditorSection> Sections { get; private set; } = new();
    public List<EditorNode> Nodes { get; private set; } = new();
    public List<EditorItem> Items { get; private set; } = new();

    /// <summary>Версия карты, полученная при загрузке этого состояния.</summary>
    public uint LoadedRowVersion { get; private set; }

    /// <summary>В структуре есть несохранённые изменения.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Раздел, который получит новая самостоятельная ветка.</summary>
    public Guid? NewBranchSectionId { get; set; }

    /// <summary>Уровень следующей верхней позиции.</summary>
    public HKObjectLevel TopPositionLevel { get; set; } = HKObjectLevel.Aggregate;

    /// <summary>Выбранный элемент для подсветки: вид и устойчивый Id.</summary>
    public (HKStructureRowKind Kind, Guid Id)? Selected { get; set; }

    /// <summary>
    /// Свёрнутые элементы — состояние показа, а не нормативного содержания:
    /// на перемещения, номера и итоги оно не влияет.
    /// </summary>
    public HashSet<Guid> Collapsed { get; private set; } = new();

    /// <summary>Специальный выбор уровня верхнего узла.</summary>
    public HKObjectLevel? NodeRootChoice { get; set; }

    private HKStructureEditorState() { }

    /// <summary>
    /// Строит состояние из read-only проекции графа. Проекция — копия, поэтому
    /// ни одна правка редактора не дотянется до загруженных сущностей.
    /// </summary>
    public static HKStructureEditorState FromGraph(HKStructureGraph graph)
    {
        var state = new HKStructureEditorState
        {
            CardId = graph.CardId,
            LoadedRowVersion = graph.RowVersion,
            NodeRootChoice = graph.NodeRootCardLevel
        };

        state.Sections = graph.Sections
            .Select(s => new EditorSection
            {
                Id = s.Id,
                SectionType = s.SectionType,
                SortOrder = s.SortOrder
            })
            .ToList();

        state.Nodes = graph.Nodes
            .Select(n => new EditorNode
            {
                Id = n.Id,
                ParentId = n.ParentId,
                Level = n.Level,
                Name = n.Name,
                Quantity = n.Quantity,
                SortOrder = n.SortOrder,
                SectionId = n.SectionId
            })
            .ToList();

        state.Items = graph.Items.Select(i =>
        {
            var item = new EditorItem
            {
                Id = i.Id,
                NodeId = i.NodeId,
                AssemblyUnitId = i.AssemblyUnitId,
                AssemblyUnitName = i.AssemblyUnitName,
                Quantity = i.Quantity,
                Volume = i.Volume,
                UnitOfMeasure = i.UnitOfMeasure,
                Periodicity = i.Periodicity,
                Notes = i.Notes,
                SortOrder = i.SortOrder,
                SectionId = i.SectionId
            };

            item.Materials.AddRange(i.Materials.Select(m => new EditorMaterial
            {
                Id = m.Id,
                GsmMaterialId = m.GsmMaterialId,
                Category = Enum.TryParse<GsmCategory>(m.Category, out var cat) ? cat : GsmCategory.Primary,
                Name = m.Name
            }));

            return item;
        }).ToList();

        foreach (var entity in state.AllEntities())
            entity.PropertyChanged += (_, _) => state.IsDirty = true;

        state.IsDirty = false;
        return state;
    }

    /// <summary>Все записи состояния: разделы, позиции, строки и материалы.</summary>
    private IEnumerable<EditorEntity> AllEntities() =>
        Sections.Cast<EditorEntity>()
            .Concat(Nodes)
            .Concat(Items)
            .Concat(Items.SelectMany(i => i.Materials));

    // ── Отслеживание изменений ──

    public void MarkDirty() => IsDirty = true;

    public void MarkClean() => IsDirty = false;

    public bool IsCollapsed(Guid nodeId) => Collapsed.Contains(nodeId);

    public void ToggleCollapse(Guid nodeId)
    {
        // Свёрнутое или развёрнутое — это просмотр, а не правка содержания.
        if (!Collapsed.Remove(nodeId))
            Collapsed.Add(nodeId);
    }

    // ── Разделы ──

    public EditorSection? SectionById(Guid? id) =>
        id is null ? null : Sections.FirstOrDefault(s => s.Id == id.Value);

    public EditorSection AddSection(HKCardSectionType type)
    {
        // Один раздел каждого вида на карте.
        var existing = Sections.FirstOrDefault(s => s.SectionType == type);
        if (existing is not null)
            return existing;

        var section = new EditorSection
        {
            Id = Guid.NewGuid(),
            SectionType = type,
            SortOrder = Sections.Count
        };
        Sections.Add(section);
        MarkDirty();
        return section;
    }

    /// <summary>
    /// Удаляет раздел. Непустой раздел не удаляется: возвращается понятный
    /// отказ, а содержимое остаётся на месте.
    /// </summary>
    public bool TryRemoveSection(Guid sectionId, out string? error)
    {
        error = null;

        if (Nodes.Any(n => n.SectionId == sectionId))
        {
            error = "Раздел содержит ветки: сначала перенесите их в другой раздел.";
            return false;
        }

        if (Items.Any(i => i.SectionId == sectionId))
        {
            error = "Раздел содержит строки: сначала перенесите их в другой раздел.";
            return false;
        }

        var section = Sections.FirstOrDefault(s => s.Id == sectionId);
        if (section is null)
        {
            error = "Раздел не найден.";
            return false;
        }

        Sections.Remove(section);
        if (NewBranchSectionId == sectionId)
            NewBranchSectionId = null;
        MarkDirty();
        return true;
    }

    public void MoveSection(Guid sectionId, int delta)
    {
        var ordered = Sections.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();
        var index = ordered.FindIndex(s => s.Id == sectionId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count)
            return;

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;

        MarkDirty();
    }

    /// <summary>Описание содержимого раздела для подтверждения удаления.</summary>
    public string SectionImpactText(Guid sectionId)
    {
        var branches = Nodes.Count(n => n.SectionId == sectionId);
        var lines = Items.Count(i => i.SectionId == sectionId);

        if (branches == 0 && lines == 0)
            return "Раздел пуст, его удаление ничего не меняет в структуре.";

        return $"В разделе: веток — {branches}, строк под корнем — {lines}. "
            + "Их нужно перенести в другой раздел или удалить явно.";
    }

    // ── Позиции ──

    public EditorNode? NodeById(Guid? id) =>
        id is null ? null : Nodes.FirstOrDefault(n => n.Id == id.Value);

    public EditorItem? ItemById(Guid? id) =>
        id is null ? null : Items.FirstOrDefault(i => i.Id == id.Value);

    public EditorNode AddTopPosition(HKObjectLevel level)
    {
        var node = new EditorNode
        {
            Id = Guid.NewGuid(),
            ParentId = null,
            Level = level,
            Name = string.Empty,
            Quantity = 1,
            SortOrder = Nodes.Count(n => n.ParentId is null),
            SectionId = NewBranchSectionId
        };

        Nodes.Add(node);
        MarkDirty();
        return node;
    }

    public EditorNode AddChildPosition(Guid parentId, HKObjectLevel level)
    {
        var node = new EditorNode
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Level = level,
            Name = string.Empty,
            Quantity = 1,
            SortOrder = Nodes.Count(n => n.ParentId == parentId),
            // Вложенная позиция раздела не имеет: раздел наследует от корня.
            SectionId = null
        };

        Nodes.Add(node);
        MarkDirty();
        return node;
    }

    /// <summary>Удаляет ветку вместе со всеми потомками и строками внутри неё.</summary>
    public string RemovePosition(Guid nodeId)
    {
        // Описание затрагиваемого содержимого считается ДО удаления: после
        // удаления ветки не осталось бы ни позиций, ни строк.
        var impact = PositionImpactText(nodeId);

        var doomed = new HashSet<Guid> { nodeId };
        bool grown;
        do
        {
            grown = false;
            foreach (var child in Nodes.Where(n => n.ParentId is { } p && doomed.Contains(p)))
            {
                if (doomed.Add(child.Id))
                    grown = true;
            }
        }
        while (grown);

        Nodes.RemoveAll(n => doomed.Contains(n.Id));
        Items.RemoveAll(i => i.NodeId is { } node && doomed.Contains(node));
        MarkDirty();
        return impact;
    }

    public string PositionImpactText(Guid nodeId)
    {
        var doomed = new HashSet<Guid> { nodeId };
        bool grown;
        do
        {
            grown = false;
            foreach (var child in Nodes.Where(n => n.ParentId is { } p && doomed.Contains(p)))
            {
                if (doomed.Add(child.Id))
                    grown = true;
            }
        }
        while (grown);

        var rows = Items.Count(i => i.NodeId is { } node && doomed.Contains(node));
        var positions = doomed.Count;

        return $"В удаляемой ветке позиций: {positions}, конечных строк: {rows}. "
            + "Сборочные единицы, нормы и назначенные марки ГСМ будут удалены вместе с ней.";
    }

    /// <summary>Переносит верхнюю позицию под другой раздел.</summary>
    public bool TrySetTopSection(Guid nodeId, Guid? sectionId, out string? error)
    {
        error = null;
        var node = NodeById(nodeId);
        if (node is null)
        {
            error = "Позиция не найдена.";
            return false;
        }

        if (node.ParentId is not null)
        {
            error = "Раздел меняется только у верхней позиции: вложенная наследует раздел корня.";
            return false;
        }

        if (sectionId is not null && !Sections.Any(s => s.Id == sectionId.Value))
        {
            error = "Раздел не найден.";
            return false;
        }

        node.SectionId = sectionId;
        MarkDirty();
        return true;
    }

    /// <summary>Раздел строки под корнем; у строки под позицией раздела нет.</summary>
    public bool TrySetRootItemSection(Guid itemId, Guid? sectionId, out string? error)
    {
        error = null;
        var item = ItemById(itemId);
        if (item is null)
        {
            error = "Строка не найдена.";
            return false;
        }

        if (item.NodeId is not null)
        {
            error = "Раздел меняется только у строки под корнем карты.";
            return false;
        }

        if (sectionId is not null && !Sections.Any(s => s.Id == sectionId.Value))
        {
            error = "Раздел не найден.";
            return false;
        }

        item.SectionId = sectionId;
        MarkDirty();
        return true;
    }

    // ── Конечные строки ──

    public EditorItem AddItem(Guid? nodeId, Guid assemblyUnitId, string? assemblyUnitName = null)
    {
        var item = new EditorItem
        {
            Id = Guid.NewGuid(),
            NodeId = nodeId,
            AssemblyUnitId = assemblyUnitId,
            AssemblyUnitName = assemblyUnitName,
            Quantity = 1,
            SortOrder = nodeId is null
                ? Items.Count(i => i.NodeId is null)
                : Items.Count(i => i.NodeId == nodeId),
            // Строка под позицией наследует раздел ветки.
            SectionId = null
        };

        Items.Add(item);
        MarkDirty();
        return item;
    }

    public string RemoveItem(Guid itemId)
    {
        var item = ItemById(itemId);
        if (item is null)
            return "Строка не найдена.";

        Items.Remove(item);
        MarkDirty();

        return $"Строка «{item.AssemblyUnitName ?? "без сборочной единицы"}»: "
            + $"норма {item.Quantity} × {item.Volume} {item.UnitOfMeasure}, "
            + $"марок ГСМ — {item.Materials.Count}. Строка будет удалена.";
    }

    // ── Марки строки ──

    /// <summary>Назначает марку ГСМ строке.</summary>
    public EditorMaterial AddMaterial(Guid itemId, Guid gsmMaterialId, GsmCategory category)
    {
        var item = ItemById(itemId)
            ?? throw new InvalidOperationException("Строка не найдена.");

        var material = new EditorMaterial
        {
            Id = Guid.NewGuid(),
            GsmMaterialId = gsmMaterialId,
            Category = category
        };

        material.PropertyChanged += (_, _) => IsDirty = true;
        item.Materials.Add(material);
        MarkDirty();
        return material;
    }

    public void RemoveMaterial(Guid itemId, Guid materialId)
    {
        var item = ItemById(itemId);
        if (item is null)
            return;

        item.Materials.RemoveAll(m => m.Id == materialId);
        MarkDirty();
    }

    // ── Переносы и порядок ──

    public HKStructureInput BuildInput()
    {
        // Порядок нормализуется по контейнерам доменным механизмом: правила
        // соседей не дублируются в редакторе.
        return HKStructureOrdering.Normalize(new HKStructureInput(
            Sections
                .Select(s => new HKStructureSectionInput(s.Id, s.SectionType, s.SortOrder))
                .ToList(),
            Nodes
                .Select(n => new HKStructureNodeInput(
                    n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder, n.SectionId))
                .ToList(),
            Items
                .Select(i => new HKStructureItemInput(
                    i.Id, i.NodeId, i.Quantity, i.Volume, i.UnitOfMeasure, i.SortOrder,
                    i.SectionId))
                .ToList()));
    }

    public IReadOnlyList<HKStructureRow> Rows() => HKStructureNumbering.Build(BuildInput());

    public HKCardTotals Totals() => HKStructureTotals.Calculate(BuildInput());

    public bool CanMovePosition(Guid nodeId, int delta) =>
        CanMove(nodeId, null, delta);

    public bool CanMoveItem(Guid itemId, int delta) =>
        CanMove(null, itemId, delta);

    private bool CanMove(Guid? nodeId, Guid? itemId, int delta)
    {
        var input = BuildInput();
        var siblings = HKStructureOrdering.SiblingsOf(input, nodeId, itemId);
        if (siblings.Count < 2)
            return false;

        var index = siblings.ToList().FindIndex(s =>
            (nodeId is not null && s.NodeId == nodeId) ||
            (itemId is not null && s.ItemId == itemId));

        return index >= 0 && index + delta >= 0 && index + delta < siblings.Count;
    }

    /// <summary>
    /// Перемещение среди настоящих соседей своего контейнера: корень, раздел
    /// или позиция. Плоский видимый порядок не используется — между соседними
    /// ветками могут находиться потомки.
    /// </summary>
    public bool MovePosition(Guid nodeId, int delta) => Move(nodeId, null, delta);

    public bool MoveItem(Guid itemId, int delta) => Move(null, itemId, delta);

    private bool Move(Guid? nodeId, Guid? itemId, int delta)
    {
        if (delta == 0)
            return false;

        var before = BuildInput();
        var moved = HKStructureOrdering.Move(before, nodeId, itemId, delta);
        if (ReferenceEquals(moved, before))
            return false;

        ApplyOrder(moved);
        MarkDirty();
        return true;
    }

    /// <summary>Перенос позиции под другого родителя или под корень.</summary>
    public bool TryMovePosition(Guid nodeId, Guid? newParentId, out string? error)
    {
        error = null;
        var node = NodeById(nodeId);
        if (node is null)
        {
            error = "Позиция не найдена.";
            return false;
        }

        if (newParentId is not null)
        {
            var parent = NodeById(newParentId);
            if (parent is null)
            {
                error = "Новая родительская позиция не найдена.";
                return false;
            }

            if (IsCycle(nodeId, newParentId.Value))
            {
                error = "Позицию нельзя вложить в её собственного потомка.";
                return false;
            }

            if (!AllowedChildLevels(parent.Level).Contains(node.Level))
            {
                error = "Такой уровень недопустим для дочерней позиции: "
                    + $"под «{LevelName(parent.Level)}» допустим "
                    + string.Join(", ", AllowedChildLevels(parent.Level).Select(LevelName)) + ".";
                return false;
            }
        }

        node.ParentId = newParentId;
        // Став верхней позицией, ветка получает раздел панели; у вложенной
        // позиции собственного раздела нет.
        if (newParentId is null)
            node.SectionId = NewBranchSectionId;

        MarkDirty();
        return true;
    }

    /// <summary>Перенос конечной строки под другую позицию или под корень.</summary>
    public bool TryMoveItem(Guid itemId, Guid? newNodeId, out string? error)
    {
        error = null;
        var item = ItemById(itemId);
        if (item is null)
        {
            error = "Строка не найдена.";
            return false;
        }

        if (newNodeId is not null && NodeById(newNodeId) is null)
        {
            error = "Целевая позиция не найдена.";
            return false;
        }

        item.NodeId = newNodeId;
        // Раздел есть только у строки под корнем.
        item.SectionId = null;

        MarkDirty();
        return true;
    }

    /// <summary>
    /// Цикл — это попытка вложить позицию в её собственного потомка: обход идёт
    /// по реальным родителям модели вверх до корня.
    /// </summary>
    private bool IsCycle(Guid nodeId, Guid newParentId)
    {
        var current = newParentId;
        while (current != Guid.Empty)
        {
            if (current == nodeId)
                return true;

            var parent = NodeById(current);
            if (parent?.ParentId is null)
                return false;

            current = parent.ParentId.Value;
        }

        return false;
    }

    /// <summary>Допустимые уровни дочерней позиции — одно правило проекта.</summary>
    public static IReadOnlyList<HKObjectLevel> AllowedChildLevels(HKObjectLevel parentLevel) =>
        parentLevel switch
        {
            HKObjectLevel.EquipmentModel => new[]
            {
                HKObjectLevel.Aggregate, HKObjectLevel.Node
            },
            HKObjectLevel.Aggregate => new[] { HKObjectLevel.Node },
            _ => Array.Empty<HKObjectLevel>()
        };

    private static string LevelName(HKObjectLevel level) => level switch
    {
        HKObjectLevel.EquipmentModel => "изделие",
        HKObjectLevel.Aggregate => "агрегат",
        HKObjectLevel.Node => "узел",
        _ => "уровень не определён"
    };

    /// <summary>Применяет нормализованный порядок доменного механизма к модели.</summary>
    private void ApplyOrder(HKStructureInput input)
    {
        foreach (var section in Sections)
        {
            var updated = input.Sections.FirstOrDefault(s => s.Id == section.Id);
            if (updated != default)
                section.SortOrder = updated.SortOrder;
        }

        foreach (var node in Nodes)
        {
            var updated = input.Nodes.FirstOrDefault(n => n.Id == node.Id);
            if (updated != default)
                node.SortOrder = updated.SortOrder;
        }

        foreach (var item in Items)
        {
            var updated = input.Items.First(i => i.Id == item.Id);
            item.SortOrder = updated.SortOrder;
        }
    }

    /// <summary>Команда атомарного сохранения из текущего состояния редактора.</summary>
    public SaveHKCardStructureCommand BuildCommand()
        => BuildCommand(CardId);

    /// <summary>
    /// Команда атомарного сохранения для указанной карты: версия берётся из
    /// состояния, то есть из версии, полученной при загрузке.
    /// </summary>
    public SaveHKCardStructureCommand BuildCommand(Guid cardId)
    {
        var normalized = BuildInput();

        var positions = normalized.Nodes
            .Select(n =>
            {
                var node = Nodes.First(x => x.Id == n.Id);
                return new HKCardStructureNodeWrite(
                    node.Id, node.ParentId, node.Level, node.Name.Trim(), node.Quantity,
                    node.SortOrder, node.ParentId is null ? node.SectionId : null);
            })
            .ToList();

        var items = normalized.Items
            .Select(i =>
            {
                var item = Items.First(x => x.Id == i.Id);
                return new HKCardStructureItemWrite(
                    item.Id, item.NodeId, item.AssemblyUnitId, item.Quantity, item.Volume,
                    item.UnitOfMeasure, item.Periodicity, item.Notes, item.SortOrder,
                    item.NodeId is null ? item.SectionId : null,
                    item.Materials
                        .Select(m => new HKCardMaterialWrite(m.Id, m.GsmMaterialId, m.Category))
                        .ToList());
            })
            .ToList();

        return new SaveHKCardStructureCommand(
            cardId,
            normalized.Sections
                .OrderBy(s => s.SortOrder)
                .Select(s => new HKCardSectionWrite(s.Id, s.SectionType, s.SortOrder))
                .ToList(),
            positions,
            items,
            NodeRootChoice,
            LoadedRowVersion);
    }

    /// <summary>Итог выбранной позиции: на один экземпляр и вклад всех экземпляров.</summary>
    public (HKPositionTotals? Position, HKPositionTotals? Item) SelectedTotals()
    {
        if (Selected is not { } selected)
            return (null, null);

        var input = BuildInput();

        return selected.Kind switch
        {
            HKStructureRowKind.Position =>
                (HKStructureTotals.CalculateNode(input, selected.Id), null),
            HKStructureRowKind.TerminalRow =>
                (null, HKStructureTotals.CalculateItem(input, selected.Id)),
            _ => (null, null)
        };
    }
}