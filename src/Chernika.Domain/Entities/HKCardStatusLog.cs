using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class HKCardStatusLog
{
    public Guid Id { get; set; }
    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;
    public HKCardStatus FromStatus { get; set; }
    public HKCardStatus ToStatus { get; set; }
    public Guid ChangedByUserId { get; set; }
    public string? Comment { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
