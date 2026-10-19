namespace Chernika.Web.Services;

/// <summary>
/// Scoped блокировка для сериализации обращений к <see cref="AppDbContext"/>
/// внутри одного Blazor Server circuit.
/// Предотвращает исключение EF Core "A second operation was started on this context instance".
/// </summary>
public sealed class CircuitDbLock : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Глубина захвата в этом circuit.
    /// <para>
    /// Счётчик живёт в самом объекте, а не в <c>AsyncLocal</c>: значение,
    /// присвоенное внутри <c>async</c>-метода, теряется при возврате к
    /// вызывающему коду, и проверка вложенного захвата молча не срабатывала.
    /// Вложенный захват приводил не к отказу, а к вечному ожиданию семафора,
    /// который держал сам же внешний захват, — страница висела в состоянии
    /// «Загрузка…» без единой ошибки в журнале.
    /// </para>
    /// </summary>
    private int _depth;

    public async Task<IAsyncDisposable> WaitAsync(CancellationToken ct = default)
    {
        if (_depth > 0)
            throw new InvalidOperationException(
                "Вложенный захват CircuitDbLock недопустим: семафор нереентерабельный, " +
                "это приводит к взаимной блокировке circuit.");

        await _gate.WaitAsync(ct);
        _depth++;
        return new ReleaseScope(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed class ReleaseScope : IAsyncDisposable
    {
        private readonly CircuitDbLock _owner;
        private bool _released;

        public ReleaseScope(CircuitDbLock owner) => _owner = owner;

        public ValueTask DisposeAsync()
        {
            if (_released) return ValueTask.CompletedTask;
            _released = true;

            if (_owner._depth > 0)
                _owner._depth--;

            try
            {
                _owner._gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // circuit уже завершён, игнорируем
            }
            return ValueTask.CompletedTask;
        }
    }
}
