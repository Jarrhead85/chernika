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
    /// Признак того, что текущий асинхронный поток уже удерживает лок.
    /// Семафор нереентерабельный, поэтому вложенный захват — это гарантированный
    /// дедлок; лучше упасть сразу с понятной ошибкой, чем подвесить circuit.
    /// </summary>
    private static readonly AsyncLocal<bool> _heldByCurrentFlow = new();

    public async Task<IAsyncDisposable> WaitAsync(CancellationToken ct = default)
    {
        if (_heldByCurrentFlow.Value)
            throw new InvalidOperationException(
                "Вложенный захват CircuitDbLock недопустим: семафор нереентерабельный, " +
                "это приводит к взаимной блокировке circuit.");

        await _gate.WaitAsync(ct);
        _heldByCurrentFlow.Value = true;
        return new ReleaseScope(_gate);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed class ReleaseScope : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _released;

        public ReleaseScope(SemaphoreSlim gate) => _gate = gate;

        public ValueTask DisposeAsync()
        {
            if (_released) return ValueTask.CompletedTask;
            _released = true;
            _heldByCurrentFlow.Value = false;
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // circuit уже завершён, игнорируем
            }
            return ValueTask.CompletedTask;
        }
    }
}
