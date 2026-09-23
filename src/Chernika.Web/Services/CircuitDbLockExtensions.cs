namespace Chernika.Web.Services;

/// <summary>
/// Хелперы для выполнения сервисных операций под общим circuit-локом.
/// Все обращения к scoped <see cref="AppDbContext"/> из UI должны быть
/// сериализованы, иначе EF Core падает с "A second operation was started".
/// </summary>
/// <remarks>
/// ВНИМАНИЕ: <see cref="CircuitDbLock"/> нереентерабельный. Нельзя вызывать
/// <c>LockedAsync</c> из кода, который уже удерживает лок через
/// <c>DbLock.WaitAsync()</c> — это приводит к взаимной блокировке circuit.
/// </remarks>
public static class CircuitDbLockExtensions
{
    /// <summary>Выполняет операцию с результатом под circuit-локом.</summary>
    public static async Task<T> LockedAsync<T>(this CircuitDbLock gate, Func<Task<T>> operation)
    {
        await using (await gate.WaitAsync())
        {
            return await operation();
        }
    }

    /// <summary>Выполняет операцию без результата под circuit-локом.</summary>
    public static async Task LockedAsync(this CircuitDbLock gate, Func<Task> operation)
    {
        await using (await gate.WaitAsync())
        {
            await operation();
        }
    }
}
