namespace Chernika.Domain.Entities;

public class Complex
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<ComplexComposition> ComplexCompositions { get; set; } = new List<ComplexComposition>();
}
