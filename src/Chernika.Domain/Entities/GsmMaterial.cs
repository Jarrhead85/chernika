namespace Chernika.Domain.Entities;

public class GsmMaterial
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Gost { get; set; }
    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool IsDraft { get; set; }

    public ICollection<HKCardItemMaterial> HKCardItemMaterials { get; set; } = new List<HKCardItemMaterial>();
}
