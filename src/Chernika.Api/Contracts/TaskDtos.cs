using Chernika.Domain.Entities;

namespace Chernika.Api.Contracts;

public record WorkTaskDto(
    Guid Id,
    string Title,
    string? Description,
    string? EntityType,
    string? EntityId,
    string AssigneeId,
    bool IsCompleted,
    DateTime? DueDate,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public record CreateWorkTaskRequest(
    string Title,
    string AssigneeId,
    string? Description,
    string? EntityType,
    string? EntityId,
    DateTime? DueDate);

public static class WorkTaskMapper
{
    public static WorkTaskDto ToDto(WorkTask t) => new(
        t.Id, t.Title, t.Description, t.EntityType, t.EntityId,
        t.AssigneeId, t.IsCompleted, t.DueDate, t.CreatedAt, t.CompletedAt);

    public static WorkTask FromCreate(CreateWorkTaskRequest r) => new()
    {
        Title = r.Title,
        AssigneeId = r.AssigneeId,
        Description = r.Description,
        EntityType = r.EntityType,
        EntityId = r.EntityId,
        DueDate = r.DueDate
    };
}
