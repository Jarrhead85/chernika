using Chernika.Domain.Enums;

namespace Chernika.Domain;

public static class IndividualCardStatusTransitions
{
    private static readonly Dictionary<IndividualCardStatus, HashSet<IndividualCardStatus>> Allowed = new()
    {
        [IndividualCardStatus.Draft] = [IndividualCardStatus.Formed],
        [IndividualCardStatus.Formed] = [IndividualCardStatus.Archived],
        [IndividualCardStatus.Archived] = [],
    };

    public static bool IsAllowed(IndividualCardStatus from, IndividualCardStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static string GetErrorMessage(IndividualCardStatus from, IndividualCardStatus to) =>
        $"Переход ИК из статуса «{IndividualCardDisplay.Status(from)}» в «{IndividualCardDisplay.Status(to)}» не разрешён.";
}
