using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Chernika.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(DatabaseConnection.Build(builder.Configuration)));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, CustomUserClaimsPrincipalFactory>();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
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
    options.AddPolicy("DeleteHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKDelete)));
    options.AddPolicy("ArchiveHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKArchive)));
    options.AddPolicy("SendToApprove", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKNodeSubmit, PermissionCodes.HKAggregateSubmit, PermissionCodes.HKEquipmentSubmit, PermissionCodes.HKComplexSubmit)));
    options.AddPolicy("CreateIndividualCard", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.IndividualCardGenerate)));
    options.AddPolicy("VerifyHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKReview)));
    options.AddPolicy("ApproveHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKApprove)));
    options.AddPolicy("ReturnHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKReview)));
    options.AddPolicy("ManageCoefficients", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("ManageUsers", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.UsersManage)));
    options.AddPolicy("ManageRoles", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.PermissionsManage)));
    options.AddPolicy("SystemConfig", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.SystemConfig)));
    options.AddPolicy("ViewAuditLog", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.AuditView)));
    options.AddPolicy("ViewTasks", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.TaskViewOwn)));
});

builder.Services.AddScoped<HKCardService>();
builder.Services.AddScoped<HKCardItemService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<IndividualCardService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    await DatabaseInit.InitializeAsync(db, userManager, roleManager);
}

app.Run();
