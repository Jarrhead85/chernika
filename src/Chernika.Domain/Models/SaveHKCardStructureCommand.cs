using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>Итоговый раздел карты в команде сохранения структуры.</summary>
public sealed record HKCardSectionWrite(
    Guid Id, HKCardSectionType SectionType, int SortOrder);

/// <summary>
/// Итоговая структурная позиция в команде сохранения.
/// <para>
/// Раздел заполняется только у верхней позиции: вложенная наследует раздел
/// своего корня.
/// </para>
/// </summary>
public sealed record HKCardStructureNodeWrite(
    Guid Id, Guid? ParentId, HKObjectLevel Level, string Name,
    int Quantity, int SortOrder, Guid? SectionId);

/// <summary>Итоговое назначение марки ГСМ строке.</summary>
public sealed record HKCardMaterialWrite(
    Guid Id, Guid GsmMaterialId, GsmCategory Category);

/// <summary>
/// Итоговая конечная строка сборочной единицы.
/// <para>
/// Раздел заполняется только у строки непосредственно под корнем карты.
/// </para>
/// </summary>
public sealed record HKCardStructureItemWrite(
    Guid Id, Guid? NodeId, Guid AssemblyUnitId, int Quantity, decimal Volume,
    string? UnitOfMeasure, string? Periodicity, string? Notes, int SortOrder,
    Guid? SectionId, IReadOnlyList<HKCardMaterialWrite> Materials);

/// <summary>
/// Единый запрос атомарного сохранения структуры карты.
/// <para>
/// Принимает ИТОГОВОЕ состояние разделов, позиций и строк, специальный выбор
/// верхнего узла и ожидаемую версию, полученную при загрузке карты.
/// </para>
/// <para>
/// Номер, статус, автор, указатель принятой редакции и другие служебные
/// свойства редактируемыми не являются и в контракт не входят.
/// </para>
/// </summary>
public sealed record SaveHKCardStructureCommand(
    Guid CardId,
    IReadOnlyList<HKCardSectionWrite> Sections,
    IReadOnlyList<HKCardStructureNodeWrite> Positions,
    IReadOnlyList<HKCardStructureItemWrite> Items,
    HKObjectLevel? NodeRootChoice,
    uint ExpectedRowVersion);