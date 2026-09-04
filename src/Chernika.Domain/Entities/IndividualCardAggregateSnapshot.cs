namespace Chernika.Domain.Entities;

public class IndividualCardAggregateSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardCompositionSnapshotId { get; set; }
    public IndividualCardCompositionSnapshot CompositionSnapshot { get; set; } = null!;

    // Scalar source reference, kept without FK per snapshot history rules.
    public Guid AggregateId { get; set; }
    public string AggregateCode { get; set; } = string.Empty;
    public string AggregateName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }

    public ICollection<IndividualCardNodeSnapshot> Nodes { get; set; }
        = new List<IndividualCardNodeSnapshot>();
}
