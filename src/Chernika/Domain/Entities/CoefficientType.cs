namespace Chernika.Domain.Entities;

public class CoefficientType
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;
    public int SortOrder { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Coefficient> Coefficients { get; set; } = new List<Coefficient>();
}
