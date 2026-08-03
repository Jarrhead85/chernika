namespace Chernika.Domain.Entities;

public class AssemblyUnit
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool IsDraft { get; set; }

    public ICollection<HKCardItem> HKCardItems { get; set; } = new List<HKCardItem>();
}
