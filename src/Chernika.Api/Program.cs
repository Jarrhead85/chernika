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
{
    options.AddPolicy("ViewHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKView)));
    options.AddPolicy("CreateHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKNodeCreate, PermissionCodes.HKAggregateCreate, PermissionCodes.HKEquipmentCreate, PermissionCodes.HKComplexCreate)));
    options.AddPolicy("EditHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKNodeEditDraft, PermissionCodes.HKAggregateEditDraft, PermissionCodes.HKEquipmentEditDraft, PermissionCodes.HKComplexEditDraft)));
    options.AddPolicy("DeleteHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKDeleteDraft, PermissionCodes.HKDeleteOnReview, PermissionCodes.HKDeleteRevisionRequired)));
    options.AddPolicy("ArchiveHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKArchive)));
    options.AddPolicy("ViewComposition", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.CompositionView)));
    options.AddPolicy("SendToApprove", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKNodeSubmit, PermissionCodes.HKAggregateSubmit, PermissionCodes.HKEquipmentSubmit, PermissionCodes.HKComplexSubmit)));
    options.AddPolicy("CreateIndividualCard", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.IndividualCardGenerate)));
    options.AddPolicy("DeleteIndividualCard", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.IndividualCardGenerate)));
    options.AddPolicy("VerifyHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKReview)));
    options.AddPolicy("ApproveHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKApprove)));
    options.AddPolicy("ReturnHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKReview)));
    options.AddPolicy("ManageCoefficients", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("ManageUsers", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.UsersManage)));
    options.AddPolicy("ManageRoles", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.PermissionsManage)));
    options.AddPolicy("SystemConfig", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.SystemConfig)));
    options.AddPolicy("ViewAuditLog", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.AuditView)));
    options.AddPolicy("ViewTasks", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.TaskViewOwn)));
    options.AddPolicy("ViewAllTasks", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.TaskView)));
    options.AddPolicy("AssignTasks", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.TaskAssign)));
    options.AddPolicy("CompleteTasks", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.TaskComplete)));
    options.AddPolicy("CancelTasks", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.TaskCancel)));
    options.AddPolicy("ManageReference", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("ManageIndividualCards", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.IndividualCardGenerate)));
    options.AddPolicy("ReportExport", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReportExport)));
    options.AddPolicy("HKAttachmentView", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKAttachmentView)));
    options.AddPolicy("HKAttachmentEdit", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKAttachmentEdit)));
    options.AddPolicy("CreateEquipment", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("EditEquipment", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("DeleteEquipment", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
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
