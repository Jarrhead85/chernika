namespace Chernika.Domain.Entities;

public class EquipmentType
{
    public Guid Id { get; set; }

    public string? TypeGroup { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
