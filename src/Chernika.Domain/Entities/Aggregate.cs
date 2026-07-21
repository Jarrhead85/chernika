namespace Chernika.Domain.Entities;

public class Aggregate
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<AggregateComposition> AggregateCompositions { get; set; } = new List<AggregateComposition>();
    public ICollection<ProductCompositionAggregate> ProductCompositionAggregates { get; set; } = new List<ProductCompositionAggregate>();
}
