using Microsoft.Extensions.Logging;

namespace Chernika.Web.Services;

/// <summary>
/// Scoped блокировка для сериализации обращений к <see cref="AppDbContext"/>
/// внутри одного Blazor Server circuit.
/// Предотвращает исключение EF Core "A second operation was started on this context instance".
/// </summary>
/// <remarks>
/// <para><b>Владение блокировкой.</b> Блокировку получает верхнеуровневая
/// операция; внутренние методы этого пути повторно её не получают, а
/// используют переданный lease. Вложенный захват исключён границами владения
/// в коде, а не распознаванием по счётчику: занятая блокировка не доказывает
/// рекурсию.</para>
/// <para><b>Конкуренция — это ожидание.</b> Независимая операция ждёт
/// освобождения; превышение ожидания — диагностика с указанием владельца.</para>
/// </remarks>
public sealed class CircuitDbLock : IDisposable
{
    /// <summary>Порог ожидания, после которого записывается предупреждение.</summary>
    private const long SlowWaitThresholdMs = 5_000;

    /// <summary>
    /// Предел ожидания. Превышение означает, что блокировку удерживает другая
    /// операция, которая не завершилась: ждать дальше бессмысленно, но и
    /// «зависшую» страницу оставлять нельзя.
    /// </summary>
    private const int MaxWaitMs = 60_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<CircuitDbLock>? _logger;
    private bool _disposed;

    /// <summary>Идентификатор текущего держателя — для диагностики ожидания.</summary>
    private long _currentOwner;

    /// <summary>Порядковый номер выданных lease.</summary>
    private long _leaseCounter;

    public CircuitDbLock() { }

    public CircuitDbLock(ILogger<CircuitDbLock> logger) => _logger = logger;

    /// <summary>Захват блокировки без указания операции.</summary>
    public Task<IAsyncDisposable> WaitAsync(CancellationToken ct = default)
        => WaitAsync("не указан", ct);

    /// <summary>
    /// Захватывает блокировку.
    /// <para>
    /// <paramref name="operation"/> — имя операции-владельца для диагностики:
    /// по журналу видно, кто ждёт блокировку и кто её держит.
    /// </para>
    /// </summary>
    public async Task<IAsyncDisposable> WaitAsync(
        string operation, CancellationToken ct = default)
    {
        var started = Environment.TickCount64;

        _logger?.LogDebug(
            "CircuitDbLock: ожидание блокировки, операция {Operation}, текущий владелец {Owner}",
            operation, _currentOwner);

        // Ожидание с таймаутом через ОТДЕЛЬНЫЙ токен: перегрузка
        // WaitAsync(timeout, ct) способна потерять permit при отмене, и
        // блокировка тогда «съедает» разрешения.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MaxWaitMs);

        try
        {
            await _gate.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogError(
                "CircuitDbLock: ожидание блокировки превысило {MaxWaitMs} мс, операция {Operation}, "
                + "владелец {Owner}. Операция circuit не завершилась.",
                MaxWaitMs, operation, _currentOwner);
            throw new TimeoutException(
                "Доступ к данным занят другой операцией более "
                + $"{MaxWaitMs / 1000} с. Повторите действие.");
        }

        var waited = Environment.TickCount64 - started;
        var id = Interlocked.Increment(ref _leaseCounter);
        _currentOwner = id;

        _logger?.LogDebug(
            "CircuitDbLock: блокировка получена, операция {Operation}, lease {LeaseId}, ожидание {WaitedMs} мс",
            operation, id, waited);

        if (waited >= SlowWaitThresholdMs)
        {
            _logger?.LogWarning(
                "CircuitDbLock: ожидание заняло {WaitedMs} мс (операция {Operation}, lease {LeaseId}).",
                waited, operation, id);
        }

        return new ReleaseScope(this, id, operation, waited);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// Lease блокировки. Повторное освобождение безопасно: освобождение
    /// выполняется один раз, иначе семафор получал бы лишний permit.
    /// </summary>
    private sealed class ReleaseScope : IAsyncDisposable
    {
        private readonly CircuitDbLock _owner;
        private readonly long _leaseId;
        private readonly string _operation;
        private readonly long _heldMs;
        private int _released;

        public ReleaseScope(
            CircuitDbLock owner, long leaseId, string operation, long heldMs)
        {
            _owner = owner;
            _leaseId = leaseId;
            _operation = operation;
            _heldMs = heldMs;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
                return ValueTask.CompletedTask;

            if (_owner._currentOwner == _leaseId)
                _owner._currentOwner = 0;

            _owner._logger?.LogDebug(
                "CircuitDbLock: блокировка освобождена, операция {Operation}, lease {LeaseId}, "
                + "удержание {HeldMs} мс",
                _operation, _leaseId, _heldMs);

            try
            {
                _owner._gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // circuit уже завершён, игнорируем
            }
            catch (SemaphoreFullException)
            {
                // повторное освобождение уже обработано выше
            }

            return ValueTask.CompletedTask;
        }
    }
}