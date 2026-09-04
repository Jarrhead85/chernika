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

    /// <summary>Null until the root HK was uniquely selected or explicitly selected.</summary>
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
/// Constructive composition resolved by preflight. Quantity carries the
/// multiplier of this composition row for the future D4 calculation:
/// for a Complex target it is ComplexCompositionItem.Quantity; for other
/// targets it is always 1 (Instance represents one concrete Изделие).
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

public sealed record IndividualCardPreflightHKSourceDto(
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
