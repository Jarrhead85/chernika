using System.Globalization;

namespace Chernika.Domain.Structure;

/// <summary>
/// Отображение итогов.
/// <para>
/// Округление выполняется только здесь, на границе показа: промежуточные
/// суммы не округляются. Масса показывается в килограммах с эквивалентом в
/// граммах, объём — в литрах. Масса и объём не складываются в одно число.
/// </para>
/// </summary>
public static class HKTotalFormatter
{
    /// <summary>
    /// Форматирует один итог. Неполный итог помечается явно, чтобы его нельзя
    /// было принять за достоверную полную сумму.
    /// </summary>
    public static string Format(HKMeasuredTotal total)
    {
        if (!total.IsComplete)
        {
            var reason = total.Diagnostics.Count > 0
                ? string.Join("; ", total.Diagnostics)
                : "часть строк имеет нераспознанную единицу измерения";

            return $"итог неполный ({reason})";
        }

        return total.Quantity switch
        {
            HKPhysicalQuantity.Mass => FormatMass(total.BaseValue),
            HKPhysicalQuantity.Liquid => FormatLiquid(total.BaseValue),
            _ => total.BaseValue.ToString("0.###", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>Формат массы: килограммы и эквивалент в граммах.</summary>
    private static string FormatMass(decimal grams)
    {
        var kilograms = grams / 1000m;
        return $"{Dec(kilograms)} кг ({Dec(grams)} г)";
    }

    private static string FormatLiquid(decimal liters) =>
        $"{Dec(liters)} л";

    /// <summary>
    /// Разделитель дробной части — запятая, как принято в документе.
    /// </summary>
    private static string Dec(decimal value)
    {
        var rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
    }
}