using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class Notification
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = null!;
    public Guid? BranchId { get; set; }

    public NotificationType Type { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;

    public string Title { get; set; } = null!;
    public string? Message { get; set; }

    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? WorkTaskId { get; set; }
    public string? NavigationUrl { get; set; }

    public string? DeduplicationKey { get; set; }

    public bool IsRead { get; set; }
    public DateTime? ReadAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}
