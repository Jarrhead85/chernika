using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Reports;
using Chernika.Infrastructure.Services;
using Chernika.Web.Auth;
using Chernika.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// Ключи DataProtection должны переживать рестарты приложения, иначе
// все auth-cookie становятся невалидными при каждом перезапуске
// (после Ctrl+F5 пользователь оказывается неаутентифицированным).
var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, ".dataprotection");
Directory.CreateDirectory(dataProtectionPath);
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("Chernika");

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(DatabaseConnection.Build(builder.Configuration)));

builder.Services.AddDbContextFactory<AppDbContext>(
    options => options.UseNpgsql(DatabaseConnection.Build(builder.Configuration)),
    ServiceLifetime.Scoped);

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPermissionService, PermissionService>();
// Оповещение процесс-локальное: один экземпляр на процесс, чтобы все
// открытые сессии этого процесса увидели изменение прав.
builder.Services.AddSingleton<IPermissionChangeNotifier, PermissionChangeNotifier>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<CircuitDbLock>();

builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, CustomUserClaimsPrincipalFactory>();

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
    // Политики полномочий регистрируются единым списком. Раздельные списки
    // хостов разошлись: часть политик была только в Api, часть только в Web,
    // и одно право считалось по-разному в зависимости от источника запроса.
    // Общая регистрация делает повторное расхождение невозможным.
    options.AddPermissionPolicies();
});

builder.Services.AddScoped<HKCardService>();
builder.Services.AddScoped<HKCardValidationService>();
builder.Services.AddScoped<HKCardItemService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<GsmMaterialService>();
builder.Services.AddScoped<ReferenceCatalogService>();
// Индивидуальные карты законсервированы: раздел IndividualCards по умолчанию
// выключен (Enabled=false), операции записи ИК отклоняются на сервере.
builder.Services.Configure<IndividualCardModuleOptions>(
    builder.Configuration.GetSection("IndividualCards"));
builder.Services.AddSingleton<IIndividualCardModuleState, ConfiguredIndividualCardModuleState>();
builder.Services.AddScoped<IndividualCardService>();
builder.Services.AddScoped<CoefficientService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<Chernika.Web.Services.NotificationRefreshService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<ISecurityDataRepairService, SecurityDataRepairService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.Configure<FileStorageOptions>(builder.Configuration.GetSection("FileStorage"));
builder.Services.Configure<HKExpirationOptions>(builder.Configuration.GetSection("HKExpiration"));
builder.Services.AddScoped<HKCardExpirationService>();
builder.Services.AddScoped<HKExpirationForecastService>();
// ВЛАДЕЛЕЦ фоновой обработки сроков ХК. Именно этот хост запускает
// start_app.cmd и содержит весь интерфейс, поэтому worker живёт здесь.
// Регистрация управляется настройкой HKExpiration:WorkerEnabled — см.
// HKExpirationWorkerRegistration. В Chernika.Api он выключен, поэтому при
// запуске обоих хостов обработка не удваивается.
builder.Services.AddHKExpirationWorker(builder.Configuration, "Chernika.Web");
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();

var app = builder.Build();

// Решение о владении worker'ом пишется в журнал запуска явно: иначе по логу
// нельзя отличить «worker включён» от «его забыли зарегистрировать».
//
// Формулировка различает ИМЯ ХОСТА и РЕШЕНИЕ. Раньше здесь подставлялись оба, и
// при выключенном worker'е выходило «владелец = Chernika.Web (НЕ этот хост)» —
// противоречие, в котором имя и решение смешивались в одну фразу.
var hkWorker = app.Services.GetRequiredService<HKExpirationWorkerOwnership>();
app.Logger.LogInformation(
    "Обработка сроков действия ХК: хост {Host} — {Decision}.",
    hkWorker.HostName,
    hkWorker.IsOwner
        ? "ВЛАДЕЛЕЦ, обработка выполняется здесь"
        : "НЕ владелец, обработка выполняется в Chernika.Web");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// E1: inline PDF-бланк ИК (Web-хост не монтирует MVC-контроллеры, поэтому
// маршрут /api/individualcards/{id}/pdf обслуживается здесь; право
// IndividualCard.View и филиал проверяет GetExportAsync).
app.MapGet("/api/individualcards/{id:guid}/pdf",
    async (Guid id, ReportService reports, HttpContext http, CancellationToken ct) =>
    {
        IndividualCardPdfFile? file;
        try
        {
            file = await reports.GenerateIndividualCardPdfAsync(id, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }

        if (file is null)
            return Results.NotFound();

        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileNameStar = file.FileName,
        }.ToString();
        return Results.File(file.Content, "application/pdf");
    })
    .RequireAuthorization();

// E2: скачивание XLSX-бланка ИК (attachment).
app.MapGet("/api/individualcards/{id:guid}/xlsx",
    async (Guid id, ReportService reports, CancellationToken ct) =>
    {
        IndividualCardXlsxFile? file;
        try
        {
            file = await reports.GenerateIndividualCardXlsxAsync(id, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }

        if (file is null)
            return Results.NotFound();

        return Results.File(
            file.Content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileDownloadName: file.FileName);
    })
    .RequireAuthorization();

// Просмотр/скачивание PDF-скана ХК из браузера (window.open с cookie).
app.MapGet("/api/hkcards/{id:guid}/attachment/content",
    async (Guid id, bool? inline, HKCardService hkCards, IPermissionService perms,
        ICurrentUserService currentUser, HttpContext http) =>
    {
        if (!await perms.HasPermissionAsync(currentUser.GetRequiredUserId().ToString(), PermissionCodes.HKView))
            return Results.Forbid();

        var content = await hkCards.OpenAttachmentAsync(id);
        if (content is null)
            return Results.NotFound();

        if (inline == true)
        {
            http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
            {
                FileNameStar = content.OriginalFileName,
            }.ToString();
            return Results.File(content.Content, content.ContentType, enableRangeProcessing: true);
        }
        return Results.File(content.Content, content.ContentType, fileDownloadName: content.OriginalFileName,
            enableRangeProcessing: true);
    })
    .RequireAuthorization();

// ── Мой аккаунт (/профиль): действия текущего пользователя ─────────────

app.MapGet("/api/account/me/avatar/content",
    async (AccountService account) =>
    {
        var content = await account.GetMyAvatarAsync();
        if (content is null)
            return Results.NotFound();
        return Results.File(content.Content, content.ContentType, enableRangeProcessing: false);
    })
    .RequireAuthorization();

app.MapPost("/api/account/me/avatar",
    async (HttpRequest request, AccountService account, CancellationToken ct) =>
    {
        if (!request.HasFormContentType || request.Form.Files.Count == 0)
            return Results.BadRequest(new { error = "Файл не выбран." });

        var file = request.Form.Files[0];
        try
        {
            await using var stream = file.OpenReadStream();
            await account.UploadMyAvatarAsync(
                stream,
                file.FileName,
                file.ContentType,
                file.Length,
                ct);
            return Results.Ok(new { message = "Фото обновлено." });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .RequireAuthorization();

app.MapDelete("/api/account/me/avatar",
    async (AccountService account, CancellationToken ct) =>
    {
        await account.DeleteMyAvatarAsync(ct);
        return Results.NoContent();
    })
    .RequireAuthorization();

app.MapPost("/api/users/{id}/reset-password",
    async (string id, [FromBody] ResetUserPasswordByAdminRequest request, AccountService account,
        CancellationToken ct) =>
    {
        try
        {
            var result = await account.ResetUserPasswordByAdminAsync(id, request, ct);
            return Results.Ok(new { success = result.Success, error = result.Error });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(403);
        }
    })
    .RequireAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var seedDemo = app.Configuration.GetValue<bool>("SeedDemoData");
    await DatabaseInit.InitializeAsync(db, userManager, roleManager, seedDemo);
}

app.Run();
