using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed record CreateIndividualCardDraftRequest(
    IndividualCardObjectLevel ObjectLevel,
    Guid ObjectId,
    Guid? RootHKCardId = null,
    string? Notes = null);

public sealed record RefreshIndividualCardDraftSourcesRequest(
    Guid IndividualCardId,
    Guid? RootHKCardId = null);

public sealed class IndividualCardDraftDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public int RevisionNumber { get; init; }
    public IndividualCardObjectLevel ObjectLevel { get; init; }
    public string ObjectLevelDisplay { get; init; } = string.Empty;
    public Guid ObjectId { get; init; }
    public string ObjectCode { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public Guid BranchId { get; init; }
    public IndividualCardStatus Status { get; init; }
    public string? Notes { get; init; }
    public string CreatedByUserId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    public IReadOnlyList<IndividualCardCompositionSnapshotDto> Compositions { get; init; } = [];
    public IReadOnlyList<IndividualCardHKSourceSnapshotDto> HKSources { get; init; } = [];
    public IReadOnlyList<IndividualCardNormativeGapSnapshotDto> NormativeGaps { get; init; } = [];

    public bool HasNormativeGaps => NormativeGaps.Count > 0;
}

public sealed class IndividualCardCompositionSnapshotDto
{
    public Guid Id { get; init; }
    public IndividualCardObjectLevel SourceLevel { get; init; }
    public Guid SourceCompositionId { get; init; }
    public string SourceCompositionVersion { get; init; } = string.Empty;
    public DateTime? SourceApprovedAt { get; init; }
    public Guid TargetObjectId { get; init; }
    public string TargetObjectCode { get; init; } = string.Empty;
    public string TargetObjectName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public DateTime CapturedAt { get; init; }
    public IReadOnlyList<IndividualCardAggregateSnapshotDto> Aggregates { get; init; } = [];
}

public sealed class IndividualCardAggregateSnapshotDto
{
    public Guid Id { get; init; }
    public Guid AggregateId { get; init; }
    public string AggregateCode { get; init; } = string.Empty;
    public string AggregateName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public int SortOrder { get; init; }
    public IReadOnlyList<IndividualCardNodeSnapshotDto> Nodes { get; init; } = [];
}

public sealed class IndividualCardNodeSnapshotDto
{
    public Guid Id { get; init; }
    public Guid NodeId { get; init; }
    public string NodeCode { get; init; } = string.Empty;
    public string NodeName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public int SortOrder { get; init; }
}

public sealed class IndividualCardHKSourceSnapshotDto
{
    public Guid Id { get; init; }
    public Guid? ParentHKSourceSnapshotId { get; init; }
    public Guid SourceHKCardId { get; init; }
    public IndividualCardObjectLevel ObjectLevel { get; init; }
    public Guid SourceObjectId { get; init; }
    public string SourceObjectCode { get; init; } = string.Empty;
    public string SourceObjectName { get; init; } = string.Empty;
    public string HKCardCode { get; init; } = string.Empty;
    public string HKCardVersion { get; init; } = string.Empty;
    public Guid BranchId { get; init; }
    public DateTime? HKCardApprovedAt { get; init; }
    public DateTime? HKCardEffectiveDate { get; init; }
    public DateTime? HKCardExpirationDate { get; init; }
    public int SortOrder { get; init; }
    public DateTime CapturedAt { get; init; }
    public bool IsComplete { get; init; }
}

public sealed class IndividualCardNormativeGapSnapshotDto
{
    public Guid Id { get; init; }
    public IndividualCardNormativeGapKind Kind { get; init; }
    public IndividualCardObjectLevel RelatedLevel { get; init; }
    public Guid? RelatedObjectId { get; init; }
    public string RelatedObjectType { get; init; } = string.Empty;
    public string? RelatedObjectCode { get; init; }
    public string RelatedObjectName { get; init; } = string.Empty;
    public Guid? RelatedHKCardId { get; init; }
    public string Message { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public DateTime CapturedAt { get; init; }
}
