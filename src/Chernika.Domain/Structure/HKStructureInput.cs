using Chernika.Domain.Enums;

namespace Chernika.Domain.Structure;

/// <summary>Раздел карты во входных данных механизма.</summary>
public sealed record HKStructureSectionInput(
    Guid Id, HKCardSectionType SectionType, int SortOrder);

/// <summary>
/// Структурная позиция во входных данных механизма.
/// <para>
/// Раздел заполняется только у верхней позиции. Вложенные позиции наследуют
/// раздел своего корня, поэтому <paramref name="SectionId"/> у них не задаётся.
/// </para>
/// </summary>
public sealed record HKStructureNodeInput(
    Guid Id, Guid? ParentId, HKObjectLevel Level, string Name,
    int Quantity, int SortOrder, Guid? SectionId);

/// <summary>
/// Конечная строка сборочной единицы во входных данных механизма.
/// <para>
/// Раздел заполняется только у строки непосредственно под корнем карты.
/// </para>
/// </summary>
public sealed record HKStructureItemInput(
    Guid Id, Guid? NodeId, int Quantity, decimal Volume,
    string? UnitOfMeasure, int SortOrder, Guid? SectionId);

/// <summary>
/// Единое неизменяемое описание структуры.
/// <para>
/// Один и тот же контракт принимают и расчёты рабочего дерева, и расчёты
/// принятой редакции: формулы не дублируются.
/// </para>
/// </summary>
public sealed record HKStructureInput(
    IReadOnlyList<HKStructureSectionInput> Sections,
    IReadOnlyList<HKStructureNodeInput> Nodes,
    IReadOnlyList<HKStructureItemInput> Items);