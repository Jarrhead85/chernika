using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Chernika.IntegrationTests;

public sealed class TestDatabaseFixture : IAsyncLifetime
{
    private const string DbName = "chernika_test";
    private const string ServerCs =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=qwerty12345";

    public ServiceProvider Services { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public Guid BranchA { get; } = Guid.NewGuid();
    public Guid BranchB { get; } = Guid.NewGuid();

    public ApplicationUser SystemAdminUser { get; private set; } = null!;
    public ApplicationUser NormAdminA { get; private set; } = null!;
    public ApplicationUser NormAdminA2 { get; private set; } = null!;
    public ApplicationUser OperatorA { get; private set; } = null!;
    public ApplicationUser HeadA { get; private set; } = null!;
    public ApplicationUser GuestA { get; private set; } = null!;
    public ApplicationUser NormAdminB { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await DropAndCreateDatabaseAsync();
        ConnectionString =
            "Host=localhost;Port=5432;Database=" + DbName + ";Username=postgres;Password=qwerty12345;Pooling=false";

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFilter(_ => false));
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(ConnectionString));
        services.AddIdentityCore<ApplicationUser>(o => { })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<FakeCurrentUser>();
        services.AddScoped<ICurrentUserService>(sp => sp.GetRequiredService<FakeCurrentUser>());
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<AuditService>();
        services.AddScoped<TaskService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<HKCardValidationService>();
        services.AddScoped<HKCardService>();
        services.AddOptions();
        services.Configure<HKExpirationOptions>(o =>
        {
            o.WarningDays = new[] { 90, 30, 7 };
            o.DailyRunTimeUtc = "01:00";
            o.ReviewTaskDueDays = 14;
        });
        services.AddScoped<HKCardExpirationService>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<CoefficientService>();
        services.AddScoped<GsmMaterialService>();
        services.AddScoped<IndividualCardService>();
        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var rm = sp.GetRequiredService<RoleManager<IdentityRole>>();
        await SecuritySeedService.SeedAsync(db, um, rm);

        db.Branches.AddRange(
            new Branch { Id = BranchA, Name = "Филиал А", Code = "A" },
            new Branch { Id = BranchB, Name = "Филиал Б", Code = "B" });
        await db.SaveChangesAsync();

        SystemAdminUser = await CreateUserAsync(um, "sysadmin", nameof(UserRole.SystemAdmin), null);
        NormAdminA = await CreateUserAsync(um, "normadmin_a", nameof(UserRole.NormAdmin), BranchA);
        NormAdminA2 = await CreateUserAsync(um, "normadmin_a2", nameof(UserRole.NormAdmin), BranchA);
        OperatorA = await CreateUserAsync(um, "operator_a", nameof(UserRole.Operator), BranchA);
        HeadA = await CreateUserAsync(um, "head_a", nameof(UserRole.HeadOfDepartment), BranchA);
        GuestA = await CreateUserAsync(um, "guest_a", nameof(UserRole.Guest), BranchA);
        NormAdminB = await CreateUserAsync(um, "normadmin_b", nameof(UserRole.NormAdmin), BranchB);
    }

    public async Task DisposeAsync()
    {
        if (Services is null)
            return;
        await Services.DisposeAsync();
    }

    public TestScope CreateScope()
    {
        var scope = Services.CreateAsyncScope();
        var user = scope.ServiceProvider.GetRequiredService<FakeCurrentUser>();
        return new TestScope(scope, user);
    }

    private static async Task DropAndCreateDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(ServerCs);
        await conn.OpenAsync();

        await using (var existsCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", conn))
        {
            existsCmd.Parameters.AddWithValue("db", DbName);
            if (await existsCmd.ExecuteScalarAsync() != null)
            {
                await using var terminate = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()", conn);
                terminate.Parameters.AddWithValue("db", DbName);
                await terminate.ExecuteNonQueryAsync();

                await using var dropCmd = new NpgsqlCommand("DROP DATABASE " + DbName, conn);
                await dropCmd.ExecuteNonQueryAsync();
            }
        }

        await using var createCmd = new NpgsqlCommand("CREATE DATABASE " + DbName, conn);
        await createCmd.ExecuteNonQueryAsync();
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        UserManager<ApplicationUser> um, string login, string role, Guid? branchId)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = true,
        };

        var result = await um.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Создание пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        var roleResult = await um.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
            throw new InvalidOperationException(
                "Назначение роли не удалось: " + string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        return user;
    }
}
