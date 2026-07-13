namespace Chernika.Domain.Entities;

public class IndividualCardItem
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;
    public Guid HKCardItemId { get; set; }
    public HKCardItem HKCardItem { get; set; } = null!;
    public decimal BaseVolume { get; set; }
    public decimal CalculatedVolume { get; set; }
    public int Quantity { get; set; }
}
