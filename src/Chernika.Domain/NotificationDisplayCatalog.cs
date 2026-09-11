using Chernika.Domain.Enums;

namespace Chernika.Domain;

/// <summary>
/// Централизованные русские отображаемые значения для уведомлений
/// (колокольчик в шапке и страница /уведомления). Встроенное значение
/// enum и технические коды пользователям не выводятся.
/// </summary>
public static class NotificationDisplayCatalog
{
    /// <summary>Подпись типа события (короткая, для строки уведомления).</summary>
    public static string TypeLabel(NotificationType type) => type switch
    {
        NotificationType.TaskAssigned => "Задача назначена",
        NotificationType.TaskCompleted => "Задача выполнена",
        NotificationType.HKSubmittedForReview => "ХК на проверке",
        NotificationType.HKReturnedForRevision => "ХК возвращена на доработку",
        NotificationType.HKApproved => "ХК утверждена",
        NotificationType.HKExpiring => "Срок действия ХК истекает",
        NotificationType.HKExpired => "Срок действия ХК истёк",
        NotificationType.ReferenceProposalPending => "Предложение справочника",
        NotificationType.CompositionReturnedToDraft => "Состав возвращён на доработку",
        NotificationType.CompositionReviewRequested => "Состав отправлен на проверку",
        NotificationType.CompositionApproved => "Состав утверждён",
        NotificationType.Information => "Информация",
        NotificationType.System => "Система",
        _ => "Уведомление",
    };
}
