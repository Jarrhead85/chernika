using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(DatabaseConnection.Build(builder.Configuration)));

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
    options.AddPolicy("ManageComposition", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("SendToApprove", policy => policy.RequireRole("Operator"));
    options.AddPolicy("CreateIndividualCard", policy => policy.RequireRole("Operator"));
    options.AddPolicy("DeleteIndividualCard", policy => policy.RequireRole("Operator"));
    options.AddPolicy("VerifyHK", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ApproveHK", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ReturnHK", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ManageCoefficients", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("ManageUsers", policy => policy.RequireRole("SystemAdmin"));
    options.AddPolicy("ManageRoles", policy => policy.RequireRole("SystemAdmin"));
    options.AddPolicy("SystemConfig", policy => policy.RequireRole("SystemAdmin"));
    options.AddPolicy("ViewAuditLog", policy => policy.RequireRole("SystemAdmin", "NormAdmin", "DepartmentHead"));
    options.AddPolicy("ViewTasks", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CreateEquipment", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("EditEquipment", policy => policy.RequireRole("NormAdmin"));
    options.AddPolicy("DeleteEquipment", policy => policy.RequireRole("NormAdmin"));
});

builder.Services.AddScoped<HKCardService>();
builder.Services.AddScoped<HKCardItemService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<IndividualCardService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ReportService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
