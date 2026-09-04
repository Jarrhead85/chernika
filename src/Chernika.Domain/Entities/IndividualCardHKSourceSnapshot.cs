using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class IndividualCardHKSourceSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    public Guid? ParentHKSourceSnapshotId { get; set; }
    public IndividualCardHKSourceSnapshot? Parent { get; set; }
    public ICollection<IndividualCardHKSourceSnapshot> Children { get; set; }
        = new List<IndividualCardHKSourceSnapshot>();

    // Scalar source reference: the HKCard may later be archived/renamed;
    // display fields below are immutable copies captured at snapshot time.
    public Guid SourceHKCardId { get; set; }

    public IndividualCardObjectLevel ObjectLevel { get; set; }
    public Guid SourceObjectId { get; set; }
    public string SourceObjectCode { get; set; } = string.Empty;
    public string SourceObjectName { get; set; } = string.Empty;

    public string HKCardCode { get; set; } = string.Empty;
    public string HKCardVersion { get; set; } = string.Empty;
    public DateTime? HKCardApprovedAt { get; set; }
    public DateTime? HKCardEffectiveDate { get; set; }
    public DateTime? HKCardExpirationDate { get; set; }

    public int SortOrder { get; set; }
    public DateTime CapturedAt { get; set; }
}
