namespace Chernika.Api.Contracts;

public record CreateWorkTaskRequest(
    string Title,
    string? AssignedToUserId = null,
    string? AssignedRole = null,
    string? Description = null,
    string? EntityType = null,
    Guid? EntityId = null,
    DateTime? DueDateUtc = null,
    int Type = 1,
    int Priority = 2);
