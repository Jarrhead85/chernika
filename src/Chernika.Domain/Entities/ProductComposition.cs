namespace Chernika.Domain.Entities;

public class ProductComposition
{
    public Guid Id { get; set; }
    public Guid EquipmentModelId { get; set; }
    public EquipmentModel EquipmentModel { get; set; } = null!;
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }

    public ICollection<ProductCompositionPart> Parts { get; set; } = new List<ProductCompositionPart>();
}
