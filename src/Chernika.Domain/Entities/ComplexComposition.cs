using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class ComplexComposition
{
    public Guid Id { get; set; }
    public Guid ComplexId { get; set; }
    public Complex Complex { get; set; } = null!;

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

    public Guid? SupersedesComplexCompositionId { get; set; }
    public ComplexComposition? SupersedesComplexComposition { get; set; }
    public ICollection<ComplexComposition> SupersededByCompositions { get; set; } = new List<ComplexComposition>();

    public ICollection<ComplexCompositionItem> Items { get; set; } = new List<ComplexCompositionItem>();
}
