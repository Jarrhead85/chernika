namespace Chernika.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
