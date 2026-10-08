using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Chernika.IntegrationTests;

/// <summary>
/// Изолированная база ровно с двумя активными системными администраторами.
/// <para>
/// Общая фикстура не годится: в её базе всегда есть собственный
/// SystemAdmin плюс администраторы, накопленные другими классами. При таком
/// наборе операция никогда не доходит до защиты последнего администратора -
/// «другие активные» всегда найдутся. Проверка защиты обязана идти на базе,
/// где активных администраторов ровно два и оба являются целью операции.
/// </para>
/// <para>
/// База отдельная и пересоздаётся: общая тестовая база и рабочая база не
/// затрагиваются.
/// </para>
/// </summary>
public sealed class TwoAdminsFixture : IAsyncLifetime
{
    private const string DbName = "chernika_test_twoadmins";

    private static string ServerCs => TestDatabase.LocalServerConnectionString();

    public ServiceProvider Services { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    /// <summary>Идентификатор первого администратора.</summary>
    public string AdminOneId { get; private set; } = null!;

    /// <summary>Идентификатор второго администратора.</summary>
    public string AdminTwoId { get; private set; } = null!;

    public Guid BranchId { get; } = Guid.NewGuid();

    public TestTimeProvider Clock { get; private set; } = null!;
    public TestLogCollector Logs { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await DropAndCreateDatabaseAsync();
        ConnectionString = TestDatabase.For(DbName);

        var services = new ServiceCollection();

        Logs = new TestLogCollector();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Warning);
            b.AddProvider(Logs);
        });

        services.AddDbContext<AppDbContext>(o =>
            o.UseNpgsql(ConnectionString).AddInterceptors(FailingCommandInterceptor.Instance));

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 1;
                o.Password.RequiredUniqueChars = 0;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole>()
            .AddDefaultTokenProviders()
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddDataProtection();
        services.AddMemoryCache();

        Clock = TestTimeProvider.SystemShim();
        services.AddSingleton(Clock);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<FakeCurrentUser>();
        services.AddScoped<ICurrentUserService>(sp => sp.GetRequiredService<FakeCurrentUser>());
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddSingleton<IPermissionChangeNotifier, PermissionChangeNotifier>();
        services.AddScoped<AuditService>();
        services.AddScoped<TaskService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<HKCardValidationService>();
        services.AddScoped<HKCardService>();
        services.AddScoped<HKCardExpirationService>();
        services.AddScoped<HKExpirationForecastService>();
        services.AddScoped<UserManagementService>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<CoefficientService>();
        services.AddScoped<GsmMaterialService>();
        services.AddScoped<SearchService>();
        services.AddScoped<TestIndividualCardModuleState>();
        services.AddScoped<IIndividualCardModuleState>(
            sp => sp.GetRequiredService<TestIndividualCardModuleState>());
        services.AddScoped<IndividualCardService>();
        services.AddScoped<ReportService>();

        services.Configure<HKExpirationOptions>(o =>
        {
            o.WarningDays = new[] { 90, 30, 7 };
            o.DailyRunTimeUtc = "01:00";
            o.ReviewTaskDueDays = 14;
        });
        services.AddOptions();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<FileStorageOptions>>(
            new OptionsWrapper<FileStorageOptions>(
                new FileStorageOptions { MaxPdfSizeBytes = 20L * 1024 * 1024 }));

        var storageRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "chernika-tests-twoadmins-storage");
        System.IO.Directory.CreateDirectory(storageRoot);

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = storageRoot,
            })
            .Build());
        services.AddSingleton<IFileStorageService, LocalFileStorageService>();

        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync();

        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var rm = sp.GetRequiredService<RoleManager<IdentityRole>>();
        await SecuritySeedService.SeedAsync(db, um, rm);

        db.Branches.Add(new Branch
        {
            Id = BranchId,
            Name = "Организация проверки",
            Code = "ADM",
        });
        await db.SaveChangesAsync();

        // Ровно два администратора и больше никого.
        AdminOneId = await CreateAdminAsync(um, "twoadmin_one");
        AdminTwoId = await CreateAdminAsync(um, "twoadmin_two");
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
            await Services.DisposeAsync();
    }

    public TestScope CreateScope()
    {
        Clock.Reset();
        Logs.Clear();

        var scope = Services.CreateAsyncScope();
        var user = scope.ServiceProvider.GetRequiredService<FakeCurrentUser>();
        return new TestScope(scope, user, Clock, Logs);
    }

    /// <summary>Возвращает активных системных администраторов по факту базы.</summary>
    public async Task<List<string>> ActiveAdminIdsAsync()
    {
        await using var s = CreateScope();

        var roleId = await s.Db.Roles
            .Where(r => r.Name == nameof(UserRole.SystemAdmin))
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        var ids = await s.Db.UserRoles
            .Where(ur => ur.RoleId == roleId)
            .Select(ur => ur.UserId)
            .ToListAsync();

        return await s.Db.Users
            .Where(u => ids.Contains(u.Id) && u.IsActive && !u.IsDeleted)
            .Select(u => u.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Возвращает базу к исходному состоянию: оба администратора снова активны
    /// и снова в роли.
    /// <para>
    /// Это подготовка между тестами, а не часть проверяемого действия:
    /// проверяемые операции обязаны идти только через публичные сервисные
    /// методы, и этот метод используется исключительно чтобы каждый тест
    /// начинал с одинаковых входных данных.
    /// </para>
    /// </summary>
    /// <summary>Записи аудита, относящиеся к этим двум администраторам.</summary>
    public async Task<List<string>> AuditActionsAsync()
    {
        await using var s = CreateScope();

        var ids = new[] { AdminOneId, AdminTwoId };

        return await s.Db.AuditLogs.AsNoTracking()
            .Where(a => ids.Contains(a.EntityId))
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.Action)
            .ToListAsync();
    }

    public async Task ResetAsync()
    {
        await using var s = CreateScope();

        var ids = new[] { AdminOneId, AdminTwoId };

        // Журнал чистится между тестами: иначе счётчики записей учитывали бы
        // операции предыдущего теста и сравнение стало бы бессмысленным.
        await s.Db.AuditLogs.Where(a => ids.Contains(a.EntityId)).ExecuteDeleteAsync();

        // Прямое обновление строк надёжнее вызовов Identity: сброс не должен
        // зависеть от проверок валидации и метки совместимости. Результат
        // проверяется отдельно, чтобы молчаливый отказ не выдавал себя за
        // подготовку к тесту.
        var affected = await s.Db.Users
            .Where(u => ids.Contains(u.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.IsActive, true)
                .SetProperty(u => u.IsDeleted, false)
                .SetProperty(u => u.DeletedAt, (DateTime?)null)
                .SetProperty(u => u.DeletedByUserId, (string?)null)
                .SetProperty(u => u.LockoutEnabled, true)
                .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null));

        Assert.Equal(ids.Length, affected);

        var roleId = await s.Db.Roles
            .Where(r => r.Name == nameof(UserRole.SystemAdmin))
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        foreach (var id in ids)
        {
            var assigned = await s.Db.UserRoles
                .AnyAsync(ur => ur.UserId == id && ur.RoleId == roleId);

            if (!assigned)
            {
                s.Db.UserRoles.Add(new IdentityUserRole<string>
                {
                    UserId = id,
                    RoleId = roleId,
                });
            }
        }

        await s.Db.SaveChangesAsync();

        var restored = await ActiveAdminIdsAsync();
        Assert.Equal(ids.Length, restored.Count);

        s.Permissions.InvalidateCache(AdminOneId);
        s.Permissions.InvalidateCache(AdminTwoId);
    }

    private async Task<string> CreateAdminAsync(UserManager<ApplicationUser> um, string login)
    {
        var user = new ApplicationUser
        {
            UserName = login,
            Email = login + "@twoadmins.test",
            EmailConfirmed = true,
            FullName = login,
            BranchId = BranchId,
            IsActive = true,
        };

        var created = await um.CreateAsync(user, "Two-Adm-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await um.AddToRoleAsync(user, nameof(UserRole.SystemAdmin));

        return user.Id;
    }

    private static async Task DropAndCreateDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(ServerCs);
        await conn.OpenAsync();

        await using (var existsCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", conn))
        {
            existsCmd.Parameters.Add(new NpgsqlParameter { Value = DbName });
            if (await existsCmd.ExecuteScalarAsync() != null)
            {
                await using var terminate = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", conn);
                terminate.Parameters.Add(new NpgsqlParameter { Value = DbName });
                await terminate.ExecuteNonQueryAsync();

                await using var dropCmd = new NpgsqlCommand("DROP DATABASE " + DbName, conn);
                await dropCmd.ExecuteNonQueryAsync();
            }
        }

        await using var createCmd = new NpgsqlCommand("CREATE DATABASE " + DbName, conn);
        await createCmd.ExecuteNonQueryAsync();
    }
}