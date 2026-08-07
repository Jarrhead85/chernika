namespace Chernika.Domain;

public sealed record AuditWriteRequest(
    string EntityType,
    string EntityId,
    string Action,
    Guid ActorUserId,
    string? EntityDisplayName = null,
    string? Details = null,
    AuditSource? Source = null);
