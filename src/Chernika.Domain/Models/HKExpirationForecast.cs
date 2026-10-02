namespace Chernika.Domain.Models;

/// <summary>
/// Прогноз обработки сроков действия ХК на конкретный UTC-день.
/// <para>
/// Модель ответа на вопрос «что сделает worker при первом запуске», не отвечая
/// на него запуском worker'а. Именно для этого прогноз и нужен: включение
/// обработки способно архивировать существующие карты, а «посмотреть» обычным
/// запуском нельзя — это и есть запись на рабочей базе.
/// </para>
/// </summary>
public sealed record HKExpirationForecast
{
    /// <summary>UTC-день, на который построен прогноз.</summary>
    public required DateOnly TodayUtc { get; init; }

    /// <summary>Пороги предупреждения, использованные в расчёте.</summary>
    public required IReadOnlyList<int> WarningDays { get; init; }

    /// <summary>Порог, создающий задачу пересмотра (наименьший из WarningDays).</summary>
    public required int ReviewTaskThreshold { get; init; }

    /// <summary>Срок задачи пересмотра из конфигурации, в днях.</summary>
    public required int ReviewTaskDueDays { get; init; }

    /// <summary>По карточкам, которые обработка затронет или пропустит.</summary>
    public required IReadOnlyList<HKExpirationForecastCard> Cards { get; init; }

    public int CardsToArchive => Cards.Count(c => c.WillArchive);

    public int CardsToNotifyExpiring => Cards.Count(c => c.WillNotifyExpiring);

    public int CardsToNotifyExpired => Cards.Count(c => c.WillNotifyExpired);

    public int ReviewTasksToCreate => Cards.Count(c => c.WillCreateReviewTask);

    public int OpenTasksToClose => Cards.Sum(c => c.OpenWorkflowTasksToClose);

    public int ComponentLinksToPreserve => Cards.Sum(c => c.ComponentLinksToPreserve);

    /// <summary>Адресаты, исключённые из уведомлений по организации или активности.</summary>
    public int RecipientsExcluded => Cards.Sum(c => c.RecipientsExcluded);
}

/// <summary>Прогноз по одной карточке.</summary>
public sealed record HKExpirationForecastCard
{
    public required Guid Id { get; init; }

    public required string Code { get; init; }

    public required string Version { get; init; }

    public required DateOnly ExpirationDayUtc { get; init; }

    /// <summary>Дней до окончания. Отрицательное — срок истёк.</summary>
    public required int DaysLeft { get; init; }

    /// <summary>Будет ли автоархивирование. Истина только на следующий день после срока.</summary>
    public required bool WillArchive { get; init; }

    /// <summary>Будет ли уведомление «истекает» (включая догоняющее).</summary>
    public required bool WillNotifyExpiring { get; init; }

    /// <summary>Будет ли уведомление «истёк» (в день срока или догоняющее).</summary>
    public required bool WillNotifyExpired { get; init; }

    /// <summary>Будет ли создана задача «Пересмотр ХК».</summary>
    public required bool WillCreateReviewTask { get; init; }

    /// <summary>Порог уведомления, который сработает. 0 — уведомления не будет.</summary>
    public required int NotificationThreshold { get; init; }

    /// <summary>Будет ли уведомление догоняющим, то есть порог был пропущен простоем.</summary>
    public required bool IsCatchUpNotification { get; init; }

    /// <summary>Открытые workflow-задачи карты, которые закроет автоархивация.</summary>
    public required int OpenWorkflowTasksToClose { get; init; }

    /// <summary>Ссылки родительских/дочерних ХК, которые должны сохраниться.</summary>
    public required int ComponentLinksToPreserve { get; init; }

    /// <summary>Строк журнала статусов, которые должны сохраниться.</summary>
    public required int StatusLogRowsToPreserve { get; init; }

    /// <summary>Кандидатов-получателей, отсечённых по организации или активности.</summary>
    public required int RecipientsExcluded { get; init; }

    /// <summary>Человекочитаемый перечень действий для предъявления владельцу.</summary>
    public required IReadOnlyList<string> PlannedActions { get; init; }
}