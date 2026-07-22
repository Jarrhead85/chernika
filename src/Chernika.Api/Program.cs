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
    options.AddPolicy("ViewHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKView)));
    options.AddPolicy("CreateHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKNodeCreate, PermissionCodes.HKAggregateCreate, PermissionCodes.HKEquipmentCreate, PermissionCodes.HKComplexCreate)));
    options.AddPolicy("EditHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKNodeEditDraft, PermissionCodes.HKAggregateEditDraft, PermissionCodes.HKEquipmentEditDraft, PermissionCodes.HKComplexEditDraft)));
    options.AddPolicy("DeleteHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKDelete)));
    options.AddPolicy("ArchiveHK", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.HKArchive)));
    options.AddPolicy("ManageComposition", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.CompositionEdit)));
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
    options.AddPolicy("ManageReference", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("ManageIndividualCards", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.IndividualCardGenerate)));
    options.AddPolicy("ManageTasks", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.TaskManage)));
    options.AddPolicy("CreateEquipment", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("EditEquipment", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
    options.AddPolicy("DeleteEquipment", policy => policy.AddRequirements(new PermissionRequirement(PermissionCodes.ReferenceEdit)));
});

builder.Services.AddScoped<HKCardService>();
builder.Services.AddScoped<HKCardItemService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<IndividualCardService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<ISecurityDataRepairService, SecurityDataRepairService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
