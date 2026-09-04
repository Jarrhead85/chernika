using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class IndividualCard
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int RevisionNumber { get; set; } = 1;

    public IndividualCardObjectLevel ObjectLevel { get; set; }
    public IndividualCardStatus Status { get; set; } = IndividualCardStatus.Draft;

    public Guid? ComplexId { get; set; }
    public Complex? Complex { get; set; }

    public Guid? EquipmentModelId { get; set; }
    public EquipmentModel? EquipmentModel { get; set; }

    public Guid? AggregateId { get; set; }
    public Aggregate? Aggregate { get; set; }

    public Guid? NodeId { get; set; }
    public Node? Node { get; set; }

    public Guid? EquipmentInstanceId { get; set; }
    public EquipmentInstance? EquipmentInstance { get; set; }

    public Guid BranchId { get; set; }
    public Branch Branch { get; set; } = null!;

    public Guid? SupersedesIndividualCardId { get; set; }
    public IndividualCard? SupersedesIndividualCard { get; set; }
    public ICollection<IndividualCard> SupersededBy { get; set; } = new List<IndividualCard>();

    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? FormedByUserId { get; set; }
    public DateTime? FormedAt { get; set; }

    public string? ArchivedByUserId { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public string? Notes { get; set; }

    // Legacy D0 fields preserved until a separate cleanup PR:
    // legacy model was EquipmentInstance-scoped with a single HKCard/Node/ProductComposition link.
    public Guid? HKCardId { get; set; }
    public HKCard? HKCard { get; set; }
    public Guid? ProductCompositionId { get; set; }
    public ProductComposition? ProductComposition { get; set; }
    public decimal TotalNorm { get; set; }

    public ICollection<IndividualCardCompositionSnapshot> CompositionSnapshots { get; set; }
        = new List<IndividualCardCompositionSnapshot>();
    public ICollection<IndividualCardHKSourceSnapshot> HKSourceSnapshots { get; set; }
        = new List<IndividualCardHKSourceSnapshot>();
    public ICollection<IndividualCardItem> Items { get; set; } = new List<IndividualCardItem>();
    public ICollection<IndividualCardCoefficientSnapshot> CoefficientSnapshots { get; set; }
        = new List<IndividualCardCoefficientSnapshot>();

    public ICollection<Coefficient> AppliedCoefficients { get; set; } = new List<Coefficient>();
}
