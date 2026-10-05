using System.Net;
using System.Net.Http.Json;
using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Chernika.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Серверный гейт индивидуальных решений по полномочиям: прямые вызовы API.
/// <para>
/// Кнопка в интерфейсе не является защитой. Здесь проверяется, что запрос без
/// права управления полномочиями отклоняется сервером и НЕ пишет в БД, а
/// запрос с этим правом проходит и пишет именно то решение, которое выбрал
/// администратор.
/// </para>
/// <para>
/// Отдельная база фабрики пересоздаётся на каждый прогон, состояние от других
/// классов не зависит.
/// </para>
/// </summary>
[Collection("Database")]
public class UserPermissionHttpGateTests : IClassFixture<ChernikaApiFactory>, IAsyncLifetime
{
    private const string TargetUserName = "http_perm_target";
    private const string ActorUserName = "http_perm_actor";

    private readonly ChernikaApiFactory _factory;
    private string _targetUserId = null!;
    private string _actorUserId = null!;

    public UserPermissionHttpGateTests(ChernikaApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        // Субъект — Guest: у роли нет Reference.View, поэтому разрешение,
        // выданное индивидуально, действительно что-то меняет.
        var target = await EnsureUserAsync(um, TargetUserName, nameof(UserRole.Guest));

        // Актёр — NormAdmin, которому индивидуально выдано Permissions.Manage.
        // В шаблоне роли этого права нет (есть только у SystemAdmin), поэтому
        // право выдаётся записью решения — тем же механизмом, который
        // проверяется в остальных тестах класса.
        var actor = await EnsureUserAsync(um, ActorUserName, nameof(UserRole.NormAdmin));

        var db = sp.GetRequiredService<AppDbContext>();
        db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            PermissionCode = PermissionCodes.PermissionsManage,
            IsGranted = true,
            Reason = "подготовка прогона",
            GrantedByUserId = actor.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        _targetUserId = target.Id;
        _actorUserId = actor.Id;
    }

    public async Task DisposeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        await db.UserPermissionOverrides
            .Where(o => o.UserId == _targetUserId || o.UserId == _actorUserId)
            .ExecuteDeleteAsync();

        foreach (var name in new[] { TargetUserName, ActorUserName })
        {
            var user = await um.FindByNameAsync(name);
            if (user != null)
                await um.DeleteAsync(user);
        }
    }

    private async Task<ApplicationUser> EnsureUserAsync(
        UserManager<ApplicationUser> um, string userName, string role)
    {
        var user = await um.FindByNameAsync(userName);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = userName,
                Email = userName + "@http.test",
                EmailConfirmed = true,
                BranchId = Guid.Parse(_factory.BranchId),
            };
            var created = await um.CreateAsync(user, "Http-Test-Pass-1");
            Assert.True(created.Succeeded,
                string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        if (!await um.IsInRoleAsync(user, role))
        {
            var added = await um.AddToRoleAsync(user, role);
            Assert.True(added.Succeeded,
                string.Join("; ", added.Errors.Select(e => e.Description)));
        }

        return user;
    }

    private async Task<UserPermissionOverride?> OverrideAsync(string code)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UserPermissionOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.UserId == _targetUserId && o.PermissionCode == code);
    }

    // ── Отказ сервера: нет права управления полномочиями ─────────────────

    [Fact]
    public async Task Anonymous_Grant_Returns401_AndWritesNothing()
    {
        var anon = _factory.CreateAnonymous();

        var response = await anon.PostAsJsonAsync(
            $"/api/users/{_targetUserId}/grant",
            new { permissionCode = PermissionCodes.ReferenceView, reason = "аноним" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await OverrideAsync(PermissionCodes.ReferenceView));
    }

    [Fact]
    public async Task UserWithoutPermissionsManage_Grant_Returns403_AndWritesNothing()
    {
        // http_reader — роль Guest. У него нет Permissions.Manage, поэтому
        // политика ManageRoles обязана отклонить запрос ДО сервиса.
        var reader = _factory.CreateAs(_factory.ReadOnlyUserName);

        var response = await reader.PostAsJsonAsync(
            $"/api/users/{_targetUserId}/grant",
            new { permissionCode = PermissionCodes.ReferenceView, reason = "без права" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await OverrideAsync(PermissionCodes.ReferenceView));
    }

    [Fact]
    public async Task UserWithoutPermissionsManage_Revoke_Returns403_AndDecisionStays()
    {
        await SeedDecisionAsync(PermissionCodes.ReferenceView, granted: false);

        var reader = _factory.CreateAs(_factory.ReadOnlyUserName);
        var response = await reader.PostAsync(
            $"/api/users/{_targetUserId}/revoke?code={PermissionCodes.ReferenceView}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Решение осталось на месте: отказ не должен «съесть» запись.
        var row = await OverrideAsync(PermissionCodes.ReferenceView);
        Assert.NotNull(row);
        Assert.False(row!.IsGranted);
    }

    // ── Разрешение сервером: право есть ──────────────────────────────────

    [Fact]
    public async Task WithPermissionsManage_Grant_Returns200_AndWritesChosenDecision()
    {
        var actor = _factory.CreateAs(ActorUserName);

        var response = await actor.PostAsJsonAsync(
            $"/api/users/{_targetUserId}/grant",
            new { permissionCode = PermissionCodes.ReferenceView, reason = "нужно для работы" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await OverrideAsync(PermissionCodes.ReferenceView);
        Assert.NotNull(row);
        Assert.True(row!.IsGranted);
        Assert.Equal("нужно для работы", row.Reason);

        // Ответ сервера — те же effective права, что вернёт потом форма.
        var dto = await response.Content.ReadFromJsonAsync<UserEffectivePermissionsDto>();
        Assert.NotNull(dto);
        var p = Assert.Single(dto!.Permissions.Where(x => x.Code == PermissionCodes.ReferenceView));
        Assert.True(p.IsEffective);
        Assert.True(p.OverrideIsGranted);
    }

    [Fact]
    public async Task WithPermissionsManage_Deny_ChangesDecision_NotCreatesSecondRow()
    {
        await SeedDecisionAsync(PermissionCodes.ReferenceView, granted: true);

        var actor = _factory.CreateAs(ActorUserName);
        var response = await actor.PostAsJsonAsync(
            $"/api/users/{_targetUserId}/deny",
            new { permissionCode = PermissionCodes.ReferenceView, reason = "ограничить" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await OverrideAsync(PermissionCodes.ReferenceView);
        Assert.NotNull(row);
        Assert.False(row!.IsGranted);
        Assert.Equal("ограничить", row.Reason);
    }

    [Fact]
    public async Task WithPermissionsManage_Revoke_DeletesDecision_AndReturnsRoleAccess()
    {
        // Снятие решения — удаление записи, а не запись противоположного.
        // Проверяется тем, что строки в БД не остаётся вовсе.
        await SeedDecisionAsync(PermissionCodes.ReferenceView, granted: false);

        var actor = _factory.CreateAs(ActorUserName);
        var response = await actor.PostAsync(
            $"/api/users/{_targetUserId}/revoke?code={PermissionCodes.ReferenceView}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await OverrideAsync(PermissionCodes.ReferenceView));

        var dto = await response.Content.ReadFromJsonAsync<UserEffectivePermissionsDto>();
        Assert.NotNull(dto);
        var p = Assert.Single(dto!.Permissions.Where(x => x.Code == PermissionCodes.ReferenceView));
        Assert.Null(p.OverrideIsGranted);
        Assert.Equal(p.GrantedByRole, p.IsEffective);
    }

    [Fact]
    public async Task SelfModification_Returns400_AndWritesNothing()
    {
        var actor = _factory.CreateAs(ActorUserName);

        var response = await actor.PostAsJsonAsync(
            $"/api/users/{_actorUserId}/grant",
            new { permissionCode = PermissionCodes.HKView, reason = "себе" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.UserPermissionOverrides
            .AnyAsync(o => o.UserId == _actorUserId && o.PermissionCode == PermissionCodes.HKView));
    }

    [Fact]
    public async Task UnknownPermissionCode_Returns400_AndWritesNothing()
    {
        var actor = _factory.CreateAs(ActorUserName);

        var response = await actor.PostAsJsonAsync(
            $"/api/users/{_targetUserId}/grant",
            new { permissionCode = "HK.NoSuchCode", reason = "нет такого права" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await OverrideAsync("HK.NoSuchCode"));
    }

    // ── Форма получает полный каталог, но активная выдача ИК исключает ───

    [Fact]
    public async Task EffectivePermissions_ContainConservedCode_ButActiveFormExcludesIt()
    {
        var actor = _factory.CreateAs(ActorUserName);

        var response = await actor.GetAsync($"/api/users/{_targetUserId}/effective-permissions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<UserEffectivePermissionsDto>();
        Assert.NotNull(dto);

        // Право ИК в DTO есть — история назначений читается, модель не потеряна…
        Assert.Contains(dto!.Permissions, p => p.Code == PermissionCodes.IndividualCardView);

        // …но в активной форме его нет ни в строках, ни в счётчиках.
        Assert.False(IndividualPermissionDecision.IsActive(PermissionCodes.IndividualCardView));
        Assert.DoesNotContain(
            dto.Permissions.Where(p => IndividualPermissionDecision.IsActive(p.Code)),
            p => p.Code == PermissionCodes.IndividualCardView);

        // Все законсервированные коды приходят в DTO — история читается…
        foreach (var code in PermissionCatalog.ConservedIndividualCardCodes)
            Assert.Contains(dto.Permissions, p => p.Code == code);

        // …и ни один из них не попадает в активную форму ни как строка, ни в
        // счётчики. Каждый такой код обязан быть признан неактивным.
        Assert.All(PermissionCatalog.ConservedIndividualCardCodes,
            code => Assert.False(IndividualPermissionDecision.IsActive(code)));

        var active = dto.Permissions
            .Where(p => IndividualPermissionDecision.IsActive(p.Code))
            .ToList();

        Assert.DoesNotContain(active, p => p.Code == PermissionCodes.IndividualCardView);
        Assert.DoesNotContain(active.Select(p => p.Module),
            m => m == PermissionCatalog.ConservedIndividualCardModule);

        var counters = IndividualPermissionDecision.Count(dto.Permissions, null, null, null);
        Assert.Equal(active.Count, counters.Total);
        Assert.Equal(active.Count(p => p.IsEffective), counters.Granted);
        Assert.Equal(active.Count(p => !p.IsEffective), counters.Denied);
        Assert.Equal(active.Count(p => p.OverrideIsGranted != null), counters.WithOverride);
    }

    private async Task SeedDecisionAsync(string code, bool granted)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = _targetUserId,
            PermissionCode = code,
            IsGranted = granted,
            Reason = "основание из теста",
            GrantedByUserId = _actorUserId,
            CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }
}