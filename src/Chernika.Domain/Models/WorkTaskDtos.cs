using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed class WorkTaskDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = null!;
    public string? Description { get; init; }

    public WorkTaskType Type { get; init; }
    public WorkTaskStatus Status { get; init; }
    public WorkTaskPriority Priority { get; init; }

    public string CreatedByUserId { get; init; } = null!;
    public string? CreatedByUserName { get; init; }
    public string? AssignedToUserId { get; init; }
    public string? AssignedToUserName { get; init; }
    public string? AssignedRole { get; init; }

    public Guid? BranchId { get; init; }

    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? EntityCodeSnapshot { get; init; }
    public string? EntityTitleSnapshot { get; init; }

    public DateTime CreatedAtUtc { get; init; }
    public DateTime? DueDateUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? CompletedByUserId { get; init; }
    public string? CompletionComment { get; init; }

    public bool IsOverdue { get; init; }
}

public sealed class WorkTaskListItemDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = null!;
    public string? Description { get; init; }

    public WorkTaskType Type { get; init; }
    public WorkTaskStatus Status { get; init; }
    public WorkTaskPriority Priority { get; init; }

    public string? AssignedToUserId { get; init; }
    public string? AssignedToUserName { get; init; }
    public string? AssignedRole { get; init; }

    public Guid? BranchId { get; init; }

    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? EntityCodeSnapshot { get; init; }
    public string? EntityTitleSnapshot { get; init; }

    public DateTime CreatedAtUtc { get; init; }
    public DateTime? DueDateUtc { get; init; }
    public bool IsOverdue { get; init; }
}
