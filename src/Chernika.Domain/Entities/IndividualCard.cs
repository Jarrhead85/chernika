namespace Chernika.Domain.Entities;

public class IndividualCard
{
    public Guid Id { get; set; }
    public Guid EquipmentInstanceId { get; set; }
    public EquipmentInstance EquipmentInstance { get; set; } = null!;
    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;
    public Guid NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public string Version { get; set; } = string.Empty;
    public decimal TotalNorm { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<IndividualCardItem> Items { get; set; } = new List<IndividualCardItem>();
    public ICollection<Coefficient> AppliedCoefficients { get; set; } = new List<Coefficient>();
}
