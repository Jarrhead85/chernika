namespace Chernika.Domain;

public static class NormCalculation
{
    /// <summary>Округление нормы до граммов (разд. 3.4 саммари).</summary>
    public static decimal RoundToGrams(decimal value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero);
}
