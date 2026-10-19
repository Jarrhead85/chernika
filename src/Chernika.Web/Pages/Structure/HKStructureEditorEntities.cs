using System.ComponentModel;
using System.Runtime.CompilerServices;
using Chernika.Domain.Enums;

namespace Chernika.Web.Pages.Structure;

/// <summary>
/// Базовая запись локального состояния редактора.
/// <para>
/// Изменение любого поля помечает состояние как несохранённое само, без
/// отдельного вызова со стороны разметки: обычный двусторонний биндинг
/// (@bind) не должен оставлять признак изменений пустым.
/// </para>
/// <para>
/// Это НЕ EF-сущность: событие изменений не попадает в ChangeTracker.
/// </para>
/// </summary>
public abstract class EditorEntity : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Признак того, что запись изменена с последнего сохранения.</summary>
    public bool IsModified { get; private set; }

    protected void Touch([CallerMemberName] string? propertyName = null)
    {
        IsModified = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Touch(propertyName);
        return true;
    }

    /// <summary>Снимает признак изменения после успешного сохранения.</summary>
    public void AcceptChanges() => IsModified = false;

    /// <summary>Глубокая копия записи: независимое состояние редактора.</summary>
    public abstract EditorEntity CloneEntity();
}

/// <summary>Раздел карты в локальном состоянии редактора.</summary>
public sealed class EditorSection : EditorEntity
{
    private Guid _id;
    private HKCardSectionType _sectionType;
    private int _sortOrder;

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public HKCardSectionType SectionType { get => _sectionType; set => Set(ref _sectionType, value); }
    public int SortOrder { get => _sortOrder; set => Set(ref _sortOrder, value); }

    public override EditorEntity CloneEntity() =>
        new EditorSection { Id = Id, SectionType = SectionType, SortOrder = SortOrder };
}

/// <summary>
/// Структурная позиция в локальном состоянии редактора.
/// <para>
/// Не доменная сущность и не EF-объект: правки редактора не должны попадать
/// в ChangeTracker до явного сохранения.
/// </para>
/// </summary>
public sealed class EditorNode : EditorEntity
{
    private Guid _id;
    private Guid? _parentId;
    private HKObjectLevel _level;
    private string _name = string.Empty;
    private int _quantity = 1;
    private int _sortOrder;
    private Guid? _sectionId;

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public Guid? ParentId { get => _parentId; set => Set(ref _parentId, value); }
    public HKObjectLevel Level { get => _level; set => Set(ref _level, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public int Quantity { get => _quantity; set => Set(ref _quantity, value); }
    public int SortOrder { get => _sortOrder; set => Set(ref _sortOrder, value); }
    public Guid? SectionId { get => _sectionId; set => Set(ref _sectionId, value); }

    public override EditorEntity CloneEntity() =>
        new EditorNode
        {
            Id = Id, ParentId = ParentId, Level = Level, Name = Name,
            Quantity = Quantity, SortOrder = SortOrder, SectionId = SectionId
        };
}

/// <summary>Назначение марки ГСМ строке в локальном состоянии редактора.</summary>
public sealed class EditorMaterial : EditorEntity
{
    private Guid _id;
    private Guid _gsmMaterialId;
    private GsmCategory _category;

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public Guid GsmMaterialId { get => _gsmMaterialId; set => Set(ref _gsmMaterialId, value); }
    public GsmCategory Category { get => _category; set => Set(ref _category, value); }

    /// <summary>Наименование марки на момент загрузки — только для показа.</summary>
    public string? Name { get; init; }

    public override EditorEntity CloneEntity() =>
        new EditorMaterial { Id = Id, GsmMaterialId = GsmMaterialId, Category = Category, Name = Name };
}

/// <summary>Конечная строка сборочной единицы в локальном состоянии редактора.</summary>
public sealed class EditorItem : EditorEntity
{
    private Guid _id;
    private Guid? _nodeId;
    private Guid _assemblyUnitId;
    private int _quantity = 1;
    private decimal _volume;
    private string? _unitOfMeasure;
    private string? _periodicity;
    private string? _notes;
    private int _sortOrder;
    private Guid? _sectionId;

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public Guid? NodeId { get => _nodeId; set => Set(ref _nodeId, value); }
    public Guid AssemblyUnitId { get => _assemblyUnitId; set => Set(ref _assemblyUnitId, value); }
    public int Quantity { get => _quantity; set => Set(ref _quantity, value); }
    public decimal Volume { get => _volume; set => Set(ref _volume, value); }
    public string? UnitOfMeasure { get => _unitOfMeasure; set => Set(ref _unitOfMeasure, value); }
    public string? Periodicity { get => _periodicity; set => Set(ref _periodicity, value); }
    public string? Notes { get => _notes; set => Set(ref _notes, value); }
    public int SortOrder { get => _sortOrder; set => Set(ref _sortOrder, value); }
    public Guid? SectionId { get => _sectionId; set => Set(ref _sectionId, value); }

    /// <summary>Наименование сборочной единицы на момент загрузки — только для показа.</summary>
    public string? AssemblyUnitName { get; init; }

    /// <summary>
    /// Назначенные марки. Список доступен на изменение: добавление и удаление
    /// выполняются методами состояния, чтобы изменение было зарегистрировано.
    /// </summary>
    public List<EditorMaterial> Materials { get; } = new();

    public override EditorEntity CloneEntity()
    {
        var copy = new EditorItem
        {
            Id = Id,
            NodeId = NodeId,
            AssemblyUnitId = AssemblyUnitId,
            AssemblyUnitName = AssemblyUnitName,
            Quantity = Quantity,
            Volume = Volume,
            UnitOfMeasure = UnitOfMeasure,
            Periodicity = Periodicity,
            Notes = Notes,
            SortOrder = SortOrder,
            SectionId = SectionId
        };

        copy.Materials.AddRange(Materials.Select(m => (EditorMaterial)m.CloneEntity()));
        return copy;
    }
}