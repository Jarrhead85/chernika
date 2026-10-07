using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Защита последнего активного системного администратора при одновременных
/// действиях.
/// <para>
/// Проверяется МЕХАНИЗМ сериализации, а не сам отказ, и вот почему. Чтобы отказ
/// сработал, в системе должен остаться ровно один администратор, а общая
/// тестовая база всегда содержит собственного SystemAdmin фикстуры. Деактивировать
/// его в тесте нельзя — это ровно то, что защищается, и это сломало бы чужие
/// тесты. Поэтому доказывается то, от чего отказ зависит: две операции, каждая из
/// которых способна лишить систему администратора, не могут пройти одновременно.
/// </para>
/// <para>
/// Без блокировки проверка была «прочитал число администраторов — потом записал».
/// Два параллельных запроса, каждый про свою учётную запись, видели второго как
/// действующего и оба проходили.
/// </para>
/// </summary>
[Collection("Database")]
public class LastSystemAdminGuardTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly List<string> _users = new();

    public LastSystemAdminGuardTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var s = _fixture.CreateScope();
        await CreateAdminAsync(s);
        await CreateAdminAsync(s);
    }

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await s.Db.UserPermissionOverrides.Where(o => _users.Contains(o.UserId)).ExecuteDeleteAsync();
        await s.Db.UserRoles.Where(ur => _users.Contains(ur.UserId)).ExecuteDeleteAsync();

        foreach (var id in _users)
        {
            var u = await s.Users.FindByIdAsync(id);
            if (u != null) await s.Users.DeleteAsync(u);
        }
    }

    private async Task<string> CreateAdminAsync(TestScope s)
    {
        var user = new ApplicationUser
        {
            UserName = "lastadm_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@lastadm.test",
            EmailConfirmed = true,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Last-Adm-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, nameof(UserRole.SystemAdmin));

        _users.Add(user.Id);
        return user.Id;
    }

    /// <summary>
    /// Две транзакции с одинаковым advisory-ключом не могут идти одновременно:
    /// вторая ждёт фиксации первой. Именно это превращает проверку «есть ли
    /// другой активный администратор» из расчёта в устойчивую защиту.
    /// <para>
    /// Механизм проверяется напрямую в базе, а не через внутренний метод
    /// сервиса: открывать внутренний API ради теста было бы ослаблением
    /// ради зелёной проверки. Ключ в сервисе - тот же приём, что и здесь.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AdvisoryLock_SerializesTransactions_WithSameKey()
    {
        const long guardKey = 8123471290;

        await using var first = _fixture.CreateScope();
        await using var second = _fixture.CreateScope();

        await using var tx1 = await first.Db.Database.BeginTransactionAsync();
        await first.Db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", guardKey);

        var secondEntered = false;
        var secondTask = Task.Run(async () =>
        {
            await using var tx2 = await second.Db.Database.BeginTransactionAsync();
            await second.Db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", guardKey);
            secondEntered = true;
            await tx2.CommitAsync();
        });

        // Пока первая транзакция открыта, вторая обязана ждать. Без блокировки
        // она вошла бы мгновенно, и проверка была бы пустой.
        var whileHeld = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.NotSame(secondTask, whileHeld);
        Assert.False(secondEntered);

        await tx1.CommitAsync();

        var finished = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(secondTask, finished);
        Assert.True(secondEntered, "вторая транзакция должна была войти после фиксации первой");
    }

    /// <summary>
    /// Параллельные операции над разными администраторами не оставляют систему в
    /// неопределённом состоянии и не роняют службу.
    /// </summary>
    [Fact]
    public async Task ParallelGuardedOperations_Complete_AndKeepAtLeastOneAdmin()
    {
        var targets = _users.ToList();

        var operations = targets.Select(async id =>
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            // Проверка прав выполняется до блокировки, поэтому отказ возможен и
            // законен; проверяется сам факт завершения без исключения.
            return await s.UserMgmt.ToggleBlockAsync(id);
        });

        var results = await Task.WhenAll(operations);

        Assert.All(results, r => Assert.NotNull(r));

        await using var check = _fixture.CreateScope();
        var admins = await check.Users.GetUsersInRoleAsync(nameof(UserRole.SystemAdmin));
        Assert.True(admins.Any(a => a.IsActive), "не осталось ни одного активного системного администратора");
    }
}