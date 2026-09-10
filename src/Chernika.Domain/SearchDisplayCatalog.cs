namespace Chernika.Domain;

/// <summary>
/// Централизованные русские отображаемые названия для результатов
/// расширенного поиска (теги, статусы, имеют только русские значения).
/// </summary>
public static class SearchDisplayCatalog
{
    public static string EntityTypeDisplay(string entityType) => entityType switch
    {
        "HKCard" => "Химмотологическая карта",
        "IndividualCard" => "Индивидуальная карта",
        "Complex" => "Комплекс",
        "EquipmentModel" => "Изделие",
        "Aggregate" => "Агрегат",
        "Node" => "Узел",
        "AssemblyUnit" => "Сборочная единица",
        "EquipmentInstance" => "Экземпляр изделия",
        "GsmMaterial" => "Марка ГСМ",
        "Coefficient" => "Коэффициент",
        "WorkTask" => "Задача",
        _ => entityType,
    };

    /// <summary>Заголовки-теги результатов (в верхнем регистре для карточек поиска).</summary>
    public static string EntityTypeTag(string entityType) => EntityTypeDisplay(entityType).ToUpperInvariant();

    public static string HKStatus(string statusKey) => statusKey switch
    {
        nameof(Enums.HKCardStatus.Draft) => "Черновик",
        nameof(Enums.HKCardStatus.OnReview) => "На согласовании",
        nameof(Enums.HKCardStatus.RevisionRequired) => "На доработке",
        nameof(Enums.HKCardStatus.Approved) => "Утверждена",
        nameof(Enums.HKCardStatus.Archived) => "Архивирована",
        nameof(Enums.HKCardStatus.Deleted) => "Закрыта",
        _ => statusKey,
    };

    public static string IndividualCardStatus(string statusKey) => statusKey switch
    {
        nameof(Enums.IndividualCardStatus.Draft) => "Черновик",
        nameof(Enums.IndividualCardStatus.Formed) => "Сформирована",
        nameof(Enums.IndividualCardStatus.Archived) => "Архивирована",
        _ => statusKey,
    };

    public static string WorkTaskStatus(string statusKey) => statusKey switch
    {
        nameof(Enums.WorkTaskStatus.Open) => "Открыта",
        nameof(Enums.WorkTaskStatus.InProgress) => "В работе",
        nameof(Enums.WorkTaskStatus.Completed) => "Завершена",
        nameof(Enums.WorkTaskStatus.Cancelled) => "Отменена",
        nameof(Enums.WorkTaskStatus.Overdue) => "Просрочена",
        _ => statusKey,
    };
}
