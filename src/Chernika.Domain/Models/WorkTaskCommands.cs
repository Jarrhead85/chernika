using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public enum WorkTaskDueFilter
{
    All = 0,
    DueToday = 1,
    DueThisWeek = 2,
    Overdue = 3
}

public sealed record CreateWorkTaskCommand(
    string Title,
    WorkTaskType Type,
    WorkTaskPriority Priority,
    string? Description = null,
    string? AssignedToUserId = null,
    string? AssignedRole = null,
    Guid? BranchId = null,
    string? EntityType = null,
    Guid? EntityId = null,
    string? EntityCodeSnapshot = null,
    string? EntityTitleSnapshot = null,
    DateTime? DueDateUtc = null,
    bool NotifyAssignee = true);

public sealed record CreateWorkflowTaskCommand(
    string Title,
    WorkTaskType Type,
    WorkTaskPriority Priority,
    string? Description = null,
    string? AssignedToUserId = null,
    Guid? BranchId = null,
    string? EntityType = null,
    Guid? EntityId = null,
    string? EntityCodeSnapshot = null,
    string? EntityTitleSnapshot = null,
    DateTime? DueDateUtc = null,
    bool NotifyAssignee = true);

public sealed record AssignWorkTaskCommand(
    Guid TaskId,
    string? AssignedToUserId,
    string? AssignedRole,
    string? Comment = null);

public sealed record CompleteWorkTaskCommand(
    Guid TaskId,
    string? CompletionComment = null);

public sealed record CancelWorkTaskCommand(
    Guid TaskId,
    string? Reason = null);

public sealed record CreateCompositionReviewGroupCommand(
    string EntityType,
    Guid EntityId,
    Guid BranchId,
    string Code,
    string Version,
    string Title,
    string? Description = null);

public sealed record CreateCompositionReadinessGroupCommand(
    string EntityType,
    Guid EntityId,
    Guid BranchId,
    string Code,
    string Name,
    string ProblemKey,
    string StatusLabel,
    string? Description = null);

public sealed record CreateCompositionAuthorTaskCommand(
    string EntityType,
    Guid EntityId,
    Guid? BranchId,
    string AuthorId,
    string Code,
    string Version,
    string Title,
    string? Description = null);

public sealed record CompleteWorkTaskGroupCommand(
    Guid TaskId,
    string ActorUserId,
    string? Comment = null);
