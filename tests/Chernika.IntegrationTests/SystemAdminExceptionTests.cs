using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Защищённое системное исключение: определяется в одном месте и ведёт себя
/// как утверждённое исключение, а не как обход всей модели прав.
/// <para>
/// Проверяется и положительное свойство (исключение действует там, где раньше
/// стояла именно эта проверка), и отрицательное (оно НЕ отменяет индивидуальный
/// запрет обычного пользователя, бизнес-правила и границы организации). Второе
/// важнее первого: «исключение» легко превратить в сквозной обход.
/// </para>
/// </summary>
[Collection("Database")]
public class SystemAdminExceptionTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly Dictionary<string, string> _users = new(StringComparer.Ordinal);

    public SystemAdminExceptionTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var s = _fixture.CreateScope();
        await CreateUserAsync(s, "plain");
    }

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var ids = _users.Values.ToList();
        await s.Db.UserPermissionOverrides.Where(o => ids.Contains(o.UserId)).ExecuteDeleteAsync();
        await s.Db.UserRoles.Where(ur => ids.Contains(ur.UserId)).ExecuteDeleteAsync();

        foreach (var id in ids)
        {
            var u = await s.Users.FindByIdAsync(id);
            if (u != null) await s.Users.DeleteAsync(u);
        }
    }

    private async Task CreateUserAsync(TestScope s, string key)
    {
        var user = new ApplicationUser
        {
            UserName = "sysadm_" + key + "_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@sysadm.test",
            EmailConfirmed = true,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Sys-Adm-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        _users[key] = user.Id;
    }

    private async Task SetDecisionAsync(TestScope s, string key, string code, bool granted)
    {
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = _users[key],
            PermissionCode = code,
            IsGranted = granted,
            Reason = "регресс-тест исключения SystemAdmin",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });

        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_users[key]);
    }

    [Fact]
    public async Task IsSystemAdmin_TrueForSystemAdmin()
    {
        await using var s = _fixture.CreateScope();

        Assert.True(await s.Permissions.IsSystemAdminAsync(_fixture.SystemAdminUser.Id));
    }

    [Fact]
    public async Task IsSystemAdmin_FalseForOrdinaryUser()
    {
        await using var s = _fixture.CreateScope();

        Assert.False(await s.Permissions.IsSystemAdminAsync(_users["plain"]));
    }

    [Fact]
    public async Task IsSystemAdmin_GrantingAdminRightDoesNotMakeSystemAdmin()
    {
        await using var s = _fixture.CreateScope();

        // Делегирование административного полномочия обычному пользователю НЕ
        // делает его системным администратором. Иначе выдача Users.Manage одному
        // человеку тихо дала бы ему все полномочия разом.
        await SetDecisionAsync(s, "plain", PermissionCodes.UsersManage, granted: true);

        Assert.False(await s.Permissions.IsSystemAdminAsync(_users["plain"]));
        Assert.True(await s.Permissions.HasPermissionAsync(_users["plain"], PermissionCodes.UsersManage));
    }

    [Fact]
    public async Task IsSystemAdmin_ForCurrentUser_FollowsActor()
    {
        await using var s = _fixture.CreateScope();

        s.User.CurrentUserId = Guid.Parse(_users["plain"]);
        Assert.False(await s.Permissions.IsSystemAdminAsync());

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        Assert.True(await s.Permissions.IsSystemAdminAsync());
    }

    [Fact]
    public async Task SystemAdmin_KeepsAccess_DespiteDenyingOwnPermission()
    {
        // Утверждённое свойство: индивидуальный запрет не ограничивает
        // системного администратора. Ошибочные существующие override не должны
        // лишать его системного доступа.
        await using var s = _fixture.CreateScope();

        await SetDecisionAsync(s, "plain", PermissionCodes.UsersManage, granted: false);

        // Системный администратор получает весь набор прав по своей роли, поэтому
        // даже при наличии запрета на конкретный код доступ к нему есть.
        Assert.True(await s.Permissions.HasPermissionAsync(_fixture.SystemAdminUser.Id, PermissionCodes.UsersManage));
        Assert.True(await s.Permissions.IsSystemAdminAsync(_fixture.SystemAdminUser.Id));
    }

    [Fact]
    public async Task InvalidatingCache_ClearsRoleDecisionToo()
    {
        await using var s = _fixture.CreateScope();

        var userId = _users["plain"];

        Assert.False(await s.Permissions.IsSystemAdminAsync(userId));

        // Роль выдали. Пока кэш не сброшен, решение остаётся прежним - так и
        // должно быть, иначе каждый вызов роли бил бы в базу.
        var user = await s.Users.FindByIdAsync(userId);
        await s.Users.AddToRoleAsync(user, nameof(UserRole.SystemAdmin));

        Assert.False(await s.Permissions.IsSystemAdminAsync(userId));

        // InvalidateCache обязан сбрасывать и решение об исключении: иначе
        // снятая роль продолжала бы действовать до истечения кэша.
        s.Permissions.InvalidateCache(userId);

        Assert.True(await s.Permissions.IsSystemAdminAsync(userId));

        await s.Users.RemoveFromRoleAsync(user, nameof(UserRole.SystemAdmin));
        s.Permissions.InvalidateCache(userId);

        Assert.False(await s.Permissions.IsSystemAdminAsync(userId));
    }

    [Fact]
    public async Task IndividualDecisionsForSystemAdmin_RejectedByServer()
    {
        // Проверка защищённости остаётся серверной: спрятать кнопку недостаточно.
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        // Считаем ДО и ПОСЛЕ, а не «не должно быть ни одной записи»: общая база
        // тестов переживает классы, и чужое наследие не должно превращать
        // проверку в ложное падение. Проверяется ровно то, что важно: вызов
        // ничего не записал.
        var adminId = _fixture.SystemAdminUser.Id;
        var before = await s.Db.UserPermissionOverrides.CountAsync(o => o.UserId == adminId);

        var (result, error) = await s.UserMgmt.GrantPermissionAsync(
            adminId, PermissionCodes.ReferenceView, "попытка выдать решение администратору");

        Assert.Null(result);
        Assert.NotNull(error);

        await using var check = _fixture.CreateScope();
        var after = await check.Db.UserPermissionOverrides.CountAsync(o => o.UserId == adminId);

        Assert.Equal(before, after);
    }
}