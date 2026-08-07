namespace Chernika.Web.Services;

/// <summary>
/// Скапированный (на весь Blazor-цикл) контейнер состояния колокольчика уведомлений.
/// Позволяет обновить счётчик непрочитанных в topbar после действий на странице уведомлений,
/// не храня счётчик только в JavaScript.
/// </summary>
public sealed class NotificationRefreshService
{
    public event Func<Task>? Changed;

    public async Task NotifyChangedAsync()
    {
        if (Changed != null)
            await Changed.Invoke();
    }
}
