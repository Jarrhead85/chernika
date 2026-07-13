namespace Chernika.Domain.Entities;

public class ProductCompositionNode
{
    public Guid Id { get; set; }
    public Guid PartId { get; set; }
    public ProductCompositionPart Part { get; set; } = null!;
    public Guid NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public int Quantity { get; set; } = 1;
}
