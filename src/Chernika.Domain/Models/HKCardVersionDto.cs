using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public class HKCardVersionDto
{
    public Guid Id { get; init; }
    public string Version { get; init; } = "";
    public HKCardStatus Status { get; init; }
    public DateTime? ApprovedDate { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsCurrent { get; init; }
    public Guid? SupersedesHKCardId { get; init; }
}
