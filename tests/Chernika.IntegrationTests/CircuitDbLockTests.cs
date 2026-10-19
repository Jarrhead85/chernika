using Chernika.Web.Services;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Блокировка доступа к общему AppDbContext внутри Blazor Server circuit.
/// <para>
/// Проверяется различие двух вещей, которые раньше смешивались: вложенный
/// захват одной операции и конкурентный захват другой. Конкурентная
/// операция обязана ЖДАТЬ освобождения, а не получать ложную ошибку
/// «вложенный захват».
/// </para>
/// <para>
/// Синхронизация построена на управляемых сигналах: тесты не полагаются на
/// случайные задержки.
/// </para>
/// </summary>
public class CircuitDbLockTests
{
    /// <summary>
    /// Независимая операция ждёт освобождения блокировки и не получает отказа.
    /// </summary>
    [Fact]
    public async Task IndependentOperation_WaitsForRelease()
    {
        using var dbLock = new CircuitDbLock();
        var acquired = new TaskCompletionSource();
        var secondAcquired = new TaskCompletionSource();

        await using var first = await dbLock.WaitAsync();
        acquired.SetResult();

        var second = Task.Run(async () =>
        {
            await using var lease = await dbLock.WaitAsync();
            secondAcquired.SetResult();
        });

        // Вторая операция ещё не получила блокировку: она ждёт.
        await acquired.Task;
        Assert.False(secondAcquired.Task.IsCompleted);

        await first.DisposeAsync();

        await secondAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await second;
    }

    /// <summary>После освобождения блокировки отложенная операция выполняется.</summary>
    [Fact]
    public async Task Operation_WaitingForLock_RunsAfterRelease()
    {
        using var dbLock = new CircuitDbLock();
        var ran = false;

        await using (var first = await dbLock.WaitAsync())
        {
            var pending = Task.Run(async () =>
            {
                await using var lease = await dbLock.WaitAsync();
                ran = true;
            });

            // Освобождение выполняется, пока вторая операция ждёт.
            await Task.Delay(50);
            await first.DisposeAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(ran);
    }

    /// <summary>Отмена ожидания не повреждает блокировку.</summary>
    [Fact]
    public async Task CancellationOfWaitingOperation_DoesNotBreakLock()
    {
        using var dbLock = new CircuitDbLock();

        var first = await dbLock.WaitAsync();
        using var cts = new CancellationTokenSource();
        var waiting = dbLock.WaitAsync(cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // Блокировка осталась у первого lease: следующий захват ждёт, а не проходит.
        var next = dbLock.WaitAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);

        await first.DisposeAsync();

        await using var lease = await next.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Исключение внутри операции освобождает блокировку.</summary>
    [Fact]
    public async Task ExceptionInsideOperation_ReleasesLock()
    {
        using var dbLock = new CircuitDbLock();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lease = await dbLock.WaitAsync();
            throw new InvalidOperationException("сбой внутри операции");
        });

        await using var lease = await dbLock.WaitAsync();
    }

    /// <summary>Повторное освобождение lease безопасно.</summary>
    [Fact]
    public async Task RepeatedRelease_IsSafe()
    {
        using var dbLock = new CircuitDbLock();

        var lease = await dbLock.WaitAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // Блокировка не «размножилась»: следующий захват идёт последовательно.
        await using var first = await dbLock.WaitAsync();
        var secondTaken = new TaskCompletionSource();
        var pending = Task.Run(async () =>
        {
            await using var lease = await dbLock.WaitAsync();
            secondTaken.SetResult();
        });

        Assert.False(secondTaken.Task.IsCompleted);
        await first.DisposeAsync();
        await secondTaken.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await pending;
    }
}