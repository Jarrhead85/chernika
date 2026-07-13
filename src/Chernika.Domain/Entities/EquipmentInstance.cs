namespace Chernika.Domain.Entities;

public class EquipmentInstance
{
    public Guid Id { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string Index { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid EquipmentModelId { get; set; }
    public EquipmentModel EquipmentModel { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<IndividualCard> IndividualCards { get; set; } = new List<IndividualCard>();
}
