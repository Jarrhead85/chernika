namespace Chernika.Domain.Entities;

public class ProductCompositionAggregate
{
    public Guid Id { get; set; }
    public Guid PartId { get; set; }
    public ProductCompositionPart Part { get; set; } = null!;
    public Guid AggregateId { get; set; }
    public Aggregate Aggregate { get; set; } = null!;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
}
