using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Регресс: чтение и запись прав, пользователей, журнала аудита и выгрузки
/// держались только на политиках хоста, а в сервисах проверок не было.
/// <para>
/// Каждая операция проверяется в обе стороны: отказ при запрете и проход при
/// разрешении. Роли подобраны так, чтобы запрет или разрешение действительно
/// что-то меняли: NormAdmin получает нужные права из роли, Guest — нет.
/// </para>
/// </summary>
[Collection("Database")]
public class ServiceLevelPermissionGuardTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly Dictionary<string, string> _users = new(StringComparer.Ordinal);

    public ServiceLevelPermissionGuardTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var s = _fixture.CreateScope();

        await CreateUserAsync(s, "admin", nameof(UserRole.NormAdmin));
        await CreateUserAsync(s, "plain", nameof(UserRole.Guest));
    }

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var ids = _users.Values.ToList();

        await s.Db.UserPermissionOverrides.Where(o => ids.Contains(o.UserId)).ExecuteDeleteAsync();
        await s.Db.AuditLogs.Where(l => ids.Contains(l.UserId.ToString())).ExecuteDeleteAsync();

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
            UserName = "svcperm_" + key + "_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@svcperm.test",
            EmailConfirmed = true,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Svc-Perm-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);

        _users[key] = user.Id;
    }

    private async Task SetDecisionAsync(TestScope s, string key, string code, bool granted)
    {
        var existing = await s.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == _users[key] && o.PermissionCode == code);

        if (existing is null)
        {
            s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = _users[key],
                PermissionCode = code,
                IsGranted = granted,
                Reason = "регресс-тест сервисных проверок",
                GrantedByUserId = _fixture.SystemAdminUser.Id,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            // Решение меняется, а не добавляется вторым: повторная вставка того же
            // кода для того же пользователя нарушает уникальный индекс.
            existing.IsGranted = granted;
            existing.Reason = "регресс-тест сервисных проверок";
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_users[key]);
    }

    // ── Пользователи: раньше в сервисе не было ни одной проверки ──────────

    [Fact]
    public async Task GetUsers_IndividualDenyOfUsersManage_Refuses()
    {
        await using var s = _fixture.CreateScope();

        await SetDecisionAsync(s, "admin", PermissionCodes.UsersManage, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["admin"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.UserMgmt.GetUsersAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.UserMgmt.GetUsersCountAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.UserMgmt.GetBranchesAsync());
    }

    [Fact]
    public async Task CreateUser_IndividualDenyOfUsersManage_Refuses_AndWritesNothing()
    {
        await using var s = _fixture.CreateScope();

        var name = "svcperm_new_" + Guid.NewGuid().ToString("N")[..6];

        await SetDecisionAsync(s, "admin", PermissionCodes.UsersManage, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["admin"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.UserMgmt.CreateUserAsync(name, "Svc@12345", "Тест", "Тест", nameof(UserRole.Guest)));

        Assert.Null(await s.Users.FindByNameAsync(name));
    }

    [Fact]
    public async Task Users_AllowedByRole_Pass()
    {
        await using var s = _fixture.CreateScope();

        // Существенный факт о сидинге: права Users.Manage и Permissions.Manage
        // НЕ входят в шаблон NormAdmin — они есть только у SystemAdmin, которому
        // выдаётся весь набор. Поэтому «проход по роли» здесь возможен лишь под
        // системным администратором, а для обычной роли доступ даёт индивидуальное
        // решение. Раньше об этом было неизвестно: проверки не было вовсе.
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        Assert.NotNull(await s.UserMgmt.GetUsersAsync());
        Assert.NotNull(await s.UserMgmt.GetBranchesAsync());
    }

    [Fact]
    public async Task Users_IndividualGrantWithoutRole_Pass()
    {
        await using var s = _fixture.CreateScope();

        // Роль Guest не даёт Users.Manage — выдаём индивидуально.
        await SetDecisionAsync(s, "plain", PermissionCodes.UsersManage, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["plain"]);

        Assert.NotNull(await s.UserMgmt.GetUsersAsync());
    }

    [Fact]
    public async Task GrantPermission_IndividualDenyOfPermissionsManage_Refuses()
    {
        await using var s = _fixture.CreateScope();

        await SetDecisionAsync(s, "admin", PermissionCodes.PermissionsManage, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["admin"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.UserMgmt.GrantPermissionAsync(_users["plain"], PermissionCodes.ReferenceView, "нельзя"));

        Assert.Null(await s.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == _users["plain"] && o.PermissionCode == PermissionCodes.ReferenceView));
    }

    [Fact]
    public async Task GetEffectivePermissions_AcceptsEitherManageRight()
    {
        await using var s = _fixture.CreateScope();

        // Читать права можно и по Users.Manage (форма), и по Permissions.Manage
        // (API). Ни одного из двух прав быть не должно.
        await SetDecisionAsync(s, "plain", PermissionCodes.UsersManage, granted: false);
        await SetDecisionAsync(s, "plain", PermissionCodes.PermissionsManage, granted: false);

        s.User.CurrentUserId = Guid.Parse(_users["plain"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.UserMgmt.GetEffectivePermissionsAsync(_users["admin"]));

        // Право появляется — доступ открывается без смены роли.
        await SetDecisionAsync(s, "plain", PermissionCodes.PermissionsManage, granted: true);
        s.Permissions.InvalidateCache(_users["plain"]);

        Assert.NotNull(await s.UserMgmt.GetEffectivePermissionsAsync(_users["admin"]));
    }

    // ── Журнал аудита: чтение требует Audit.View, запись — нет ───────────

    [Fact]
    public async Task AuditReads_IndividualDenyOfAuditView_Refuse()
    {
        await using var s = _fixture.CreateScope();

        await SetDecisionAsync(s, "admin", PermissionCodes.AuditView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["admin"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Audit.GetLogsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Audit.GetTotalCountAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Audit.GetLogsWithEntityNamesAsync());
    }

    [Fact]
    public async Task AuditReads_AllowedByRole_Pass()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_users["admin"]);

        Assert.NotNull(await s.Audit.GetLogsAsync());
        Assert.True(await s.Audit.GetTotalCountAsync() >= 0);
    }

    [Fact]
    public async Task AuditWrite_DoesNotRequireAuditView()
    {
        await using var s = _fixture.CreateScope();

        // Запись аудита — часть бизнес-операции, её вызывают и фоновые пути, где
        // пользователя нет. Право просмотра журнала её не должно ломать.
        await SetDecisionAsync(s, "admin", PermissionCodes.AuditView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["admin"]);

        var log = await s.Audit.LogAsync(new AuditWriteRequest(
            "RegressionCheck", Guid.NewGuid().ToString(), "Written",
            Guid.Parse(_users["admin"]), Details: "проверка записи без права просмотра"));

        Assert.NotNull(log);
    }

    // ── Выгрузка: раньше печать шла под ViewHK ──────────────────────────

    [Fact]
    public async Task ReportExport_IndividualDeny_Refuses()
    {
        await using var s = _fixture.CreateScope();

        // Узел создаётся здесь: общая фикстура справочников не засевает.
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "SVC-ND-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Узел проверки выгрузки",
        };

        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "SVC-RPT-" + Guid.NewGuid().ToString("N")[..8],
            Version = "1",
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            BranchId = _fixture.BranchA,
            Status = HKCardStatus.Approved,
            EffectiveDate = DateTime.UtcNow.AddDays(-10),
        };

        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        try
        {
            await SetDecisionAsync(s, "admin", PermissionCodes.ReportExport, granted: false);
            s.User.CurrentUserId = Guid.Parse(_users["admin"]);

            // Раньше печать шла под ViewHK, и запрет Report.Export не оставлял
            // следа: форму можно было выгрузить.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => s.Reports.GenerateHKCardPdfAsync(card));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => s.Reports.GenerateHKRegistryExcelAsync(new List<HKCard> { card }));

            // Контроль: с разрешением те же операции проходят.
            await SetDecisionAsync(s, "admin", PermissionCodes.ReportExport, granted: true);
            s.Permissions.InvalidateCache(_users["admin"]);

            Assert.NotEmpty(await s.Reports.GenerateHKCardPdfAsync(card));
            Assert.NotEmpty(await s.Reports.GenerateHKRegistryExcelAsync(new List<HKCard> { card }));
        }
        finally
        {
            await using var cleanup = _fixture.CreateScope();
            await cleanup.Db.HKCardStatusLogs.Where(l => l.HKCardId == card.Id).ExecuteDeleteAsync();
            await cleanup.Db.HKCards.Where(c => c.Id == card.Id).ExecuteDeleteAsync();
            await cleanup.Db.Nodes.Where(n => n.Id == node.Id).ExecuteDeleteAsync();
        }
    }
}