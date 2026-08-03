using System.ComponentModel.DataAnnotations;

namespace Chernika.Domain.Entities;

public class MilitaryBranch
{
    public Guid Id { get; set; }

    [Required, StringLength(50)]
    public string Code { get; set; } = null!;

    [Required, StringLength(250)]
    public string Name { get; set; } = null!;

    [StringLength(1000)]
    public string? Description { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
