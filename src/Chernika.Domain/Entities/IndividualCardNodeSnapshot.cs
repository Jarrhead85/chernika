namespace Chernika.Domain.Entities;

public class IndividualCardNodeSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardAggregateSnapshotId { get; set; }
    public IndividualCardAggregateSnapshot AggregateSnapshot { get; set; } = null!;

    // Scalar source reference, kept without FK per snapshot history rules.
    public Guid NodeId { get; set; }
    public string NodeCode { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
}
