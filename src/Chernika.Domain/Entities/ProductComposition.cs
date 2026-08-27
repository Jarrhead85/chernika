using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class ProductComposition
{
    public Guid Id { get; set; }
    public Guid EquipmentModelId { get; set; }
    public EquipmentModel EquipmentModel { get; set; } = null!;

    public ProductCompositionStatus Status { get; set; }
    public string Version { get; set; } = null!;
    public Guid? BranchId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public string? AuthorId { get; set; }
    public string? ApprovedByUserId { get; set; }

    public string? Comment { get; set; }

    /// <summary>True only for the current active approved composition. Not set from UI.</summary>
    public bool IsActive { get; set; }

    public Guid? SupersedesProductCompositionId { get; set; }
    public ProductComposition? SupersedesProductComposition { get; set; }
    public ICollection<ProductComposition> SupersededByCompositions { get; set; } = new List<ProductComposition>();

    public ICollection<ProductCompositionPart> Parts { get; set; } = new List<ProductCompositionPart>();

    /// <summary>Все агрегаты версии состава, включая агрегаты без группы (PartId = null).</summary>
    public ICollection<ProductCompositionAggregate> Aggregates { get; set; } = new List<ProductCompositionAggregate>();
}
