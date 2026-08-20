namespace Chernika.Domain.Entities;

public class ProductCompositionAggregate
{
    public Guid Id { get; set; }

    /// <summary>Родительский состав изделия (заполняется всегда; используется для уникальности агрегата во всей версии).</summary>
    public Guid ProductCompositionId { get; set; }
    public ProductComposition ProductComposition { get; set; } = null!;

    /// <summary>Опциональная логическая часть изделия. Если null — агрегат находится на верхнем уровне состава.</summary>
    public Guid? PartId { get; set; }
    public ProductCompositionPart? Part { get; set; }

    public Guid AggregateId { get; set; }
    public Aggregate Aggregate { get; set; } = null!;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
}
