namespace Chernika.Domain;

/// <summary>
/// Оповещение об изменении индивидуального решения по полномочию.
/// <para>
/// Механизм процесс-локальный: подписки живут в том же процессе, где
/// произошло изменение. Между разными процессами (например, отдельно
/// развёрнутые Web и Api) доставки нет, и обновление прав там становится
/// видимым только по истечении времени жизни кэша. Об этом ограничении
/// сказано прямо в отчёте, чтобы его не выдавали за мгновенное.
/// </para>
/// </summary>
public interface IPermissionChangeNotifier
{
    /// <summary>
    /// Сообщает, что решение по правам пользователя изменилось и уже
    /// зафиксировано. Вызывается после успешной фиксации, поэтому
    /// подписчики не увидят отменённое изменение.
    /// </summary>
    void NotifyChanged(string userId);

    /// <summary>Подписка на изменения прав указанного пользователя.</summary>
    IDisposable Subscribe(string userId, Action handler);
}

/// <inheritdoc />
public sealed class PermissionChangeNotifier : IPermissionChangeNotifier
{
    private readonly List<Subscription> _subscriptions = new();
    private readonly object _gate = new();

    public void NotifyChanged(string userId)
    {
        Action[] handlers;

        lock (_gate)
        {
            // Копия снимка: подписчик вправе отписаться прямо из обработчика,
            // и обход по исходному списку при этом сломался бы.
            handlers = _subscriptions
                .Where(s => s.UserId == userId)
                .Select(s => s.Handler)
                .ToArray();
        }

        foreach (var handler in handlers)
            handler();
    }

    public IDisposable Subscribe(string userId, Action handler)
    {
        var subscription = new Subscription(this, userId, handler);

        lock (_gate)
            _subscriptions.Add(subscription);

        return subscription;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly PermissionChangeNotifier _owner;
        private bool _disposed;

        public Subscription(PermissionChangeNotifier owner, string userId, Action handler)
        {
            _owner = owner;
            UserId = userId;
            Handler = handler;
        }

        public string UserId { get; }

        public Action Handler { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.Remove(this);
        }
    }
}