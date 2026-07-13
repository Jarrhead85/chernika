namespace Chernika.Domain.Entities;

public class Branch
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }

    public ICollection<HKCard> HKCards { get; set; } = new List<HKCard>();
}
