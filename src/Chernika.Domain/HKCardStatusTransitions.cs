using Chernika.Domain.Enums;

namespace Chernika.Domain;

public static class HKCardStatusTransitions
{
    private static readonly Dictionary<HKCardStatus, HashSet<HKCardStatus>> Allowed = new()
    {
        [HKCardStatus.Draft] = [HKCardStatus.OnReview, HKCardStatus.Deleted],
        [HKCardStatus.OnReview] = [HKCardStatus.Approved, HKCardStatus.RevisionRequired, HKCardStatus.Deleted],
        [HKCardStatus.RevisionRequired] = [HKCardStatus.OnReview, HKCardStatus.Deleted],
        [HKCardStatus.Approved] = [HKCardStatus.Archived, HKCardStatus.Deleted],
        [HKCardStatus.Archived] = [],
        [HKCardStatus.Deleted] = [],
    };

    public static bool IsAllowed(HKCardStatus from, HKCardStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static string GetErrorMessage(HKCardStatus from, HKCardStatus to) =>
        $"Переход из «{from}» в «{to}» не допускается.";

    public static bool CanDelete(HKCardStatus currentStatus, UserRole actorRole) =>
        currentStatus switch
        {
            HKCardStatus.Draft => actorRole is UserRole.Operator or UserRole.SystemAdmin,
            HKCardStatus.RevisionRequired => actorRole is UserRole.Operator or UserRole.SystemAdmin,
            HKCardStatus.OnReview => actorRole == UserRole.SystemAdmin,
            HKCardStatus.Archived => actorRole == UserRole.SystemAdmin,
            _ => false
        };
}
