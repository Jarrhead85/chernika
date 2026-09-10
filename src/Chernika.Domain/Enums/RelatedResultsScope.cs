namespace Chernika.Domain.Enums;

/// <summary>
/// Область выдачи расширенного поиска для выбранного типа результата:
/// во всех связанных данных / только справочник / только ХК / только ИК.
/// </summary>
public enum RelatedResultsScope
{
    All = 0,
    ReferenceOnly = 1,
    HKOnly = 2,
    ICOnly = 3,
}
