namespace Chernika.Domain;

public enum AuditSeverity { Neutral, Success, Warning, Danger }

public sealed record AuditActionDisplay(
    string Title,
    AuditSeverity Severity);

public static class AuditDisplayCatalog
{
    private static readonly Dictionary<string, AuditActionDisplay> Actions = new()
    {
        ["Create"] = new("Создание", AuditSeverity.Success),
        ["Created"] = new("Создание", AuditSeverity.Success),
        ["Update"] = new("Изменение", AuditSeverity.Neutral),
        ["Updated"] = new("Изменение", AuditSeverity.Neutral),
        ["Delete"] = new("Удаление", AuditSeverity.Danger),
        ["Deleted"] = new("Удаление", AuditSeverity.Danger),
        ["CreateDraft"] = new("Создание черновика", AuditSeverity.Success),
        ["UpdateDraft"] = new("Изменение черновика", AuditSeverity.Neutral),
        ["DeleteDraft"] = new("Удаление черновика", AuditSeverity.Danger),
        ["Approve"] = new("Утверждение", AuditSeverity.Success),
        ["Archive"] = new("Архивирование", AuditSeverity.Neutral),
        ["RoleChanged"] = new("Изменение роли пользователя", AuditSeverity.Warning),
        ["Blocked"] = new("Блокировка пользователя", AuditSeverity.Danger),
        ["Unblocked"] = new("Разблокировка пользователя", AuditSeverity.Success),
        ["Restored"] = new("Восстановление пользователя", AuditSeverity.Success),
        ["Restore"] = new("Восстановление", AuditSeverity.Success),
        ["OverrideGranted"] = new("Индивидуальное полномочие разрешено", AuditSeverity.Warning),
        ["OverrideDenied"] = new("Индивидуальное полномочие запрещено", AuditSeverity.Danger),
        ["OverrideRemoved"] = new("Индивидуальное решение отменено", AuditSeverity.Neutral),
        ["OverrideRevoked"] = new("Индивидуальное решение отменено", AuditSeverity.Neutral),
        ["UpdateQuantity"] = new("Изменение количества", AuditSeverity.Neutral),
        ["RoleCreated"] = new("Создание базовой роли", AuditSeverity.Success),
        ["ViewerMigrated"] = new("Перенос из устаревшей роли «Наблюдатель» в «Гость»", AuditSeverity.Warning),
        ["Repaired"] = new("Автоматическое исправление конфигурации доступа", AuditSeverity.Warning),
        ["Task.Created"] = new("Задача создана", AuditSeverity.Success),
        ["Task.Assigned"] = new("Задача назначена исполнителю", AuditSeverity.Warning),
        ["Task.Started"] = new("Задача взята в работу", AuditSeverity.Neutral),
        ["Task.Completed"] = new("Задача выполнена", AuditSeverity.Success),
        ["Task.Cancelled"] = new("Задача отменена", AuditSeverity.Danger),
        ["Task.Overdue"] = new("Задача просрочена", AuditSeverity.Warning),
        ["Task.Deleted"] = new("Задача удалена", AuditSeverity.Danger),
        ["Notification.Created"] = new("Уведомление создано", AuditSeverity.Neutral),
        ["Notification.Read"] = new("Уведомление прочитано", AuditSeverity.Neutral),
        ["Notification.ReadAll"] = new("Все уведомления прочитаны", AuditSeverity.Neutral),
        ["ReferenceProposal.NoNormAdmin"] = new("Нет NormAdmin в филиале", AuditSeverity.Warning),
        ["Workflow.NoAssignee"] = new("Нет исполнителя в филиале", AuditSeverity.Warning),
        ["HK.ExpirationWarningCreated"] = new("Предупреждение об истечении срока ХК", AuditSeverity.Warning),
        ["HK.ExpiredArchived"] = new("ХК автоматически архивирована по истечении срока", AuditSeverity.Warning),
        ["HKCard.NewVersionCreated"] = new("Создана новая версия ХК", AuditSeverity.Success),
        ["ComplexComposition.NewVersionCreated"] = new("Создана новая версия состава комплекса", AuditSeverity.Success),
        ["ProductComposition.NewVersionCreated"] = new("Создана новая версия состава изделия", AuditSeverity.Success),
        ["AggregateComposition.NewVersionCreated"] = new("Создана новая версия состава агрегата", AuditSeverity.Success),
    };

    private static readonly Dictionary<string, string> EntityTypes = new()
    {
        ["HKCard"] = "Химмотологическая карта",
        ["Complex"] = "Комплекс",
        ["EquipmentModel"] = "Изделие",
        ["Aggregate"] = "Узел",
        ["Node"] = "Узел",
        ["AssemblyUnit"] = "Сборочная единица",
        ["EquipmentInstance"] = "Экземпляр техники",
        ["IndividualCard"] = "Индивидуальная карта",
        ["GsmMaterial"] = "Марка ГСМ",
        ["Branch"] = "Филиал",
        ["Coefficient"] = "Коэффициент",
        ["CoefficientType"] = "Тип коэффициента",
        ["WorkTask"] = "Задача",
        ["User"] = "Пользователь",
        ["UserPermissionOverride"] = "Индивидуальное полномочие пользователя",
        ["RolePermissionTemplate"] = "Шаблон полномочий роли",
        ["SecurityRepair"] = "Конфигурация безопасности",
        ["ProductComposition"] = "Состав изделия",
        ["ProductCompositionPart"] = "Строка состава изделия",
        ["ProductCompositionAggregate"] = "Узел в составе изделия",
        ["AggregateComposition"] = "Состав узла",
        ["AggregateCompositionNode"] = "Изделие в составе узла",
        ["ComplexComposition"] = "Состав комплекса",
        ["ComplexCompositionItem"] = "Изделие в составе комплекса",
        ["MilitaryBranch"] = "Род войск",
        ["EquipmentType"] = "Вид техники",
        ["HKCardAttachment"] = "PDF-вложение ХК",
        ["Notification"] = "Уведомление",
        ["ReferenceProposal"] = "Предложение справочника",
    };

    private static readonly Dictionary<string, string[]> ActionFilterGroups = new()
    {
        ["Create"] = ["Create", "Created"],
        ["Update"] = ["Update", "Updated"],
        ["Delete"] = ["Delete", "Deleted"],
        ["StatusChange"] = ["Status:"],
        ["UserManagement"] = ["RoleChanged", "Blocked", "Unblocked", "Restored", "Created"],
        ["PermissionOverride"] = ["OverrideGranted", "OverrideDenied", "OverrideRemoved", "OverrideRevoked"],
        ["Security"] = ["RoleCreated", "ViewerMigrated", "Repaired"],
        ["Tasks"] = ["Task.Created", "Task.Assigned", "Task.Started", "Task.Completed", "Task.Cancelled", "Task.Overdue", "Task.Deleted"],
        ["Notifications"] = ["Notification.Created", "Notification.Read", "Notification.ReadAll"],
        ["Expiration"] = ["HK.ExpirationWarningCreated", "HK.ExpiredArchived", "Task.Overdue"],
    };

    public static AuditActionDisplay GetAction(string action)
    {
        if (Actions.TryGetValue(action, out var display))
            return display;

        if (action.StartsWith("Status:"))
            return new($"Изменение статуса: {TranslateStatus(action["Status:".Length..])}", AuditSeverity.Neutral);

        return new("Неизвестное действие", AuditSeverity.Neutral);
    }

    public static string GetEntityTypeDisplay(string entityType) =>
        EntityTypes.TryGetValue(entityType, out var name) ? name : entityType;

    public static string[] GetFilterActions(string? filterKey)
    {
        if (filterKey == null)
            return Actions.Keys.Concat(["Status:"]).ToArray();

        if (ActionFilterGroups.TryGetValue(filterKey, out var group))
            return group;

        return [filterKey];
    }

    public static string TranslateStatus(string statusCode) => statusCode switch
    {
        "Draft" => "Черновик",
        "OnReview" => "На проверке",
        "RevisionRequired" => "Требует доработки",
        "Approved" => "Утверждено",
        "Archived" => "Архив",
        "Deleted" => "Удалено",
        _ => statusCode,
    };
}
