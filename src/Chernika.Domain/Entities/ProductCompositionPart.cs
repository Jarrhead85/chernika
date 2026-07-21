namespace Chernika.Domain.Entities;

public class ProductCompositionPart
{
    public Guid Id { get; set; }
    public Guid ProductCompositionId { get; set; }
    public ProductComposition ProductComposition { get; set; } = null!;

    /// <summary>Наименование составной части (например, "Силовая установка").</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Порядок отображения внутри состава.</summary>
    public int SortOrder { get; set; }

    public ICollection<ProductCompositionAggregate> Aggregates { get; set; } = new List<ProductCompositionAggregate>();
}

