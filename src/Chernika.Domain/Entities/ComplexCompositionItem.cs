namespace Chernika.Domain.Entities;

public class ComplexCompositionItem
{
    public Guid Id { get; set; }
    public Guid ComplexCompositionId { get; set; }
    public ComplexComposition ComplexComposition { get; set; } = null!;
    public Guid EquipmentModelId { get; set; }
    public EquipmentModel EquipmentModel { get; set; } = null!;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
}
