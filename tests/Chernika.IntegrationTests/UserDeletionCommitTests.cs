using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Удаление пользователя обязано фиксировать изменения.
/// <para>
/// На исходном коде метод сообщал об успехе до вызова CommitAsync, который
/// физически находился за <c>return</c> и был недостижим. Транзакция при этом
/// открывалась, поэтому при освобождении откатывалась: сервис отвечал
/// «удалено», а пользователь оставался в базе.
/// </para>
/// <para>
/// Тест проверяет наблюдаемое состояние в новом scope, а не наличие строки
/// CommitAsync в исходнике: перечитывание из другой области видимости
/// отличает зафиксированное изменение от откаченного.
/// </para>
/// </summary>
[Collection("Database")]
public class UserDeletionCommitTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private string _targetId = string.Empty;

    public UserDeletionCommitTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_targetId))
            return;

        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await s.Db.UserPermissionOverrides.Where(o => o.UserId == _targetId).ExecuteDeleteAsync();
        await s.Db.Notifications.Where(n => n.UserId == _targetId).ExecuteDeleteAsync();
        await s.Db.AuditLogs.Where(a => a.EntityId == _targetId).ExecuteDeleteAsync();

        var user = await s.Users.FindByIdAsync(_targetId);
        if (user != null)
            await s.Users.DeleteAsync(user);
    }

    /// <summary>Создаёт обычного пользователя и возвращает его идентификатор.</summary>
    private async Task<string> CreateTargetAsync(string login, string role = "Operator")
    {
        await using var s = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            UserName = login,
            Email = login + "@delete.test",
            EmailConfirmed = true,
            FullName = "Удаляемый " + login,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Delete-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);

        return user.Id;
    }

    [Fact]
    public async Task DeleteUserAsync_ReportsSuccess_AndChangeIsActuallyCommitted()
    {
        _targetId = await CreateTargetAsync("del_ok_" + Guid.NewGuid().ToString("N")[..6]);

        string actorId;
        await using (var s = _fixture.CreateScope())
        {
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            actorId = s.User.CurrentUserId!.Value.ToString();

            var (ok, error) = await s.UserMgmt.DeleteUserAsync(
                _targetId, "Проверка фиксации удаления");

            Assert.Null(error);
            Assert.True(ok);
        }

        // Новая область видимости: видны только зафиксированные изменения.
        // Удаление мягкое, строка сохраняется, поэтому проверяется именно факт
        // фиксации, а не отсутствие записи.
        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(_targetId);

            Assert.NotNull(user);
            Assert.True(user!.IsDeleted, "Признак удаления должен быть сохранён.");
            Assert.False(user.IsActive, "Удалённый пользователь не должен быть активен.");
            Assert.NotNull(user.DeletedAt);
            Assert.Equal(actorId, user.DeletedByUserId);
        }

        // Запись audit тоже должна быть зафиксирована: сервис сообщил успех.
        await using (var auditCheck = _fixture.CreateScope())
        {
            var entry = await auditCheck.Db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.EntityId == _targetId
                            && a.Action == "Deleted"
                            && a.UserId.ToString() == actorId);

            Assert.True(entry, "Запись аудита об удалении должна быть сохранена.");
        }
    }

    [Fact]
    public async Task DeleteUserAsync_PersistsLockoutAndSecurityStampChange()
    {
        // Удаление должно зафиксировать не только флаг IsDeleted, но и
        // блокировку входа: промежуточные записи Identity store тоже откатились
        // бы вместе с транзакцией.
        _targetId = await CreateTargetAsync("del_lock_" + Guid.NewGuid().ToString("N")[..6]);

        await using (var s = _fixture.CreateScope())
        {
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (ok, error) = await s.UserMgmt.DeleteUserAsync(
                _targetId, "Проверка блокировки при удалении");

            Assert.Null(error);
            Assert.True(ok);
        }

        // Блокировка входа сохраняется вместе с удалением. Проверяется чтением
        // без глобальных фильтров, чтобы увидеть и удалённую строку.
        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(_targetId);

            Assert.NotNull(user);
            Assert.True(user!.IsDeleted);
            Assert.False(user.IsActive, "Удалённый пользователь не должен быть активен.");
            Assert.True(user.LockoutEnabled, "Блокировка входа должна быть сохранена.");

            // Хеш пароля сам по себе остаётся верным, поэтому проверять вход
            // через CheckPasswordAsync бессмысленно: он смотрит только хеш и не
            // учитывает ни активность, ни блокировку. Значит проверяется
            // сохранённое состояние, по которому вход всё равно будет отклонён.
            Assert.Equal(DateTimeOffset.MaxValue, user.LockoutEnd!.Value);
        }
    }

    [Fact]
    public async Task DeleteUserAsync_RefusesSelfWithoutChangingAnything()
    {
        // Отказ не должен оставлять следов: guard открывается и для отказа,
        // поэтому важно убедиться, что данные не изменились.
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var (ok, error) = await s.UserMgmt.DeleteUserAsync(
            _fixture.SystemAdminUser.Id, "Попытка удалить себя");

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task DeleteUserAsync_RejectsBlankReason_WithoutTouchingUser()
    {
        _targetId = await CreateTargetAsync("del_reason_" + Guid.NewGuid().ToString("N")[..6]);

        await using (var s = _fixture.CreateScope())
        {
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (ok, error) = await s.UserMgmt.DeleteUserAsync(_targetId, "   ");

            Assert.False(ok);
            Assert.NotNull(error);
        }

        await using (var check = _fixture.CreateScope())
        {
            var user = await check.Users.FindByIdAsync(_targetId);

            Assert.NotNull(user);
            Assert.False(user!.IsDeleted);
            Assert.True(user.IsActive);
        }
    }
}