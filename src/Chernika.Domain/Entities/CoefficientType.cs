using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class CoefficientType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CoefficientGroup Group { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }

    public ICollection<Coefficient> Coefficients { get; set; } = new List<Coefficient>();
}
