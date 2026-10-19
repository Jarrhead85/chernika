using Chernika.Domain.Enums;
using Chernika.Domain.Structure;

namespace Chernika.Domain.Models;

/// <summary>
/// Марка ГСМ строки собственного дерева в read-only проекции.
/// <para>
/// Наименование и НД сняты в момент чтения вместе с признаком мягкого удаления:
/// удалённая марка остаётся в графе и показывается с пометкой, а не исчезает
/// тихо вместе с глобальным фильтром.
/// </para>
/// </summary>
public sealed record HKStructureGraphMaterial(
    Guid Id,
    Guid GsmMaterialId,
    string? Name,
    string? Nd,
    string? Category,
    bool SourceIsDeleted);

/// <summary>
/// Конечная строка сборочной единицы в read-only проекции полного графа.
/// </summary>
public sealed record HKStructureGraphItem(
    Guid Id,
    Guid? NodeId,
    Guid AssemblyUnitId,
    string? AssemblyUnitName,
    string? AssemblyUnitCode,
    int Quantity,
    decimal Volume,
    string? UnitOfMeasure,
    string? Periodicity,
    string? Notes,
    int SortOrder,
    Guid? SectionId,
    IReadOnlyList<HKStructureGraphMaterial> Materials);

/// <summary>
/// Read-only проекция полного рабочего графа собственной структуры ХК:
/// реквизиты карты, разделы, позиции, конечные строки с материалами.
/// <para>
/// Проекция существует, чтобы редактор не редактировал EF-граф напрямую и
/// не собирал бизнес-данные запросами из Razor: состояние редактора строится
/// из этой модели глубоким копированием. Чтение выполняется целиком
/// AsNoTracking — новые добавления и переносы не затрагивают ChangeTracker.
/// </para>
/// </summary>
public sealed record HKStructureGraph(
    Guid CardId,
    uint RowVersion,
    HKCardStatus Status,
    bool IsAutonomous,
    /// <summary>Специальный выбор верхнего уровня, как он задан на карте.</summary>
    HKObjectLevel? NodeRootCardLevel,
    Guid BranchId,
    IReadOnlyList<HKStructureSectionInput> Sections,
    IReadOnlyList<HKStructureNodeInput> Nodes,
    IReadOnlyList<HKStructureGraphItem> Items);
