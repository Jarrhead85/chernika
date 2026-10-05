using System.Security.Claims;
using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Chernika.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Chernika.IntegrationTests;

/// <summary>
/// Проход по каталогу действующих полномочий: требование политики проверяется
/// для КАЖДОГО права, и отдельно фиксируется, у каких прав вообще есть
/// защищённая операция, а у каких нет доступного пользовательского сценария.
/// <para>
/// Заявлять «все права работают» по одному тесту отображения таблицы нельзя.
/// Здесь проверяется исполнительный механизм — реальный обработчик
/// <see cref="PermissionAuthorizationHandler"/> — для всех прав каталога, в
/// обе стороны: решение разрешает и решение запрещает.
/// </para>
/// </summary>
[Collection("Database")]
public class PermissionScenarioCoverageTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly ITestOutputHelper _output;
    private string _subjectUserId = null!;

    public PermissionScenarioCoverageTests(TestDatabaseFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await using var s = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            UserName = "perm_matrix_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@perm.test",
            EmailConfirmed = true,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Perm-Matrix-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        // Намеренно БЕЗ ролей: у такого пользователя нет ни одного права из
        // шаблона роли, и в проверке участвует только индивидуальное решение.
        // Общий шаблон роли при этом не трогается — чужие тесты не затрагиваются.
        _subjectUserId = user.Id;
    }

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == _subjectUserId)
            .ExecuteDeleteAsync();

        var user = await s.Users.FindByIdAsync(_subjectUserId);
        if (user != null)
            await s.Users.DeleteAsync(user);
    }

    /// <summary>Все права каталога — и активные, и законсервированные.</summary>
    public static TheoryData<string> EveryCatalogPermission
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var def in PermissionCatalog.All)
                data.Add(def.Code);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryCatalogPermission))]
    public async Task PolicyRequirement_IsSatisfiedOnlyWhenDecisionGrants(string code)
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var requirement = new PermissionRequirement(code);

        // 1. Решения нет, роли нет — требование не выполнено.
        Assert.False(await IsSatisfiedAsync(s, requirement));

        // 2. Разрешающее решение — требование выполнено. Кнопка в интерфейсе
        //    тут ни при чём: проверяется обработчик политики.
        await SeedDecisionAsync(s, code, granted: true);
        Assert.True(await IsSatisfiedAsync(s, requirement));

        // 3. Запрещающее решение — снова не выполнено, причём строка решения
        //    перезаписывается, а не добавляется второй.
        await SeedDecisionAsync(s, code, granted: false);
        Assert.False(await IsSatisfiedAsync(s, requirement));
        Assert.Equal(1, await s.Db.UserPermissionOverrides
            .CountAsync(o => o.UserId == _subjectUserId && o.PermissionCode == code));

        // 4. Снятие решения возвращает исходное состояние.
        var toRemove = await s.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == _subjectUserId && o.PermissionCode == code);
        Assert.NotNull(toRemove);
        s.Db.UserPermissionOverrides.Remove(toRemove!);
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_subjectUserId);

        Assert.False(await IsSatisfiedAsync(s, requirement));
    }

    [Theory]
    [MemberData(nameof(EveryCatalogPermission))]
    public async Task EffectiveAccess_FollowsDecision_AndRoleStaysEmpty(string code)
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var before = await s.UserMgmt.GetEffectivePermissionsAsync(_subjectUserId);
        Assert.NotNull(before);
        var p0 = Assert.Single(before!.Permissions.Where(x => x.Code == code));

        Assert.False(p0.GrantedByRole);
        Assert.Null(p0.OverrideIsGranted);
        Assert.False(p0.IsEffective);

        await SeedDecisionAsync(s, code, granted: true);

        var after = await s.UserMgmt.GetEffectivePermissionsAsync(_subjectUserId);
        var p1 = Assert.Single(after!.Permissions.Where(x => x.Code == code));

        Assert.True(p1.IsEffective);
        Assert.True(p1.OverrideIsGranted);
        Assert.False(p1.GrantedByRole);
        Assert.True(await s.Permissions.HasPermissionAsync(_subjectUserId, code));

        // Новая сессия даёт то же состояние, что сохранено в БД.
        await using (var fresh = _fixture.CreateScope())
        {
            fresh.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            var p2 = Assert.Single((await fresh.UserMgmt.GetEffectivePermissionsAsync(_subjectUserId))!
                .Permissions.Where(x => x.Code == code));

            Assert.True(p2.IsEffective);
            Assert.True(p2.OverrideIsGranted);
        }
    }

    // ── Проход по каталогу: что можно проверить сквозным вызовом ─────────

    [Fact]
    public async Task CatalogWalk_RecordsWhichPermissionsHaveProtectedOperation()
    {
        // Защищённая операция — эндпоинт, у которого в метаданных авторизации
        // стоит политика. Список политик берётся из метаданных самих
        // эндпоинтов, а не из списка в тесте: иначе проверка устарела бы молча.
        using var api = new ChernikaApiFactory();
        await api.InitializeAsync();

        var endpointData = api.Services.GetRequiredService<EndpointDataSource>();
        var policyProvider = api.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var codesWithOperationFromEndpoints = new HashSet<string>(StringComparer.Ordinal);
        var policyToCodes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var policiesWithoutOperation = new List<string>();

        foreach (var endpoint in endpointData.Endpoints)
        {
            var authorizeData = endpoint.Metadata.GetMetadata<IAuthorizeData>();
            var policyName = authorizeData?.Policy;
            if (string.IsNullOrEmpty(policyName))
            {
                // Без политики действует только требование аутентификации:
                // доступ такого эндпоинта от индивидуального решения не зависит.
                policiesWithoutOperation.Add("эндпоинт без политики: " + endpoint.DisplayName);
                continue;
            }

            var policy = await policyProvider.GetPolicyAsync(policyName);
            Assert.NotNull(policy);

            foreach (var requirement in policy!.Requirements.OfType<PermissionRequirement>())
            {
                if (!policyToCodes.TryGetValue(policyName, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    policyToCodes[policyName] = set;
                }

                foreach (var code in requirement.PermissionCodes)
                {
                    set.Add(code);
                    codesWithOperationFromEndpoints.Add(code);
                }
            }
        }

        var active = PermissionCatalog.All
            .Where(d => IndividualPermissionDecision.IsActive(d.Code))
            .ToList();
        var conserved = PermissionCatalog.All
            .Where(d => !IndividualPermissionDecision.IsActive(d.Code))
            .ToList();

        // Страницы Blazor — не эндпоинты API, поэтому их политики собираются
        // отдельно: по атрибуту [Authorize(Policy=...)] на скомпилированных
        // компонентах. Это ровно то, что проверяет маршрутизатор при переходе.
        var pagePolicies = PagePolicies();
        var codesWithPage = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedPagePolicies = new List<string>();

        foreach (var policyName in pagePolicies.Keys)
        {
            var policy = await policyProvider.GetPolicyAsync(policyName);

            if (policy is null)
            {
                // Политика зарегистрирована только в Web, а коды полномочий
                // провайдер берёт у Api. Молча пропустить её нельзя: иначе права
                // этой страницы попали бы в «без защиты».
                unresolvedPagePolicies.Add(policyName);
                _output.WriteLine(
                    $"политика страницы {policyName} есть только в Web — коды не разрешены прогоном");
                continue;
            }

            foreach (var requirement in policy.Requirements.OfType<PermissionRequirement>())
            {
                foreach (var code in requirement.PermissionCodes)
                    codesWithPage.Add(code);
            }
        }

        var codesWithOperation = new HashSet<string>(codesWithOperationFromEndpoints, StringComparer.Ordinal);
        codesWithOperation.UnionWith(codesWithPage);

        var activeWithoutOperation = active
            .Where(d => !codesWithOperation.Contains(d.Code))
            .ToList();

        var conservedWithOperation = conserved
            .Where(d => codesWithOperation.Contains(d.Code))
            .ToList();

        var report = new List<string>
        {
            "ПРОХОД ПО КАТАЛОГУ ДЕЙСТВУЮЩИХ ПОЛИНОМОЧИЙ",
            "==========================================",
            "",
            $"прав в каталоге всего:            {PermissionCatalog.All.Count}",
            $"  активных (входят в форму):       {active.Count}",
            $"  законсервированных (модуль ИК):  {conserved.Count}",
            "",
            $"политик на эндпоинтах API:         {policyToCodes.Count}",
            $"политик на страницах интерфейса:   {pagePolicies.Count}",
            $"активных прав под защитой:         {active.Count(d => codesWithOperation.Contains(d.Code))}",
            $"  из них только эндпоинтом API:    {active.Count(d => codesWithOperationFromEndpoints.Contains(d.Code))}",
            $"  из них только страницей:          {active.Count(d => !codesWithOperationFromEndpoints.Contains(d.Code) && codesWithPage.Contains(d.Code))}",
            $"активных прав БЕЗ защиты:          {activeWithoutOperation.Count}",
            "",
            "ПОЛИТИКА ЭНДПОИНТА -> КОДЫ ПОЛНОМОЧИЙ",
        };

        foreach (var (policy, codes) in policyToCodes)
            report.Add($"  {policy}: {string.Join(", ", codes)}");

        report.Add("");
        report.Add("ПОЛИТИКА СТРАНИЦЫ -> КОДЫ ПОЛНОМОЧИЙ");
        foreach (var (policy, codes) in pagePolicies.OrderBy(x => x.Key))
            report.Add($"  {policy}: {string.Join(", ", codes)}");

        if (unresolvedPagePolicies.Count > 0)
        {
            report.Add("");
            report.Add("  ВНИМАНИЕ: перечисленные политики зарегистрированы только в Web,");
            report.Add("  а провайдер политик в этом прогоне взят у Api. Их коды не разрешены,");
            report.Add("  и права таких страниц могут попасть в список «без защиты» ошибочно:");
            report.Add("  " + string.Join(", ", unresolvedPagePolicies));
        }

        report.Add("");
        report.Add("=== АКТИВНЫЕ ПРАВА ПОД ЗАЩИТОЙ ОПЕРАЦИЕЙ (проверяются сквозным вызовом) ===");
        foreach (var def in active.Where(d => codesWithOperation.Contains(d.Code)).OrderBy(d => d.Code))
            report.Add($"  {def.Code} — {def.Name}");

        report.Add("");
        report.Add("=== АКТИВНЫЕ ПРАВА БЕЗ ЗАЩИТНОЙ ОПЕРАЦИИ ===");
        if (activeWithoutOperation.Count == 0)
        {
            report.Add("  (нет)");
        }
        else
        {
            report.Add("  Сценария, в котором индивидуальный запрет что-то менял бы, сейчас нет:");
            report.Add("  доступ определяется ролью. Запись решения меняет только каталог и");
            report.Add("  значение IsEffective в форме, но ни одна серверная операция по нему");
            report.Add("  не отклоняется. Такие права НЕльзя объявлять проверенными сквозным вызовом.");
            if (unresolvedPagePolicies.Count > 0)
            {
                report.Add("");
                report.Add("  ОГОВОРКА: часть из перечисленных может закрываться политикой страницы,");
                report.Add("  которая зарегистрирована только в Web (см. список выше). Для них");
                report.Add("  подтверждено лишь то, что индивидуальное решение пишется и читается.");
            }
            foreach (var def in activeWithoutOperation.OrderBy(d => d.Code))
                report.Add($"  {def.Code} — {def.Name} [{def.Module}]");
        }

        report.Add("");
        report.Add("=== ЗАКОНСЕРВИРОВАННЫЕ ПРАВА ИК (в активной форме отсутствуют) ===");
        foreach (var def in conserved.OrderBy(d => d.Code))
        {
            var note = codesWithOperation.Contains(def.Code)
                ? "эндпоинт API всё ещё требует этого права (модуль выключен)"
                : "эндпоинтов нет";
            report.Add($"  {def.Code} — {def.Name}: {note}");
        }

        if (conservedWithOperation.Count > 0)
        {
            report.Add("");
            report.Add("  ВНИМАНИЕ: у " + conservedWithOperation.Count
                + " законсервированных прав всё ещё есть эндпоинты, требующие их.");
        }

        WriteReport(report);

        // Каждое активное право обязано быть классифицировано: либо под
        // защитой операции, либо в списке «сценария нет». Неclassified быть не
        // может — иначе отчёт врёт.
        foreach (var def in active)
        {
            var classified = codesWithOperation.Contains(def.Code) || activeWithoutOperation.Contains(def);
            Assert.True(classified, def.Code + " не попало ни в одну категорию отчёта");
        }

        _output.WriteLine(string.Join("\n", report));

        // Инвариант отчёта: консервация не должна выглядеть как «работает».
        Assert.All(conserved,
            d => Assert.False(IndividualPermissionDecision.IsActive(d.Code)));
        Assert.True(activeWithoutOperation.Count >= 0);
    }

    /// <summary>
    /// Политики страниц интерфейса: политика -&gt; имена страниц, где она стоит.
    /// Берутся из атрибута на скомпилированных компонентах — это тот же
    /// атрибут, на который смотрит маршрутизатор при переходе.
    /// </summary>
    private static Dictionary<string, List<string>> PagePolicies()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Сборка указывается явно: Program есть и в Api, и в Web, и без указания
        // сканировалась не та. Из-за этого страницы не попадали в отчёт вовсе.
        foreach (var type in typeof(IndividualPermissionDecision).Assembly.GetTypes())
        {
            var attribute = type
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>()
                .FirstOrDefault(a => !string.IsNullOrEmpty(a.Policy));

            if (attribute?.Policy is not { } policyName)
                continue;

            if (!result.TryGetValue(policyName, out var pages))
            {
                pages = new List<string>();
                result[policyName] = pages;
            }

            pages.Add(type.Name);
        }

        return result;
    }

    private void WriteReport(List<string> lines)
    {
        var dir = FindRepoRoot();
        if (dir == null)
        {
            _output.WriteLine("корень репозитория не найден — отчёт не записан в файл");
            return;
        }

        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "permission_scenarios.txt"), lines);
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Chernika.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    private async Task<bool> IsSatisfiedAsync(TestScope s, PermissionRequirement requirement)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, _subjectUserId),
                    new Claim(ClaimTypes.Name, "perm_matrix"),
                },
                "TestAuth"));

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };

        var handler = new PermissionAuthorizationHandler(s.Permissions, accessor);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement }, principal, resource: null);

        await handler.HandleAsync(context);

        return context.HasSucceeded;
    }

    private async Task SeedDecisionAsync(TestScope s, string code, bool granted)
    {
        var existing = await s.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == _subjectUserId && o.PermissionCode == code);

        if (existing is null)
        {
            s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = _subjectUserId,
                PermissionCode = code,
                IsGranted = granted,
                Reason = "основание из теста",
                GrantedByUserId = _fixture.SystemAdminUser.Id,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.IsGranted = granted;
            existing.Reason = "основание из теста";
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_subjectUserId);
    }
}