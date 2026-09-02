namespace Chernika.Domain.Entities;

public class Coefficient
{
    public Guid Id { get; set; }
    public Guid CoefficientTypeId { get; set; }
    public CoefficientType CoefficientType { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? ConditionDescription { get; set; }
    public string? NormativeBasis { get; set; }
    public decimal Value { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<IndividualCard> IndividualCards { get; set; } = new List<IndividualCard>();
}
