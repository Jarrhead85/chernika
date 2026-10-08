using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Защита последнего активного системного администратора проверяется реальными
/// конкурентными операциями, а не только захватом advisory-ключа.
/// <para>
/// Сценарий: база содержит ровно два активных SystemAdmin, и каждый является
/// актором операции против другого. Оба запроса идут почти одновременно, каждый
/// в своей области видимости и своём контексте. Первый, зафиксировавший
/// изменение, побеждает; второй, дождавшись блокировки, обязан увидеть уже
/// изменившееся состояние и получить контролируемый отказ.
/// </para>
/// <para>
/// Таймаут не считается корректным отказом: если задача не завершилась, тест
/// падает, иначе зависание маскировалось бы под защиту.
/// </para>
/// </summary>
public class LastSystemAdminConcurrencyTests : IClassFixture<TwoAdminsFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(45);

    private readonly TwoAdminsFixture _fixture;

    public LastSystemAdminConcurrencyTests(TwoAdminsFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Запускает две операции одновременно и требует, чтобы обе завершились в
    /// срок. Возвращает их результаты.
    /// </summary>
    private async Task<((bool, string?) First, (bool, string?) Second)> RunRaceAsync(
        Func<TestScope, string, Task<(bool Success, string? Error)>> operation)
    {
        // Барьер синхронизации: оба вызова стартуют по сигналу, а не по стеку
        // вызовов, иначе второй начинался бы заметно позже и гонки не было бы.
        var gate = new TaskCompletionSource();

        async Task<(bool, string?)> Branch(string actorId, string targetId)
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(actorId);

            await gate.Task;
            return await operation(s, targetId);
        }

        var firstTask = Branch(_fixture.AdminOneId, _fixture.AdminTwoId);
        var secondTask = Branch(_fixture.AdminTwoId, _fixture.AdminOneId);

        gate.SetResult();

        var all = Task.WhenAll(firstTask, secondTask);
        var finished = await Task.WhenAny(all, Task.Delay(Wait));

        Assert.True(ReferenceEquals(finished, all),
            "Обе операции должны завершиться: зависание не считается корректным отказом.");

        var results = await all;
        return (results[0], results[1]);
    }

    /// <summary>
    /// Проверяет результат гонки: ровно одна операция прошла, вторая отказана,
    /// и в базе остался хотя бы один активный администратор.
    /// </summary>
    private async Task AssertExactlyOneWinsAsync(
        (bool, string?) first, (bool, string?) second, string expectedRefusal)
    {
        var successes = new[] { first, second }.Count(r => r.Item1);
        var failures = new[] { first, second }.Count(r => !r.Item1);

        Assert.True(successes == 1,
            "Ровно одна операция должна завершиться успехом, а не двумя. Отказы: "
                + first.Item2 + " / " + second.Item2);

        Assert.Equal(1, failures);

        var refusal = first.Item1 ? second.Item2 : first.Item2;
        Assert.Equal(expectedRefusal, refusal);

        var active = await _fixture.ActiveAdminIdsAsync();
        Assert.NotEmpty(active);
    }

    [Fact]
    public async Task Guard_ActuallyBlocksConcurrentToggleBlock()
    {
        // Проверяется не абстрактная работа блокировки PostgreSQL, а то, что
        // реальная операция деактивации ждёт чужую транзакцию. Если бы guard
        // не брал блокировку, операция завершилась бы мгновенно, пока держится
        // чужая транзакция с тем же ключом.
        var holderReleased = new TaskCompletionSource();

        var holder = Task.Run(async () =>
        {
            await using var s = _fixture.CreateScope();
            await using var tx = await s.Db.Database.BeginTransactionAsync();

            await s.Db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})", 8123471290);

            // Держим ключ, пока операция не упрётся в ожидание, затем отпускаем.
            await holderReleased.Task;
            await tx.CommitAsync();
        });

        var actor = Task.Run(async () =>
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.AdminOneId);
            return await s.UserMgmt.ToggleBlockAsync(_fixture.AdminTwoId);
        });

        var premature = await Task.WhenAny(actor, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(!ReferenceEquals(premature, actor),
            "Операция не должна завершаться, пока держится чужая транзакция: "
            + "guard не блокирует операцию.");

        holderReleased.SetResult();
        await holder;

        var done = await Task.WhenAny(actor, Task.Delay(Wait));
        Assert.True(ReferenceEquals(done, actor),
            "Операция должна была завершиться после снятия блокировки.");

        var result = await actor;
        Assert.True(result.Item1, result.Item2);
    }

    [Fact]
    public async Task ConcurrentBlock_OfBothAdmins_LeavesOneActiveAndRefusesSecond()
    {
        var (first, second) = await RunRaceAsync(
            (s, targetId) => s.UserMgmt.ToggleBlockAsync(targetId));

        await AssertExactlyOneWinsAsync(
            first, second, "Нельзя лишить систему последнего активного системного администратора.");

        var blocked = await _fixture.ActiveAdminIdsAsync();
        Assert.Single(blocked);

        var audits = await _fixture.AuditActionsAsync();
        Assert.Single(audits.Where(a => a == "Blocked"));
    }

    [Fact]
    public async Task ConcurrentDelete_OfBothAdmins_LeavesOneActiveAndRefusesSecond()
    {
        var (first, second) = await RunRaceAsync(
            (s, targetId) => s.UserMgmt.DeleteUserAsync(targetId, "Гонка удаления администраторов"));

        await AssertExactlyOneWinsAsync(
            first, second, "Нельзя лишить систему последнего активного системного администратора.");

        var audits = await _fixture.AuditActionsAsync();
        Assert.Single(audits.Where(a => a == "Deleted"));
    }

    [Fact]
    public async Task ConcurrentRoleChange_OfBothAdmins_LeavesOneAdminAndRefusesSecond()
    {
        var (first, second) = await RunRaceAsync(
            (s, targetId) => s.UserMgmt.UpdateUserAsync(
                targetId,
                fullName: "Понижен до оператора",
                position: "Оператор",
                roleName: nameof(UserRole.Operator),
                branchId: _fixture.BranchId));

        await AssertExactlyOneWinsAsync(
            first, second, "Нельзя лишить систему последнего активного системного администратора.");

        await using var check = _fixture.CreateScope();

        // Проверяется не только число, но и отсутствие состояния «роль сняли,
        // новую не назначили»: у пониженного должна быть именно новая роль.
        var loweredId = await check.Db.Users
            .Where(u => u.FullName == "Понижен до оператора")
            .Select(u => u.Id)
            .FirstOrDefaultAsync();

        Assert.NotEqual(default, loweredId);

        var lowered = await check.Users.FindByIdAsync(loweredId.ToString());
        Assert.NotNull(lowered);
        Assert.True(await check.Users.IsInRoleAsync(lowered!, nameof(UserRole.Operator)));
        Assert.False(await check.Users.IsInRoleAsync(lowered!, nameof(UserRole.SystemAdmin)));

        var audits = await _fixture.AuditActionsAsync();
        Assert.Single(audits.Where(a => a == "RoleChanged"));
    }

    [Fact]
    public async Task MixedBlockAndRoleChange_LeaveOneAdminAndRefuseSecond()
    {
        // Деактивация одного администратора против смены роли другого. Оба
        // пути обязаны быть под одной блокировкой, иначе один из них увидит
        // устаревшее число администраторов.
        var gate = new TaskCompletionSource();

        async Task<(bool, string?)> BlockAsync()
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.AdminOneId);
            await gate.Task;
            return await s.UserMgmt.ToggleBlockAsync(_fixture.AdminTwoId);
        }

        async Task<(bool, string?)> LowerAsync()
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.AdminTwoId);
            await gate.Task;
            return await s.UserMgmt.UpdateUserAsync(
                _fixture.AdminOneId,
                fullName: "Понижен в смешанной гонке",
                position: "Оператор",
                roleName: nameof(UserRole.Operator),
                branchId: _fixture.BranchId);
        }

        var firstTask = BlockAsync();
        var secondTask = LowerAsync();
        gate.SetResult();

        var all = Task.WhenAll(firstTask, secondTask);
        var finished = await Task.WhenAny(all, Task.Delay(Wait));

        Assert.True(ReferenceEquals(finished, all),
            "Обе операции должны завершиться: зависание не считается корректным отказом.");

        var results = await all;
        var successes = results.Count(r => r.Item1);
        var failures = results.Count(r => !r.Item1);

        Assert.True(successes == 1,
            "Ровно одна операция должна пройти. Отказы: "
                + string.Join(" / ", results.Where(r => !r.Item1).Select(r => r.Item2)));

        Assert.Equal(1, failures);

        var refusal = results.First(r => !r.Item1).Item2;
        Assert.Equal(
            "Нельзя лишить систему последнего активного системного администратора.",
            refusal);

        var active = await _fixture.ActiveAdminIdsAsync();
        Assert.NotEmpty(active);
    }

    [Fact]
    public async Task MixedDeleteAndBlock_LeaveOneAdminAndRefuseSecond()
    {
        var gate = new TaskCompletionSource();

        async Task<(bool, string?)> DeleteAsync()
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.AdminOneId);
            await gate.Task;
            return await s.UserMgmt.DeleteUserAsync(
                _fixture.AdminTwoId, "Гонка удаления и блокировки");
        }

        async Task<(bool, string?)> BlockAsync()
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.AdminTwoId);
            await gate.Task;
            return await s.UserMgmt.ToggleBlockAsync(_fixture.AdminOneId);
        }

        var firstTask = DeleteAsync();
        var secondTask = BlockAsync();
        gate.SetResult();

        var all = Task.WhenAll(firstTask, secondTask);
        var finished = await Task.WhenAny(all, Task.Delay(Wait));

        Assert.True(ReferenceEquals(finished, all),
            "Обе операции должны завершиться: зависание не считается корректным отказом.");

        var results = await all;
        var successes = results.Count(r => r.Item1);
        Assert.True(successes == 1,
            "Ровно одна операция должна пройти. Отказы: "
                + string.Join(" / ", results.Where(r => !r.Item1).Select(r => r.Item2)));

        var active = await _fixture.ActiveAdminIdsAsync();
        Assert.NotEmpty(active);
    }

    [Fact]
    public async Task RestoreUserAsync_ToSystemAdmin_WorksAndRaisesCount()
    {
        // Восстановление - четвёртый путь, возвращающий роль администратора.
        // Он тоже обязан идти под блокировкой и не обязан быть запрещён.
        await _fixture.ResetAsync();

        string removedId;
        await using (var s = _fixture.CreateScope())
        {
            s.User.CurrentUserId = Guid.Parse(_fixture.AdminOneId);

            var (ok, error) = await s.UserMgmt.DeleteUserAsync(
                _fixture.AdminTwoId, "Подготовка проверки восстановления");

            Assert.True(ok, error);
            removedId = _fixture.AdminTwoId;
        }

        var before = await _fixture.ActiveAdminIdsAsync();
        Assert.Single(before);

        await using (var s = _fixture.CreateScope())
        {
            s.User.CurrentUserId = Guid.Parse(_fixture.AdminOneId);

            var (ok, error) = await s.UserMgmt.RestoreUserAsync(
                removedId, nameof(UserRole.SystemAdmin), null);

            Assert.True(ok, error);
        }

        var after = await _fixture.ActiveAdminIdsAsync();
        Assert.Equal(2, after.Count);
    }
}