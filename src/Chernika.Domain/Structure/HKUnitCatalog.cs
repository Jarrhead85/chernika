namespace Chernika.Domain.Structure;

/// <summary>Физическая величина. Масса и объём не складываются в одно число.</summary>
public enum HKPhysicalQuantity
{
    /// <summary>Масса. Базовая единица расчёта — граммы.</summary>
    Mass,

    /// <summary>Объём жидкости. Базовая единица расчёта — литры.</summary>
    Liquid
}

/// <summary>
/// Распознанная единица измерения.
/// <para>
/// Механизм расширяемый, но не превращается в универсальную систему всех
/// единиц: неизвестное значение не угадывается и не приравнивается к нулю.
/// </para>
/// </summary>
public sealed record HKUnitInfo(
    string Unit,
    HKPhysicalQuantity Quantity,
    decimal ToBaseFactor,
    string BaseSuffix)
{
    /// <summary>Приводит количество к базовой единице величины.</summary>
    public decimal ToBase(decimal value) => value * ToBaseFactor;
}

/// <summary>Каталог поддерживаемых единиц измерения.</summary>
public static class HKUnitCatalog
{
    private static readonly HKUnitInfo[] Catalog =
    {
        new("кг", HKPhysicalQuantity.Mass, 1000m, "г"),
        new("г", HKPhysicalQuantity.Mass, 1m, "г"),
        new("л", HKPhysicalQuantity.Liquid, 1m, "л"),
    };

    /// <summary>
    /// Пытается распознать единицу. Сравнение регистронезависимое, окружающие
    /// пробелы и точки отбрасываются.
    /// </summary>
    public static bool TryResolve(string? unit, out HKUnitInfo info)
    {
        var key = Normalize(unit);

        foreach (var candidate in Catalog)
        {
            if (string.Equals(Normalize(candidate.Unit), key, StringComparison.Ordinal))
            {
                info = candidate;
                return true;
            }
        }

        info = default!;
        return false;
    }

    /// <summary>Единица по имени или null, если она не поддерживается.</summary>
    public static HKUnitInfo? ResolveOrNull(string? unit) =>
        TryResolve(unit, out var info) ? info : null;

    private static string Normalize(string? unit) =>
        (unit ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
}