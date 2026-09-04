using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class IndividualCardCompositionSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    public IndividualCardObjectLevel SourceLevel { get; set; }

    // Scalar source references: history must survive source archive/revision,
    // so no FK is created to mutable composition rows.
    public Guid SourceCompositionId { get; set; }
    public string SourceCompositionVersion { get; set; } = string.Empty;
    public DateTime? SourceApprovedAt { get; set; }

    public Guid TargetObjectId { get; set; }
    public string TargetObjectCode { get; set; } = string.Empty;
    public string TargetObjectName { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; }

    public ICollection<IndividualCardAggregateSnapshot> Aggregates { get; set; }
        = new List<IndividualCardAggregateSnapshot>();
}
