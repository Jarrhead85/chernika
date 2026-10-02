using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Единственный владелец фоновой обработки сроков ХК.
/// <para>
/// Штатный запуск приложения — <c>Chernika.Web</c> (см. <c>start_app.cmd</c>).
/// Раньше worker был зарегистрирован только в <c>Chernika.Api</c>, поэтому при
/// штатном запуске обработка сроков не выполнялась вообще, а при запуске обоих
/// хостов выполнялась бы дважды.
/// </para>
/// <para>
/// Проверки идут на НАСТОЯЩЕЙ конфигурации и НАСТОЯЩИХ хостах, а не на подставной.
/// Иначе утверждение «владелец ровно один» ничего бы не доказывало: можно было бы
/// подсунуть удобную конфигурацию и «подтвердить» что угодно.
/// </para>
/// <para>
/// Чего эти тесты НЕ доказывают: что два экземпляра ОДНОГО хоста не выполнят
/// прогон дважды. Это проверяется на уровне базы отдельно — advisory lock в
/// <see cref="HKExpirationBoundaryTests.ProcessAsync_SkipsRun_WhenLockHeldByAnotherInstance"/>.
/// Поднять два Web-хоста в одном тесте и утверждать, что они не совпали по
/// времени, значило бы проверять совпадение, а не защиту.
/// </para>
/// </summary>
public class HKExpirationWorkerOwnershipTests
{
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string HostDirectory(string project) =>
        Path.Combine(RepositoryRoot(), "src", project);

    private static IConfiguration HostConfiguration(string project) =>
        new ConfigurationBuilder()
            .SetBasePath(HostDirectory(project))
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    private const string OwnershipLog = "владелец =";
    private const string StartupRunLog = "Стартовый прогон обработки сроков ХК";

    // ── Ровно один владелец, объявленный данными ───────────────────────────

    [Fact]
    public void ExactlyOneHost_DeclaresItselfOwnerOfTheWorker()
    {
        var owners = new[] { "Chernika.Web", "Chernika.Api" }
            .Where(host => HostConfiguration(host).GetValue<bool>("HKExpiration:WorkerEnabled"))
            .ToArray();

        Assert.True(owners.Length == 1,
            "владельцев обработки сроков должно быть ровно один, а их: "
            + (owners.Length == 0 ? "ни одного" : string.Join(", ", owners)));
        Assert.Equal("Chernika.Web", owners[0]);
    }

    [Fact]
    public void HostConfiguration_KeepsThresholdsUnchanged()
    {
        // Владельцем меняется только регистрация worker'а. Пороги и правила
        // обработки не переписываются: иначе смена владельца тихо изменила бы
        // поведение обработки сроков.
        var options = new HKExpirationOptions();
        HostConfiguration("Chernika.Web").GetSection("HKExpiration").Bind(options);

        Assert.Equal(new[] { 90, 30, 7 }, options.WarningDays);
        Assert.Equal("01:00", options.DailyRunTimeUtc);
        Assert.Equal(14, options.ReviewTaskDueDays);
        Assert.True(options.RunOnStartup);
    }

    [Fact]
    public void Registration_FollowsConfiguration_NotTheProject()
    {
        // Оба хоста вызывают одно и то же расширение, различие только в данных.
        // Поэтому «случайно вернули AddHostedService в API» проверяется здесь.
        var apiServices = BuildProvider("Chernika.Api");
        Assert.Empty(apiServices.GetServices<IHostedService>());
        Assert.False(apiServices.GetRequiredService<HKExpirationWorkerOwnership>().IsOwner);

        var webServices = BuildProvider("Chernika.Web");
        Assert.Single(webServices.GetServices<IHostedService>());
        Assert.True(webServices.GetRequiredService<HKExpirationWorkerOwnership>().IsOwner);
    }

    [Fact]
    public void ApiSource_NoLongerRegistersWorkerDirectly()
    {
        // Прямая проверка исходника: если вернуть AddHostedService в Chernika.Api
        // рядом с настройкой, поведение снова станет зависящим от того, какой файл
        // читает человек, а не от конфигурации.
        var program = File.ReadAllText(Path.Combine(HostDirectory("Chernika.Api"), "Program.cs"));

        Assert.DoesNotContain("AddHostedService<HKExpirationBackgroundService>", program);
        Assert.Contains("AddHKExpirationWorker(builder.Configuration, \"Chernika.Api\")", program);
    }

    [Fact]
    public void WebSource_RegistersWorkerThroughTheSameExtension()
    {
        var program = File.ReadAllText(Path.Combine(HostDirectory("Chernika.Web"), "Program.cs"));
        Assert.Contains("AddHKExpirationWorker(builder.Configuration, \"Chernika.Web\")", program);
    }

    // ── Штатный запуск: worker ровно один и он начинает обработку ─────────

    [Fact]
    public void WebHost_StartsExactlyOneWorker_WithRealConfiguration()
    {
        using var host = WebHostFor.Start("Chernika.Web");

        Assert.Single(host.Workers);

        var options = host.Services.GetRequiredService<IOptions<HKExpirationOptions>>().Value;
        Assert.True(options.WorkerEnabled);
        Assert.Equal(new[] { 90, 30, 7 }, options.WarningDays);
        Assert.Equal("01:00", options.DailyRunTimeUtc);
        Assert.Equal(14, options.ReviewTaskDueDays);
        Assert.True(options.RunOnStartup);

        // Обработка действительно начата: worker записал решение о владении и
        // вышел на стартовый прогон (RunOnStartup=true).
        Assert.True(host.Logs.Any(m => m.Contains(OwnershipLog) && m.Contains("Chernika.Web")
                && m.Contains("этим хостом")),
            "журнал запуска не содержит решения о владении обработкой сроков: "
            + string.Join(" | ", host.Logs));
        Assert.True(host.Logs.Any(m => m.Contains(StartupRunLog)),
            "worker не начал стартовый прогон обработки сроков: "
            + string.Join(" | ", host.Logs));
    }

    [Fact]
    public void BothHostsRunning_Together_StillHaveExactlyOneWorker()
    {
        // Сценарий развёртывания: Web поднят отдельно, API поднят отдельно —
        // как при одновременной работе обоих хостов.
        using var web = WebHostFor.Start("Chernika.Web");
        using var api = ApiHostFor.Start();

        Assert.Single(web.Workers);
        Assert.Empty(api.Workers);

        Assert.True(web.Logs.Any(m => m.Contains(OwnershipLog) && m.Contains("этим хостом")),
            string.Join(" | ", web.Logs));

        Assert.True(api.Logs.Any(m => m.Contains(OwnershipLog)
                && m.Contains("Chernika.Api") && m.Contains("НЕ этот хост")),
            "API не зафиксировал в журнале, что он не владелец: "
            + string.Join(" | ", api.Logs));

        // Двойного исполнения нет: обработку запустил только Web.
        Assert.False(api.Logs.Any(m => m.Contains(StartupRunLog)),
            "API выполнил прогон обработки сроков, хотя владельцем не является");
    }

    // ── Вспомогательное ───────────────────────────────────────────────────

    private static ServiceProvider BuildProvider(string host)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // TimeProvider обязателен: worker внедряется через контейнер и без него
        // не разрешается, а проверяется именно факт регистрации.
        services.AddSingleton(TimeProvider.System);
        services.AddHKExpirationWorker(HostConfiguration(host), host);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Поднимает настоящий <c>Chernika.Web</c> на отдельной базе. База своя,
    /// потому что worker с <c>RunOnStartup=true</c> пишет данные, и рабочая БД
    /// тут недопустима.
    /// </summary>
    private sealed class WebHostFor : WebApplicationFactory<Chernika.Web.EntryPointMarker>
    {
        private readonly HostLogCollector _logs = new();

        public static WebHostFor Start(string hostName) => new(hostName);

        private WebHostFor(string hostName)
        {
            HostName = hostName;
        }

        public string HostName { get; }

        public IReadOnlyList<HKExpirationBackgroundService> Workers => Services
            .GetServices<IHostedService>()
            .OfType<HKExpirationBackgroundService>()
            .ToArray();

        public IReadOnlyList<string> Logs => _logs.Snapshot();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection",
                TestDatabase.For("chernika_owner_chernika_web"));
            // Не Development: иначе хост начнёт создавать базу и сеять демо-данные,
            // а проверяется тут владение worker'ом, а не сидинг.
            builder.UseEnvironment("Staging");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(_logs);
                logging.SetMinimumLevel(LogLevel.Information);
            });
            builder.ConfigureServices(services =>
            {
                // Ровно как в Program.cs: решение принимает конфигурация хоста.
                services.AddHKExpirationWorker(HostConfiguration(HostName), HostName);
            });
        }
    }

    /// <summary>Настоящий <c>Chernika.Api</c> на отдельной базе.</summary>
    private sealed class ApiHostFor : WebApplicationFactory<Program>
    {
        private readonly HostLogCollector _logs = new();

        public static ApiHostFor Start() => new();

        public IReadOnlyList<HKExpirationBackgroundService> Workers => Services
            .GetServices<IHostedService>()
            .OfType<HKExpirationBackgroundService>()
            .ToArray();

        public IReadOnlyList<string> Logs => _logs.Snapshot();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection",
                TestDatabase.For("chernika_owner_chernika_api"));
            builder.UseEnvironment("Staging");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(_logs);
                logging.SetMinimumLevel(LogLevel.Information);
            });
            builder.ConfigureServices(services =>
            {
                services.AddHKExpirationWorker(HostConfiguration("Chernika.Api"), "Chernika.Api");
            });
        }
    }

    /// <summary>Собирает записи журнала запуска хоста.</summary>
    private sealed class HostLogCollector : ILoggerProvider
    {
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Snapshot()
        {
            lock (_entries) return _entries.ToArray();
        }

        public ILogger CreateLogger(string categoryName) => new Collecting(categoryName, _entries);

        public void Dispose() { }

        private sealed class Collecting : ILogger
        {
            private readonly string _category;
            private readonly List<string> _target;

            public Collecting(string category, List<string> target)
            {
                _category = category;
                _target = target;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = "[" + _category + "] " + formatter(state, exception);
                lock (_target) _target.Add(message);
            }
        }
    }
}
