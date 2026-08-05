using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed class WorkTaskQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    public string? Text { get; init; }
    public WorkTaskStatus? Status { get; init; }
    public bool ActiveOnly { get; init; }
    public WorkTaskType? Type { get; init; }
    public WorkTaskPriority? Priority { get; init; }
    public WorkTaskDueFilter DueFilter { get; init; } = WorkTaskDueFilter.All;

    public int? CompletedWithinDays { get; init; }

    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public Guid? BranchId { get; init; }

    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }
}
