namespace Chernika.Domain.Entities;

public class HKCardComponent
{
    public Guid Id { get; set; }
    public Guid ParentHKCardId { get; set; }
    public HKCard ParentHKCard { get; set; } = null!;
    public Guid ChildHKCardId { get; set; }
    public HKCard ChildHKCard { get; set; } = null!;
    public int SortOrder { get; set; }
    public DateTime AddedAt { get; set; }
    public string AddedByUserId { get; set; } = string.Empty;

    public string ChildCode { get; set; } = string.Empty;
    public string ChildVersion { get; set; } = string.Empty;
    public DateTime? ChildApprovedAt { get; set; }
}