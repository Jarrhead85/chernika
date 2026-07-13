using Chernika.Domain.Entities;

namespace Chernika.Api.Contracts;

public record AuditLogDto(
    Guid Id,
    string EntityType,
    string EntityId,
    string Action,
    Guid UserId,
    string? Details,
    DateTime CreatedAt);

public static class AuditLogMapper
{
    public static AuditLogDto ToDto(AuditLog l) => new(
        l.Id, l.EntityType, l.EntityId, l.Action, l.UserId, l.Details, l.CreatedAt);
}
