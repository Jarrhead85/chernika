using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Атомарность административных операций: отказ на любом шаге не должен
/// оставлять частично применённых изменений.
/// <para>
/// Сбой подменяется перехватчиком команды на записи в таблицу аудита, то есть
/// после того, как изменения пользователя уже записаны. Если транзакция
/// охватывает и пользователя, и аудит, откат убирает всё; если аудит вне
/// транзакции, пользователь остаётся изменённым.
/// </para>
/// </summary>
[Collection("Database")]
public class UserOperationAtomicityTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly List<string> _created = new();

    public UserOperationAtomicityTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        FailingCommandInterceptor.Disarm();

        if (_created.Count == 0)
            return;

        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await s.Db.UserPermissionOverrides.Where(o => _created.Contains(o.UserId)).ExecuteDeleteAsync();
        await s.Db.AuditLogs.Where(a => _created.Contains(a.EntityId)).ExecuteDeleteAsync();
        await s.Db.UserRoles.Where(u => _created.Contains(u.UserId)).ExecuteDeleteAsync();

        foreach (var id in _created)
        {
            var user = await s.Users.FindByIdAsync(id);
            if (user != null)
                await s.Users.DeleteAsync(user);
        }
    }

    /// <summary>
    /// Вызывает административную операцию, допуская оба честных исхода при
    /// сбое инфраструктуры: контролируемый отказ либо пробрасывание исключения.
    /// <para>
    /// Второй вариант допустим по спецификации («контролируемую ошибку либо
    /// согласованное исключение»), и важно лишь одно: успех не сообщается и
    /// изменения не остаются.
    /// </para>
    /// </summary>
    private static async Task<(bool Success, string? Error)> RunAllowingThrowAsync(
        Func<Task<(bool Success, string? Error)>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            return (false, "операция прервана сбоем инфраструктуры");
        }
    }

    private async Task<string> CreateUserAsync(string login, string role = "Operator")
    {
        await using var s = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            UserName = login,
            Email = login + "@atomic.test",
            EmailConfirmed = true,
            FullName = "Атомарность " + login,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Atomic-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);

        _created.Add(user.Id);
        return user.Id;
    }

    [Fact]
    public async Task DeleteUserAsync_AuditFailure_RollsBackDeletionEntirely()
    {
        var userId = await CreateUserAsync("atomic_del_" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            // Сбой на вставке записи аудита: пользователь к этому моменту уже
            // помечен удалённым, но ещё не зафиксирован.
            FailingCommandInterceptor.ArmAt("\"AuditLogs\"", 1);

            var (ok, error) = await RunAllowingThrowAsync(
                () => s.UserMgmt.DeleteUserAsync(userId, "Проверка отката"));

            Assert.False(ok, "Операция обязана сообщить об отказе, а не об успехе.");
            Assert.NotNull(error);
            Assert.True(FailingCommandInterceptor.Fired, "Сбой аудита должен был быть внедрён.");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        // Новая область: пользователь обязан остаться нетронутым.
        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(userId);

            Assert.NotNull(user);
            Assert.False(user!.IsDeleted, "Удаление должно было откатиться.");
            Assert.True(user.IsActive, "Пользователь должен остаться активным.");
            Assert.Null(user.DeletedAt);
        }

        await using (var audit = _fixture.CreateScope())
        {
            var entry = await audit.Db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.EntityId == userId && a.Action == "Deleted");

            Assert.False(entry, "Запись об успешном удалении не должна была сохраниться.");
        }
    }

    [Fact]
    public async Task ToggleBlockAsync_AuditFailure_RollsBackBlockingAndKeepsAccountActive()
    {
        var userId = await CreateUserAsync("atomic_block_" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            FailingCommandInterceptor.ArmAt("\"AuditLogs\"", 1);

            var (ok, error) = await RunAllowingThrowAsync(
                () => s.UserMgmt.ToggleBlockAsync(userId));

            Assert.False(ok, "Операция обязана сообщить об отказе.");
            Assert.NotNull(error);
            Assert.True(
                FailingCommandInterceptor.Fired,
                "Ожидался сбой записи аудита, получено: " + error);
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(userId);

            Assert.NotNull(user);
            Assert.True(user!.IsActive, "Блокировка должна была откатиться вместе с аудитом.");

            // Признак именно блокировки - срок LockoutEnd: он выставляется в
            // бессрочный. У свежего пользователя он пуст, у заблокированного
            // задан. Флаг LockoutEnabled у нового пользователя включён по
            // умолчанию настройкой Identity, поэтому он непригоден как признак.
            Assert.True(
                user.LockoutEnd is null,
                "Срок блокировки не должен был сохраниться после отката: " + user.LockoutEnd);
        }

        await using (var audit = _fixture.CreateScope())
        {
            var entry = await audit.Db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.EntityId == userId && a.Action == "Blocked");

            Assert.False(entry, "Запись о блокировке не должна была сохраниться.");
        }
    }

    [Fact]
    public async Task UpdateUserAsync_RoleChangeFailure_RollsBackBothRoleAndProfile()
    {
        // Сценарий спецификации: недопустимо состояние «старую роль сняли, новую
        // не назначили». Сбой вставляется на удаление связи роли - то есть уже
        // после сохранения прочих полей пользователя.
        var userId = await CreateUserAsync("atomic_role_" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            FailingCommandInterceptor.ArmAt("DELETE FROM \"UserRoles\"", 1);

            var (ok, error) = await RunAllowingThrowAsync(
                () => s.UserMgmt.UpdateUserAsync(
                    userId,
                    fullName: "Должно откатиться",
                    position: "Новая должность",
                    roleName: nameof(UserRole.NormAdmin),
                    branchId: _fixture.BranchA));

            Assert.False(ok, "Операция обязана сообщить об отказе.");
            Assert.NotNull(error);
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(userId);
            Assert.NotNull(user);

            // Профиль не изменился.
            Assert.NotEqual("Должно откатиться", user!.FullName);
            Assert.NotEqual("Новая должность", user.Position);

            // Роль прежняя: пользователь не остался без роли.
            var inUserStore = await check.Users.IsInRoleAsync(user, nameof(UserRole.Operator));
            Assert.True(inUserStore, "Пользователь обязан сохранить прежнюю роль.");

            var inNewRole = await check.Users.IsInRoleAsync(user, nameof(UserRole.NormAdmin));
            Assert.False(inNewRole, "Новая роль не должна была сохраниться.");
        }

        await using (var audit = _fixture.CreateScope())
        {
            var success = await audit.Db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.EntityId == userId && a.Action == "RoleChanged");

            Assert.False(success, "Запись об успешной смене роли не должна была сохраниться.");
        }
    }

    [Fact]
    public async Task RestoreUserAsync_AuditFailure_LeavesUserDeleted()
    {
        // Восстановление тоже обязано быть атомарным: иначе пользователь мог бы
        // оказаться активным без записи аудита.
        var userId = await CreateUserAsync("atomic_restore_" + Guid.NewGuid().ToString("N")[..6]);

        await using (var s = _fixture.CreateScope())
        {
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            var (ok, error) = await s.UserMgmt.DeleteUserAsync(userId, "Подготовка восстановления");

            Assert.True(ok, error);
        }

        try
        {
            await using var s = _fixture.CreateScope();
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            FailingCommandInterceptor.ArmAt("\"AuditLogs\"", 1);

            var (ok, error) = await RunAllowingThrowAsync(
                () => s.UserMgmt.RestoreUserAsync(
                    userId, nameof(UserRole.Operator), _fixture.BranchA));

            Assert.False(ok, "Операция обязана сообщить об отказе.");
            Assert.NotNull(error);
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(userId);

            Assert.NotNull(user);
            Assert.True(user!.IsDeleted, "Восстановление должно было откатиться.");
            Assert.False(user.IsActive);
        }
    }

    [Fact]
    public async Task DeniedOperation_LeavesNoAuditAndNoDataChange()
    {
        // Отказ по бизнес-правилу не должен ни менять данные, ни писать запись
        // об успехе.
        var userId = await CreateUserAsync("atomic_deny_" + Guid.NewGuid().ToString("N")[..6]);

        await using (var s = _fixture.CreateScope())
        {
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (ok, error) = await s.UserMgmt.DeleteUserAsync(userId, "   ");

            Assert.False(ok);
            Assert.NotNull(error);
        }

        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(userId);

            Assert.NotNull(user);
            Assert.False(user!.IsDeleted);

            var audit = await check.Db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.EntityId == userId && a.Action == "Deleted");

            Assert.False(audit, "Отказ не должен оставлять запись аудита.");
        }
    }
}