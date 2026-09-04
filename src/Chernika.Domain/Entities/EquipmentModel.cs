namespace Chernika.Domain.Entities;

public class EquipmentModel
{
    public Guid Id { get; set; }
    public string Index { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Brand { get; set; }
    public string? Modification { get; set; }
    public string? Description { get; set; }

    public Guid? EquipmentTypeId { get; set; }
    public EquipmentType? EquipmentType { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<ProductComposition> ProductCompositions { get; set; } = new List<ProductComposition>();
    public ICollection<EquipmentInstance> Instances { get; set; } = new List<EquipmentInstance>();
    public ICollection<ComplexCompositionItem> ComplexCompositionItems { get; set; } = new List<ComplexCompositionItem>();
    public ICollection<HKCard> HKCards { get; set; } = new List<HKCard>();
    public ICollection<IndividualCard> IndividualCards { get; set; } = new List<IndividualCard>();
}
