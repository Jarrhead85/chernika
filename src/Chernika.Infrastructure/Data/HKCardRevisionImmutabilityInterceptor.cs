using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chernika.Infrastructure.Data;

/// <summary>
/// Явная защита зафиксированных редакций от изменения и удаления через
/// прикладной EF-контекст.
/// <para>
/// Проверка живёт в <see cref="SaveChangesInterceptor"/>, а не в сервисе:
/// сервис можно обойти, а перехватчик срабатывает на любом пути сохранения
/// изменений этого контекста, включая чужой код.
/// </para>
/// <para>
/// <b>Граница защиты.</b> Это защита прикладного уровня. Произвольный SQL
/// администратора, выполнивший <c>UPDATE</c> напрямую, её не останавливает:
/// средствами приложения от такого не защититься.
/// </para>
/// </summary>
public sealed class HKCardRevisionImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Guard(DbContext? context)
    {
        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (!IsSnapshot(entry))
                continue;

            // Added допускается: так создаётся редакция. Modified и Deleted —
            // нет: редакция неизменяема.
            if (entry.State is not EntityState.Added)
                throw new InvalidOperationException(
                    "Зафиксированная редакция карты неизменяема. " +
                    "Следующая фиксация создаёт новую редакцию.");
        }
    }

    private static bool IsSnapshot(EntityEntry entry) => entry.Entity switch
    {
        HKCardRevision => true,
        HKCardRevisionNode => true,
        HKCardRevisionItem => true,
        HKCardRevisionItemMaterial => true,
        _ => false,
    };
}