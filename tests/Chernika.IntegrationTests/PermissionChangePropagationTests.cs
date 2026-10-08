using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Смена решения через сервис и её последствия для уже открытых сессий.
/// <para>
/// Требование спецификации: после изменения решения повторный вызов и НОВЫЙ scope
/// обязаны видеть новое состояние без ручного сброса кэша тестом. Прежние проверки
/// сбрасывали кэш вручную, чем скрывали бы дефект: сервис мог бы его не сбрасывать,
/// и тест всё равно прошёл бы.
/// </para>
/// <para>
/// Операцией-индикатором выбрана работа с индивидуальными правами: право
/// Permissions.Manage есть только у системного администратора, поэтому его всегда
/// можно запретить обычному пользователю и проверить отказ на живой операции.
/// Право Reference.View для этой цели не годится — его получают и Guest, и
/// NormAdmin, то есть запрет нечего наблюдать.
/// </para>
/// </summary>
[Collection("Database")]
public class PermissionChangePropagationTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly Dictionary<string, string> _users = new(StringComparer.Ordinal);

    public PermissionChangePropagationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var s = _fixture.CreateScope();
        await CreateUserAsync(s, "owner", nameof(UserRole.NormAdmin));
    }

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var ids = _users.Values.ToList();
        await s.Db.UserPermissionOverrides.Where(o => ids.Contains(o.UserId)).ExecuteDeleteAsync();

        foreach (var id in ids)
        {
            var u = await s.Users.FindByIdAsync(id);
            if (u != null) await s.Users.DeleteAsync(u);
        }
    }

    private async Task CreateUserAsync(TestScope s, string key, string role)
    {
        var user = new ApplicationUser
        {
            UserName = "permchange_" + key + "_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@permchange.test",
            EmailConfirmed = true,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Perm-Change-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);

        _users[key] = user.Id;
    }

    /// <summary>Выдаёт тестовому пользователю Permissions.Manage через решение.</summary>
    private async Task<string> GiveManageAsync(TestScope s, string userId)
    {
        var (_, error) = await s.UserMgmt.GrantPermissionAsync(
            userId, PermissionCodes.PermissionsManage, "подготовка прогона");

        Assert.Null(error);
        return userId;
    }

    [Fact]
    public async Task DenyThroughService_IsVisibleToNewScope_WithoutManualCacheReset()
    {
        // 1. Решение разрешает — операция проходит, кэш наполняется.
        await using (var before = _fixture.CreateScope())
        {
            before.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            await GiveManageAsync(before, _users["owner"]);

            Assert.True(await before.Permissions.HasPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage));
        }

        // 2. Решение меняется ЧЕРЕЗ СЕРВИС. Тест кэш вручную не трогает: если сервис
        //    его не сбросит, новая сессия увидит прежнее решение и тест упадёт.
        await using (var change = _fixture.CreateScope())
        {
            change.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (_, error) = await change.UserMgmt.DenyPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage, "проверка распространения");

            Assert.Null(error);
        }

        // 3. Новая сессия видит запрет и на состоянии прав, и на самой операции.
        await using (var after = _fixture.CreateScope())
        {
            // Актор в этой сессии - тот же, что и раньше: проверяется кэш прав
            // целевого пользователя, а не доступ самого проверяющего.
            after.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            Assert.False(await after.Permissions.HasPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage));

            var dto = await after.UserMgmt.GetEffectivePermissionsAsync(_users["owner"]);
            Assert.NotNull(dto);

            var perm = Assert.Single(dto!.Permissions.Where(p => p.Code == PermissionCodes.PermissionsManage));
            Assert.False(perm.OverrideIsGranted);
            Assert.False(perm.IsEffective);

            // И та же операция от лица целевого пользователя отклоняется сервером.
            after.User.CurrentUserId = Guid.Parse(_users["owner"]);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => after.UserMgmt.GetOverridesAsync(_users["owner"]));
        }
    }

    [Fact]
    public async Task RevokeThroughService_RestoresRoleAccessInNewScope()
    {
        // Снятие решения удаляет запись и возвращает доступ по роли. У NormAdmin
        // Permissions.Manage в роли нет, поэтому после снятия доступа не будет —
        // и это как раз и наблюдается.
        await using (var prepare = _fixture.CreateScope())
        {
            prepare.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            await GiveManageAsync(prepare, _users["owner"]);
        }

        await using (var revoke = _fixture.CreateScope())
        {
            revoke.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (_, error) = await revoke.UserMgmt.RevokePermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage);
            Assert.Null(error);
        }

        await using (var after = _fixture.CreateScope())
        {
            Assert.False(await after.Permissions.HasPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage));

            var row = await after.Db.UserPermissionOverrides
                .AsNoTracking()
                .AnyAsync(o => o.UserId == _users["owner"]
                            && o.PermissionCode == PermissionCodes.PermissionsManage);

            Assert.False(row);
        }
    }

    [Fact]
    public async Task GrantThenDenyThenGrant_TracksEveryChange()
    {
        await using (var step = _fixture.CreateScope())
        {
            step.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            var userId = _users["owner"];

            var (_, e1) = await step.UserMgmt.GrantPermissionAsync(
                userId, PermissionCodes.PermissionsManage, "первое");
            Assert.Null(e1);
            Assert.True(await step.Permissions.HasPermissionAsync(userId, PermissionCodes.PermissionsManage));

            var (_, e2) = await step.UserMgmt.DenyPermissionAsync(
                userId, PermissionCodes.PermissionsManage, "второе");
            Assert.Null(e2);
            Assert.False(await step.Permissions.HasPermissionAsync(userId, PermissionCodes.PermissionsManage));

            var (_, e3) = await step.UserMgmt.GrantPermissionAsync(
                userId, PermissionCodes.PermissionsManage, "третье");
            Assert.Null(e3);
            Assert.True(await step.Permissions.HasPermissionAsync(userId, PermissionCodes.PermissionsManage));

            // Запись решения одна, а не три: повторные команды её обновляют.
            var count = await step.Db.UserPermissionOverrides
                .CountAsync(o => o.UserId == userId && o.PermissionCode == PermissionCodes.PermissionsManage);
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task LongLivedScope_SeesDecisionChangeWithoutManualCacheReset()
    {
        // Отличие от прежней проверки: область A не освобождается между
        // вызовами. Прежний тест создавал новую область на каждом шаге, чем
        // доказывал лишь то, что новый scope читает базу. Здесь область,
        // наполнившая кэш, продолжает жить, и решение меняется из независимой
        // области B - так же, как открытая сессия пользователя при чужом
        // изменении прав.
        await using var scopeA = _fixture.CreateScope();
        scopeA.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await GiveManageAsync(scopeA, _users["owner"]);

        // Первый вызов наполняет кэш области A и выполняет защищённую операцию.
        Assert.True(await scopeA.Permissions.HasPermissionAsync(
            _users["owner"], PermissionCodes.PermissionsManage));

        var initial = await scopeA.UserMgmt.GetEffectivePermissionsAsync(_users["owner"]);
        Assert.NotNull(initial);
        Assert.True(initial!.Permissions
            .Single(p => p.Code == PermissionCodes.PermissionsManage).IsEffective);

        // Запрет выполняется из независимой области B.
        await using (var scopeB = _fixture.CreateScope())
        {
            scopeB.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (_, error) = await scopeB.UserMgmt.DenyPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage, "запрет из другой области");

            Assert.Null(error);
        }

        await using (var diag = _fixture.CreateScope())
        {
            var fresh = await diag.Permissions.HasPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage);

            Assert.False(fresh);
        }

        // Область A продолжает жить и не пересоздаёт сервисы. Права обязаны
        // смениться: кэш процесса общий, а сброс выполняет сам сервис.
        Assert.False(await scopeA.Permissions.HasPermissionAsync(
            _users["owner"], PermissionCodes.PermissionsManage));

        var afterDeny = await scopeA.UserMgmt.GetEffectivePermissionsAsync(_users["owner"]);
        var denied = afterDeny!.Permissions
            .Single(p => p.Code == PermissionCodes.PermissionsManage);

        Assert.False(denied.IsEffective);
        Assert.False(denied.OverrideIsGranted);

        // И живая операция из той же области обязана отказать.
        scopeA.User.CurrentUserId = Guid.Parse(_users["owner"]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => scopeA.UserMgmt.GetOverridesAsync(_users["owner"]));

        // Повторное разрешение снова видно в A без пересоздания.
        await using (var scopeC = _fixture.CreateScope())
        {
            scopeC.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (_, error) = await scopeC.UserMgmt.GrantPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage, "повторное разрешение");

            Assert.Null(error);
        }

        Assert.True(await scopeA.Permissions.HasPermissionAsync(
            _users["owner"], PermissionCodes.PermissionsManage));

        // И снятие решения снова видно.
        await using (var scopeD = _fixture.CreateScope())
        {
            scopeD.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (_, error) = await scopeD.UserMgmt.RevokePermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage);

            Assert.Null(error);
        }

        Assert.False(await scopeA.Permissions.HasPermissionAsync(
            _users["owner"], PermissionCodes.PermissionsManage));
    }

    [Fact]
    public async Task StaleClientView_DoesNotAllowServerToPerformForbiddenOperation()
    {
        // Сценарий устаревшей кнопки: страница загружена, когда доступ был, и
        // клиент сохранил флаг «можно». Решение затем изменилось. Сервер обязан
        // отказать, несмотря на устаревшее представление клиента.
        await using (var prepare = _fixture.CreateScope())
        {
            prepare.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            await GiveManageAsync(prepare, _users["owner"]);
        }

        await using (var change = _fixture.CreateScope())
        {
            change.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

            var (_, error) = await change.UserMgmt.DenyPermissionAsync(
                _users["owner"], PermissionCodes.PermissionsManage, "запрет после загрузки страницы");
            Assert.Null(error);
        }

        // Клиент по-прежнему считает операцию доступной.
        await using (var act = _fixture.CreateScope())
        {
            act.User.CurrentUserId = Guid.Parse(_users["owner"]);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => act.UserMgmt.GetOverridesAsync(_users["owner"]));
        }
    }
}