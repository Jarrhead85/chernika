using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(DatabaseConnection.Build(builder.Configuration)));

builder.Services.AddDbContextFactory<AppDbContext>(
    options => options.UseNpgsql(DatabaseConnection.Build(builder.Configuration)),
    ServiceLifetime.Scoped);

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 1;
    options.Password.RequiredUniqueChars = 0;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/вход";
    options.LogoutPath = "/выход";
    options.AccessDeniedPath = "/доступ-запрещен";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});

builder.Services.AddAuthorization(options =>
    // Политики полномочий регистрируются единым списком. Раздельные списки
    // хостов разошлись: часть политик была только в Api, часть только в Web,
    // и одно право считалось по-разному в зависимости от источника запроса.
    // Общая регистрация делает повторное расхождение невозможным.
{
    options.AddPermissionPolicies();
});

builder.Services.AddScoped<HKCardService>();
builder.Services.AddScoped<HKCardValidationService>();
builder.Services.AddScoped<HKCardItemService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<GsmMaterialService>();
// Индивидуальные карты законсервированы: раздел IndividualCards по умолчанию
// выключен (Enabled=false), операции записи ИК отклоняются на сервере — в том
// числе при прямом вызове API.
builder.Services.Configure<IndividualCardModuleOptions>(
    builder.Configuration.GetSection("IndividualCards"));
builder.Services.AddSingleton<IIndividualCardModuleState, ConfiguredIndividualCardModuleState>();
builder.Services.AddScoped<IndividualCardService>();
builder.Services.AddScoped<CoefficientService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPermissionService, PermissionService>();
// Оповещение процесс-локальное: один экземпляр на процесс, чтобы все
// открытые сессии этого процесса увидели изменение прав.
builder.Services.AddSingleton<IPermissionChangeNotifier, PermissionChangeNotifier>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<ISecurityDataRepairService, SecurityDataRepairService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.Configure<FileStorageOptions>(builder.Configuration.GetSection("FileStorage"));
builder.Services.Configure<HKExpirationOptions>(builder.Configuration.GetSection("HKExpiration"));
builder.Services.AddScoped<HKCardExpirationService>();
builder.Services.AddScoped<HKExpirationForecastService>();
// Обработка сроков действия ХК здесь НЕ выполняется: владелец — Chernika.Web,
// который запускает start_app.cmd и содержит весь интерфейс. Раньше worker был
// зарегистрирован только в API, из-за чего при штатном запуске приложения
// обработка не выполнялась вообще, а при запуске обоих хостов выполнялась бы
// дважды. Решение задаётся настройкой HKExpiration:WorkerEnabled и проверяется
// тестом — см. HKExpirationWorkerRegistration.
builder.Services.AddHKExpirationWorker(builder.Configuration, "Chernika.Api");
builder.Services.AddScoped<SearchService>();

var app = builder.Build();

// Владелец worker'а объявляется в журнале запуска: иначе по логу нельзя
// отличить «обработка выполняется» от «её здесь нет». Имя хоста и решение
// разведены по разным местам фразы, иначе при выключенном worker'е выходило бы
// противоречие вида «владелец = Chernika.Api (НЕ этот хост)».
var hkWorker = app.Services.GetRequiredService<HKExpirationWorkerOwnership>();
app.Logger.LogInformation(
    "Обработка сроков действия ХК: хост {Host} — {Decision}.",
    hkWorker.HostName,
    hkWorker.IsOwner
        ? "ВЛАДЕЛЕЦ, обработка выполняется здесь"
        : "НЕ владелец, обработка выполняется в Chernika.Web");

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

/// <summary>
/// Точка входа, видимая для <c>WebApplicationFactory&lt;Program&gt;</c>.
/// <para>
/// Top-level statements компилируются в внутренний класс, на который нельзя
/// сослаться из другого проекта. Пустой partial-класс делает его доступным,
/// не меняя ни регистрации сервисов, ни поведения приложения.
/// </para>
/// <para>
/// Нужен именно для HTTP-тестов: без него проверки шли бы в обход реальных
/// маршрутов, политик и middleware, что не доказывает ничего.
/// </para>
/// </summary>
public partial class Program;
