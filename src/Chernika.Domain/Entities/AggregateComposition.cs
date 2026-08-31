using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public partial class AggregateComposition
{
    public Guid Id { get; set; }
    public Guid AggregateId { get; set; }
    public Aggregate Aggregate { get; set; } = null!;

    public string Version { get; set; } = null!;
    public ProductCompositionStatus Status { get; set; }
    public Guid? BranchId { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }

    public string? AuthorId { get; set; }
    public string? ApprovedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? Comment { get; set; }

    public bool IsActive { get; set; }

    public Guid? SupersedesAggregateCompositionId { get; set; }
    public AggregateComposition? SupersedesAggregateComposition { get; set; }
    public ICollection<AggregateComposition> SupersededByCompositions { get; set; } = new List<AggregateComposition>();

    public ICollection<AggregateCompositionNode> Nodes { get; set; } = new List<AggregateCompositionNode>();
}
