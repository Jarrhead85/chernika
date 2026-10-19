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
/// используют переданный lease. Вложенный захват исключён границами
/// владения, а не диагностикой по счётчику.</para>
/// <para><b>Конкуренция — это ожидание.</b> Независимая операция ждёт
/// освобождения блокировки. Раньше ожидание определялось по счётчику глубины,
/// и конкурентный захват получал ложную ошибку «вложенный захват»;
/// вложенность при этом не определялась надёжно: значение
/// <c>AsyncLocal</c>, установленное внутри <c>async</c>-метода, теряется при
/// возврате к вызывающему коду.</para>
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

    /// <summary>
    /// Захватывает блокировку. Независимая операция ждёт освобождения:
    /// ожидание не считается вложенным захватом и не отвергается ошибкой.
    /// </summary>
    public async Task<IAsyncDisposable> WaitAsync(CancellationToken ct = default)
    {
        var started = Environment.TickCount64;

        if (!await _gate.WaitAsync(MaxWaitMs, ct))
        {
            _logger?.LogError(
                "CircuitDbLock: ожидание блокировки превысило {MaxWaitMs} мс — операция circuit не завершилась.",
                MaxWaitMs);
            throw new TimeoutException(
                "Доступ к данным занят другой операцией более "
                + $"{MaxWaitMs / 1000} с. Повторите действие.");
        }

        var waited = Environment.TickCount64 - started;
        var id = Interlocked.Increment(ref _leaseCounter);
        _currentOwner = id;

        if (waited >= SlowWaitThresholdMs)
        {
            _logger?.LogWarning(
                "CircuitDbLock: ожидание заняло {WaitedMs} мс (lease {LeaseId}). "
                + "Долгое ожидание указывает на конкуренцию операций circuit, а не на вложенный захват.",
                waited, id);
        }

        return new ReleaseScope(this, id);
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
        private int _released;

        public ReleaseScope(CircuitDbLock owner, long leaseId)
        {
            _owner = owner;
            _leaseId = leaseId;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
                return ValueTask.CompletedTask;

            if (_owner._currentOwner == _leaseId)
                _owner._currentOwner = 0;

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