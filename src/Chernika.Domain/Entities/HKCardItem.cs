namespace Chernika.Domain.Entities;

public class HKCardItem
{
    public Guid Id { get; set; }
    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;
    public Guid AssemblyUnitId { get; set; }
    public AssemblyUnit AssemblyUnit { get; set; } = null!;
    public int Quantity { get; set; } = 1;
    public decimal Volume { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? Periodicity { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }

    public ICollection<HKCardItemMaterial> Materials { get; set; } = new List<HKCardItemMaterial>();
    public ICollection<IndividualCardItem> IndividualCardItems { get; set; } = new List<IndividualCardItem>();
}
