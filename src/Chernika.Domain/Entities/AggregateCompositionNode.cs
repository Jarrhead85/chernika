namespace Chernika.Domain.Entities;

public class AggregateCompositionNode
{
    public Guid Id { get; set; }
    public Guid AggregateCompositionId { get; set; }
    public AggregateComposition AggregateComposition { get; set; } = null!;
    public Guid NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
}
