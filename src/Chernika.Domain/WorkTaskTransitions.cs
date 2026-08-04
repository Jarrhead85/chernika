using Chernika.Domain.Enums;

namespace Chernika.Domain;

public static class WorkTaskTransitions
{
    public static bool IsTerminal(WorkTaskStatus status) =>
        status == WorkTaskStatus.Completed || status == WorkTaskStatus.Cancelled;

    public static bool CanModify(WorkTaskStatus status) => !IsTerminal(status);

    public static bool IsActive(WorkTaskStatus status) =>
        status == WorkTaskStatus.Open
        || status == WorkTaskStatus.InProgress
        || status == WorkTaskStatus.Overdue;

    public static bool CanStart(WorkTaskStatus status) =>
        status == WorkTaskStatus.Open || status == WorkTaskStatus.Overdue;
}
