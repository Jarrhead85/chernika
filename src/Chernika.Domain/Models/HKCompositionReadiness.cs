using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public interface ICompositionEntity
{
    Guid ObjectId { get; }
    Guid? BranchId { get; }
    ProductCompositionStatus Status { get; }
    DateTime UpdatedAt { get; }
    Guid Id { get; }
}

public sealed record ReadinessChild(Guid ChildId, string Code, string Name);

public sealed record HKCardSnapshot(
    Guid Id,
    string? Version,
    HKCardStatus Status,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate);

public enum HKCompositionReadinessState
{
    NotApplicable = 0,
    Ready = 1,
    RequiresAttention = 2,
    NoComposition = 3,
    NoActiveComposition = 4
}

public sealed record HKReadinessContext(
    Guid HKCardId,
    HKObjectLevel ObjectLevel,
    Guid? ObjectId,
    Guid BranchId);

public sealed class HKCompositionReadinessSummary
{
    public Guid HKCardId { get; init; }
    public HKCompositionReadinessState State { get; init; }
    public int IssueCount { get; init; }
    public Guid? CompositionId { get; init; }
    public Guid? NavigationCompositionId { get; init; }
}

public sealed class HKCompositionReadinessDetails
{
    public Guid HKCardId { get; init; }
    public HKCompositionReadinessState State { get; init; }
    public int IssueCount { get; init; }
    public Guid? CompositionId { get; init; }
    public Guid? NavigationCompositionId { get; init; }
    public IReadOnlyList<ReadinessRow> Issues { get; init; } = Array.Empty<ReadinessRow>();
}
