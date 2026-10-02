using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Chernika.Domain;
using Chernika.Domain.Enums;
using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chernika.IntegrationTests;

/// <summary>
/// Тестовый хост реального приложения <c>Chernika.Api</c> для HTTP-проверок.
/// <para>
/// Что здесь НАСТОЯЩЕЕ, а что подменено:
/// </para>
/// <list type="bullet">
/// <item><b>Настоящие</b>: маршрутизация MVC, контроллеры, контракты, политики
/// авторизации, <c>IPermissionService</c> со всеми проверками прав в сервисах,
/// конвейер middleware, привязка моделей, сериализация JSON, сама БД и её
/// миграции и ограничения.</item>
/// <item><b>Подменено ровно одно</b> — способ аутентификации: вместо входа
/// через cookie Identity тест предъявляет имя пользователя, а обработчик строит
/// ClaimsPrincipal по РЕАЛЬНОЙ учётной записи из БД. Права не выдаются и не
/// подменяются: их вычисляет тот же <c>IPermissionService</c> по тем же
/// назначениям ролей, что и в бою. Иначе проверка «пользователь без
/// Reference.Edit не может писать» была бы тавтологией.</item>
/// </list>
/// <para>
/// Отдельная база данных. Write-тесты физически не могут задеть рабочую
/// <c>chernika</c>: строки подключения различаются именем базы, и фабрика
/// откажется стартовать, если это не так.
/// </para>
/// </summary>
public sealed class ChernikaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Имя БД HTTP-тестов. НЕ совпадает с рабочей и НЕ совпадает с
    /// общей тестовой: тесты должны быть изолированы и друг от друга, и от БД,
    /// в которой работает приложение.</summary>
    public const string HttpDbName = "chernika_http_test";

    private const string ConnectionStringTemplate =
        "Host=localhost;Port=5432;Database={0};Username=postgres;Password=qwerty12345;Pooling=false";

    public static string HttpConnectionString => string.Format(ConnectionStringTemplate, HttpDbName);

    public string SystemAdminUserName { get; private set; } = null!;
    public string NormAdminUserName { get; private set; } = null!;
    public string ReadOnlyUserName { get; private set; } = null!;
    public string BranchId { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Строгая охрана: запуск против рабочей БД из этих тестов недопустим.
        var incoming = builder.GetSetting("ConnectionStrings:DefaultConnection");
        if (!string.IsNullOrEmpty(incoming) && !incoming.Contains(HttpDbName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "HTTP-тесты не должны работать против БД '" + HttpDbName + "'. "
                + "В конфигурации передана: " + incoming);
        }

        builder.UseSetting("ConnectionStrings:DefaultConnection", HttpConnectionString);
        builder.UseSetting("IndividualCards:Enabled", "false");
        builder.UseEnvironment("Development");

        builder.ConfigureLogging(l => l.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                    options.DefaultChallengeScheme = TestAuthHandler.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });

            // Модуль ИК выключен настройкой, и состояние берётся из неё же.
            // Подменять IIndividualCardModuleState нельзя: граница проверяется
            // именно тем, что её видит приложение.
        });
    }

    /// <summary>Клиент без заголовка пользователя: аноним.</summary>
    public HttpClient CreateAnonymous() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    /// <summary>
    /// Клиент от имени реального пользователя. Права не выдаются: запрос
    /// проходит штатную проверку политик и сервисов.
    /// </summary>
    public HttpClient CreateAs(string userName)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userName);
        return client;
    }

    public async Task InitializeAsync()
    {
        // База пересоздаётся целиком: состояние прогона не должно зависеть от
        // того, что осталось от предыдущего. Иначе «ошибка миграции» и «остатки
        // данных» выглядели бы одинаково.
        await RecreateDatabaseAsync();

        await using var scope = Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var rm = sp.GetRequiredService<RoleManager<IdentityRole>>();

        // Роли и назначения прав — настоящие, из SecuritySeedService. Именно их
        // потом вычисляет IPermissionService в проверяемых запросах.
        await SecuritySeedService.SeedAsync(db, um, rm);

        var branch = new Branch
        {
            Id = Guid.NewGuid(),
            Name = "Филиал HTTP-тестов",
            Code = "HT-" + Guid.NewGuid().ToString("N")[..4],
        };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        BranchId = branch.Id.ToString();

        SystemAdminUserName = await EnsureUserAsync(um, "http_sysadmin", nameof(UserRole.SystemAdmin));
        NormAdminUserName = await EnsureUserAsync(um, "http_normadmin", nameof(UserRole.NormAdmin));

        // Пользователь только для чтения: роль без Reference.Edit. Создаётся
        // ОТДЕЛЬНО, а не «сниманием» прав у существующего, чтобы проверка
        // запрета записи опиралась на настоящую роль, а не на подкрученное
        // индивидуальное решение.
        await EnsureReadOnlyUserAsync(um);
    }

    /// <summary>
    /// Пересоздание базы. Соединение с самой фабрики здесь не годится: хост ещё
    /// поднимается, и держать открытое соединение к удаляемой базе рискованно.
    /// </summary>
    private static async Task RecreateDatabaseAsync()
    {
        var adminConnection =
            "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=qwerty12345";

        await using var connection = new Npgsql.NpgsqlConnection(adminConnection);
        await connection.OpenAsync();

        await using (var drop = new Npgsql.NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{HttpDbName}\" WITH (FORCE)", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using var create = new Npgsql.NpgsqlCommand(
            $"CREATE DATABASE \"{HttpDbName}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    public new Task DisposeAsync()
    {
        base.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> EnsureUserAsync(
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
                BranchId = Guid.Parse(BranchId),
            };
            var created = await um.CreateAsync(user, "Http-Test-Pass-1");
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    "не удалось создать тестового пользователя: "
                    + string.Join("; ", created.Errors.Select(e => e.Description)));
            }
        }

        if (!await um.IsInRoleAsync(user, role))
        {
            var added = await um.AddToRoleAsync(user, role);
            if (!added.Succeeded)
            {
                throw new InvalidOperationException(
                    "не удалось выдать роль: " + string.Join("; ", added.Errors.Select(e => e.Description)));
            }
        }

        return userName;
    }

    private async Task EnsureReadOnlyUserAsync(UserManager<ApplicationUser> um)
    {
        ReadOnlyUserName = "http_reader";

        // Роли из SecuritySeedService: у Guest есть Reference.View и НЕТ
        // Reference.Edit — ровно то разделение, которое проверяется.
        var guest = await um.FindByNameAsync(ReadOnlyUserName);
        if (guest is null)
        {
            guest = new ApplicationUser
            {
                UserName = ReadOnlyUserName,
                Email = ReadOnlyUserName + "@http.test",
                EmailConfirmed = true,
                BranchId = Guid.Parse(BranchId),
            };
            var created = await um.CreateAsync(guest, "Http-Test-Pass-1");
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    "не удалось создать пользователя только для чтения: "
                    + string.Join("; ", created.Errors.Select(e => e.Description)));
            }
        }

        if (!await um.IsInRoleAsync(guest, nameof(UserRole.Guest)))
        {
            var added = await um.AddToRoleAsync(guest, nameof(UserRole.Guest));
            if (!added.Succeeded)
            {
                throw new InvalidOperationException(
                    "не удалось выдать роль читателя: "
                    + string.Join("; ", added.Errors.Select(e => e.Description)));
            }
        }
    }
}

/// <summary>
/// Аутентификация тестового клиента по имени пользователя.
/// <para>
/// Подменяет ТОЛЬКО аутентификацию. Никаких прав не выдаёт: обработчик лишь
/// находит реальную учётную запись и строит principal с её настоящим
/// <c>NameIdentifier</c>. Дальше работает штатный
/// <c>PermissionAuthorizationHandler</c> и штатные проверки прав в сервисах.
/// </para>
/// <para>
/// Если заголовка нет — principal анонимный, и защищённый маршрут должен
/// ответить 401. Это тоже проверяется, а не обходится.
/// </para>
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "TestAuth";
    public const string UserHeader = "X-Test-User";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var values))
        {
            // НЕ Success с пустым principal: тогда политика `[Authorize]` решит,
            // что principal «анонимный, но аутентифицированный», и защищённый
            // маршрут ответит 403 вместо 401. NoResult заставляет конвейер
            // вызвать Challenge — то есть спросить credentials.
            return AuthenticateResult.NoResult();
        }

        var userName = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userName))
            return AuthenticateResult.NoResult();

        var users = Context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(userName);
        if (user is null)
        {
            // Неизвестный пользователь обязан остаться неаутентифицированным:
            // тест не должен проходить под вымышленной учётной записью.
            return AuthenticateResult.Fail("Пользователь не найден.");
        }

        var identity = new ClaimsIdentity(Scheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName ?? userName));

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}
