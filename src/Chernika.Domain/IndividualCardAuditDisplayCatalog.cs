using System.Globalization;
using System.Text.RegularExpressions;

namespace Chernika.Domain;

/// <summary>
/// Отображение технических Details журнала событий ИК в безопасный
/// пользовательский русский текст. Сырые ключи (ObjectId, BranchId,
/// Snapshot/Preflight/Occurrence), значения GUID и длинные технические
/// идентификаторы пользователя не увидит.
/// </summary>
public static class IndividualCardAuditDisplayCatalog
{
    private static readonly (string Key, string Label)[] Known =
    [
        ("TotalCoefficient=", "Общий коэффициент: "),
        ("ObjectLevel=", "Уровень объекта: "),
        ("CoefficientCount=", "Коэффициентов: "),
        ("CalculationItemCount=", "Расчётных строк: "),
        ("PrimaryMaterialSnapshotCount=", "Основных марок ГСМ: "),
        ("CalculationProblemCount=", "Нормативных замечаний: "),
        ("CompositionCount=", "Версия состава: "),
        ("NormativeGapCount=", "Нормативных замечаний: "),
        ("HKSourceCount=", "Источников ХК: "),
    ];

    private static readonly Dictionary<string, string> ObjectLevelNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Complex"] = "Комплекс",
            ["EquipmentModel"] = "Изделие",
            ["Aggregate"] = "Агрегат",
            ["Node"] = "Узел",
            ["EquipmentInstance"] = "Экземпляр изделия",
        };

    private static readonly Regex GuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    /// <summary>
    /// Возвращает отформатированный русский текст Details или null, если
    /// Details не содержит распознанных технологических записей. Значения
    /// с GUID/длинными техническими токенами не отображаются.
    /// </summary>
    public static string? FormatDetails(string? details)
    {
        if (details is not { Length: > 0 } source)
            return null;

        // Проверка на сырые GUID в свободном тексте — скрываем целиком.
        if (GuidRegex.IsMatch(source))
            return null;

        var hasKeys = Known.Any((known) => details.Contains(known.Key, StringComparison.OrdinalIgnoreCase));
        if (!hasKeys)
            return source; // уже диалоговый русский текст — показываем как есть.

        var entries = new List<string>();

        foreach (var (key, label) in Known)
        {
            if (!TryGetValue(details, key, out var rawValue))
                continue;

            var formatted = FormatValue(key, label, rawValue);
            if (string.IsNullOrWhiteSpace(formatted))
                continue;

            entries.Add(formatted);
        }

        return entries.Count is 0
            ? null
            : string.Join(" · ", entries);
    }

    private static bool TryGetValue(string details, string key, out string value)
    {
        value = string.Empty;
        var index = details.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;

        var valueStart = index + key.Length;
        var semicolon = details.IndexOf(';', valueStart);
        var valueEnd = semicolon >= valueStart ? semicolon : details.Length;
        value = details[valueStart..valueEnd].Trim();
        return value.Length > 0;
    }

    private static readonly string[] IntegerKeys =
    [
        "CoefficientCount=",
        "CalculationItemCount=",
        "PrimaryMaterialSnapshotCount=",
        "CalculationProblemCount=",
        "CompositionCount=",
        "NormativeGapCount=",
        "HKSourceCount=",
    ];

    private static string FormatValue(string key, string label, string rawValue)
    {
        if (key is "ObjectLevel=")
        {
            if (ObjectLevelNames.TryGetValue(rawValue, out var levelName))
                return "Уровень объекта: " + levelName;
            return "Уровень объекта: " + rawValue;
        }

        if (IsTechnicalValue(rawValue))
            return string.Empty;

        if (key is "CompositionCount=" && rawValue is "0")
            return "Версия состава: отсутствует";
        if (key is "HKSourceCount=" && rawValue is "0")
            return "Источников ХК: нет";
        if (key is "NormativeGapCount=" && rawValue is "0")
            return "Нормативных замечаний: нет";

        if (key is "TotalCoefficient=")
        {
            if (decimal.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var coefficient))
                return label + decimalValue(coefficient);
            return label + rawValue;
        }

        // Все остальные известные ключи — целочисленные счётчики.
        if (IntegerKeys.Contains(key, StringComparer.Ordinal) &&
            int.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var count))
            return label + count.ToString(CultureInfo.InvariantCulture);

        return label + rawValue;
    }

    private static string decimalValue(decimal value) => value.ToString("F2", CultureInfo.GetCultureInfo("ru-RU"));

    private static bool IsTechnicalValue(string value) =>
        value.Contains('-') && value.Length >= 16;

    private static string ValueDisplay(string key, string rawValue) =>
        rawValue;
}
