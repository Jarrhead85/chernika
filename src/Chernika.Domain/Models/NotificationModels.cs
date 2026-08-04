using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed record CreateNotificationCommand(
    NotificationType Type,
    string Title,
    string? Message = null,
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? WorkTaskId = null,
    string? NavigationUrl = null,
    Guid? BranchId = null,
    string? DeduplicationKey = null,
    DateTime? ExpiresAtUtc = null);

public sealed class NotificationQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    public bool UnreadOnly { get; init; }
    public NotificationType? Type { get; init; }
    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public int? Days { get; init; }
}

public sealed class NotificationDto
{
    public Guid Id { get; init; }
    public NotificationType Type { get; init; }

    public string Title { get; init; } = null!;
    public string? Message { get; init; }

    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public Guid? WorkTaskId { get; init; }
    public string? NavigationUrl { get; init; }

    public bool IsRead { get; init; }
    public DateTime? ReadAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
