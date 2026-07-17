using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Chernika.Web.Auth;
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
    options.AddPolicy("ViewHK", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CreateHK", policy => policy.RequireRole("Operator"));
    options.AddPolicy("EditHK", policy => policy.RequireRole("Operator"));
    options.AddPolicy("DeleteHK", policy => policy.RequireRole("Operator"));
    options.AddPolicy("ArchiveHK", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("SendToApprove", policy => policy.RequireRole("Operator"));
    options.AddPolicy("CreateIndividualCard", policy => policy.RequireRole("Operator"));
    options.AddPolicy("VerifyHK", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ApproveHK", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ReturnHK", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ManageCoefficients", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ManageUsers", policy => policy.RequireRole("SystemAdmin"));
    options.AddPolicy("ManageRoles", policy => policy.RequireRole("SystemAdmin"));
    options.AddPolicy("SystemConfig", policy => policy.RequireRole("SystemAdmin"));
    options.AddPolicy("ViewAuditLog", policy => policy.RequireRole("SystemAdmin", "NormAdmin", "DepartmentHead"));
    options.AddPolicy("ViewTasks", policy => policy.RequireAuthenticatedUser());
});

builder.Services.AddScoped<HKCardService>();
builder.Services.AddScoped<HKCardItemService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<IndividualCardService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SearchService>();
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
