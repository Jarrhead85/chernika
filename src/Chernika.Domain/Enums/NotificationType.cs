namespace Chernika.Domain.Enums;

public enum NotificationType
{
    Information = 1,
    TaskAssigned = 2,
    TaskCompleted = 3,
    HKSubmittedForReview = 4,
    HKReturnedForRevision = 5,
    HKApproved = 6,
    HKExpiring = 7,
    HKExpired = 8,
    ReferenceProposalPending = 9,
    System = 10
}
