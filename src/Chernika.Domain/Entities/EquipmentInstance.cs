namespace Chernika.Domain.Entities;

public class EquipmentInstance
{
    public Guid Id { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string Index { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid EquipmentModelId { get; set; }
    public EquipmentModel EquipmentModel { get; set; } = null!;

    /// <summary>Вид техники (классификатор), необязателен.</summary>
    public Guid? EquipmentTypeId { get; set; }
    public EquipmentType? EquipmentType { get; set; }

    /// <summary>Марка (перенесена из изделия в экземпляр).</summary>
    public string? Brand { get; set; }

    /// <summary>Модификация (перенесена из изделия в экземпляр).</summary>
    public string? Modification { get; set; }

    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<IndividualCard> IndividualCards { get; set; } = new List<IndividualCard>();
}
