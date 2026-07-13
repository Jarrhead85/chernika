namespace Chernika.Domain.Entities;

public class Node
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<HKCard> HKCards { get; set; } = new List<HKCard>();
    public ICollection<ProductCompositionNode> ProductCompositionNodes { get; set; } = new List<ProductCompositionNode>();
}
