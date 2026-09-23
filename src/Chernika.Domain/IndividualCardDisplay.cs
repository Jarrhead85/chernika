using Chernika.Domain.Enums;

namespace Chernika.Domain;

/// <summary>
/// Отображаемые пользователю названия понятий индивидуальной карты.
/// EquipmentModel всегда отображается как «Изделие»; технический термин
/// «Модель техники» не должен появляться в пользовательских текстах.
/// </summary>
public static class IndividualCardDisplay
{
    private static readonly Dictionary<IndividualCardObjectLevel, string> ObjectLevelNames = new()
    {
        [IndividualCardObjectLevel.Complex] = "Комплекс",
        [IndividualCardObjectLevel.EquipmentModel] = "Изделие",
        [IndividualCardObjectLevel.Aggregate] = "Агрегат",
        [IndividualCardObjectLevel.Node] = "Узел",
        [IndividualCardObjectLevel.EquipmentInstance] = "Экземпляр техники",
    };

    private static readonly Dictionary<IndividualCardStatus, string> StatusNames = new()
    {
        [IndividualCardStatus.Draft] = "Черновик",
        [IndividualCardStatus.Formed] = "Сформирована",
        [IndividualCardStatus.Archived] = "Архив",
    };

    public static string ObjectLevel(IndividualCardObjectLevel level) =>
        ObjectLevelNames.TryGetValue(level, out var name) ? name : level.ToString();

    public static string Status(IndividualCardStatus status) =>
        StatusNames.TryGetValue(status, out var name) ? name : status.ToString();
}
