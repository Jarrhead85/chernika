namespace Chernika.Domain.Entities;

public class IndividualCardItem
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    // Legacy D0 field preserved until a separate cleanup PR.
    public Guid? HKCardItemId { get; set; }
    public HKCardItem? HKCardItem { get; set; }

    // Scalar reference into the snapshot tree of the same IndividualCard.
    // Deliberately without an FK: node snapshots are cascade-deleted together
    // with the card and a restrictive FK could break cascade ordering.
    public Guid? NodeSnapshotId { get; set; }

    public string AssemblyUnitCode { get; set; } = string.Empty;
    public string AssemblyUnitName { get; set; } = string.Empty;
    public int AssemblyUnitQuantity { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public string? Periodicity { get; set; }
    public string? Notes { get; set; }

    public decimal SourceVolume { get; set; }
    public decimal BaseVolume { get; set; }
    public decimal CalculatedVolume { get; set; }
    public int SortOrder { get; set; }

    // Legacy D0 field preserved until a separate cleanup PR.
    public int Quantity { get; set; }

    public ICollection<IndividualCardItemMaterialSnapshot> MaterialSnapshots { get; set; }
        = new List<IndividualCardItemMaterialSnapshot>();
}
