using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed record IndividualCardPreflightRequest(
    IndividualCardObjectLevel ObjectLevel,
    Guid ObjectId,
    Guid? RootHKCardId = null);

public enum IndividualCardPreflightRootState
{
    Missing = 1,
    AutomaticallySelected = 2,
    SelectionRequired = 3,
    ExplicitlySelected = 4
}

public enum IndividualCardNormativeGapKind
{
    TargetNotFound = 1,
    MissingRootHKCard = 2,
    RootSelectionRequired = 3,
    MissingApprovedComposition = 4,
    MissingLinkedHKCard = 5,
    LinkedHKCardNotApproved = 6,
    LinkedHKCardWrongObject = 7,
    LinkedHKCardWrongLevel = 8,
    LinkedHKCardWrongBranch = 9,
    MissingConstructiveItem = 10,
    InconsistentNormativeChain = 11
}

public sealed class IndividualCardPreflightResult
{
    public IndividualCardObjectLevel ObjectLevel { get; init; }
    public Guid ObjectId { get; init; }
    public string ObjectCode { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public string ObjectDisplayType { get; init; } = string.Empty;

    /// <summary>Пусто, пока корневая ХК не выбрана однозначно или явно.</summary>
    public Guid? BranchId { get; init; }

    public IndividualCardPreflightRootState RootState { get; init; }
    public IReadOnlyList<IndividualCardHKCandidateDto> RootCandidates { get; init; } = [];
    public IndividualCardHKCandidateDto? SelectedRoot { get; init; }

    public IReadOnlyList<IndividualCardPreflightCompositionDto> Compositions { get; init; } = [];
    public IReadOnlyList<IndividualCardPreflightHKSourceDto> HKSources { get; init; } = [];
    public IReadOnlyList<IndividualCardNormativeGapDto> NormativeGaps { get; init; } = [];

    public bool IsComplete => SelectedRoot is not null && NormativeGaps.Count == 0;
}

public sealed record IndividualCardHKCandidateDto(
    Guid HKCardId,
    string Code,
    string Version,
    IndividualCardObjectLevel ObjectLevel,
    Guid ObjectId,
    string ObjectCode,
    string ObjectName,
    Guid BranchId,
    DateTime? ApprovedAt,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    int SortOrder);

/// <summary>
/// Конструктивный состав, определённый предварительной проверкой. Quantity несёт
/// множитель этой строки состава для будущего расчёта D4:
/// для комплекса это ComplexCompositionItem.Quantity; для остальных
/// целей всегда 1 (экземпляр представляет одно конкретное изделие).
/// </summary>
public sealed record IndividualCardPreflightCompositionDto(
    IndividualCardObjectLevel SourceLevel,
    Guid CompositionId,
    string CompositionVersion,
    DateTime? ApprovedAt,
    Guid TargetObjectId,
    string TargetObjectCode,
    string TargetObjectName,
    int Quantity,
    IReadOnlyList<IndividualCardPreflightAggregateDto> Aggregates);

public sealed record IndividualCardPreflightAggregateDto(
    Guid AggregateId,
    string Code,
    string Name,
    int Quantity,
    int SortOrder,
    Guid? AggregateCompositionId,
    string? AggregateCompositionVersion,
    IReadOnlyList<IndividualCardPreflightNodeDto> Nodes);

public sealed record IndividualCardPreflightNodeDto(
    Guid NodeId,
    string Code,
    string Name,
    int Quantity,
    int SortOrder);

/// <summary>
/// Разрешённый источник ХК в дереве предварительной проверки. PreflightOccurrenceId задаёт
/// ПОЗИЦИЮ в разрешённом дереве: один и тот же источник HKCardId может законно
/// встречаться в нескольких ветках (разные родители), образуя отдельные
/// вхождения. ParentPreflightOccurrenceId всегда ссылается на вхождение
/// в пределах того же дерева предварительной проверки.
/// </summary>
public sealed record IndividualCardPreflightHKSourceDto(
    Guid PreflightOccurrenceId,
    Guid? ParentPreflightOccurrenceId,
    Guid HKCardId,
    Guid? ParentHKCardId,
    IndividualCardObjectLevel ObjectLevel,
    Guid ObjectId,
    string ObjectCode,
    string ObjectName,
    string HKCardCode,
    string HKCardVersion,
    Guid BranchId,
    DateTime? ApprovedAt,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    int SortOrder,
    bool IsComplete);

public sealed record IndividualCardNormativeGapDto(
    IndividualCardNormativeGapKind Kind,
    IndividualCardObjectLevel RelatedLevel,
    Guid? RelatedObjectId,
    string RelatedObjectType,
    string? RelatedObjectCode,
    string RelatedObjectName,
    Guid? RelatedHKCardId,
    string Message,
    int SortOrder);
