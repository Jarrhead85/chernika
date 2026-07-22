using System.Diagnostics;
using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class SecurityDataRepairService : ISecurityDataRepairService
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IPermissionService _permissions;
    private readonly AuditService _audit;

    private static readonly HashSet<string> BaseBusinessRoles = new()
    {
        nameof(UserRole.SystemAdmin),
        nameof(UserRole.NormAdmin),
        nameof(UserRole.Operator),
        nameof(UserRole.HeadOfDepartment),
        nameof(UserRole.Guest),
    };

    public SecurityDataRepairService(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IPermissionService permissions,
        AuditService audit)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _permissions = permissions;
        _audit = audit;
    }

    public async Task<SecurityRepairResult> RepairAsync(CancellationToken ct = default)
    {
        var result = new SecurityRepairResult();
        var sw = Stopwatch.StartNew();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            foreach (var roleName in BaseBusinessRoles)
            {
                if (!await _roleManager.RoleExistsAsync(roleName))
                {
                    await _roleManager.CreateAsync(new IdentityRole(roleName));
                    result.RolesCreated++;
                }
            }

            if (await _roleManager.RoleExistsAsync("Viewer"))
            {
                var viewers = await _userManager.GetUsersInRoleAsync("Viewer");
                foreach (var user in viewers)
                {
                    await _userManager.RemoveFromRoleAsync(user, "Viewer");
                    if (!await _userManager.IsInRoleAsync(user, "Guest"))
                        await _userManager.AddToRoleAsync(user, "Guest");
                    result.ViewerToGuestMigrated++;
                }

                var viewerRole = await _roleManager.FindByNameAsync("Viewer");
                if (viewerRole != null)
                {
                    var viewerUsers = await _userManager.GetUsersInRoleAsync("Viewer");
                    if (viewerUsers.Count == 0)
                        await _roleManager.DeleteAsync(viewerRole);
                }
            }

            var existingTemplates = await _db.RolePermissionTemplates
                .Select(x => new { x.RoleName, x.PermissionCode })
                .ToListAsync(ct);

            var existingSet = existingTemplates
                .GroupBy(x => x.RoleName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToHashSet());

            foreach (var roleName in BaseBusinessRoles)
            {
                IEnumerable<string> permsForRole;
                if (roleName == nameof(UserRole.SystemAdmin))
                    permsForRole = PermissionCodes.All;
                else
                    permsForRole = RolePermissionDefaults.GetForRole(roleName);

                existingSet.TryGetValue(roleName, out var existingCodes);

                foreach (var perm in permsForRole)
                {
                    if (existingCodes == null || !existingCodes.Contains(perm))
                    {
                        _db.RolePermissionTemplates.Add(new RolePermissionTemplate
                        {
                            Id = Guid.NewGuid(),
                            RoleName = roleName,
                            PermissionCode = perm,
                        });
                        result.TemplatesUpserted++;
                    }
                }
            }

            await _db.SaveChangesAsync(ct);

            var allUsers = await _userManager.Users
                .Where(u => !u.IsDeleted)
                .ToListAsync(ct);

            foreach (var user in allUsers)
            {
                var roles = await _userManager.GetRolesAsync(user);
                var baseRoles = roles.Where(r => BaseBusinessRoles.Contains(r)).ToList();

                if (baseRoles.Count == 0)
                {
                    result.UsersWithoutBaseRole.Add(new UserDiagnosticInfo
                    {
                        UserId = user.Id,
                        UserName = user.UserName ?? "",
                        CurrentRoles = roles.ToList(),
                    });
                }
                else if (baseRoles.Count > 1)
                {
                    result.UsersWithMultipleBaseRoles.Add(new UserDiagnosticInfo
                    {
                        UserId = user.Id,
                        UserName = user.UserName ?? "",
                        CurrentRoles = baseRoles,
                    });
                }
                else
                {
                    var baseRole = baseRoles[0];
                    if (baseRole != nameof(UserRole.SystemAdmin) && user.BranchId == null)
                    {
                        result.UsersRepaired.Add(new UserDiagnosticInfo
                        {
                            UserId = user.Id,
                            UserName = user.UserName ?? "",
                            CurrentRoles = [baseRole],
                        });
                    }
                }
            }

            var usersToClearCache = allUsers.Select(u => u.Id).ToList();
            foreach (var userId in usersToClearCache)
            {
                _permissions.InvalidateCache(userId);
                result.PermissionsCacheCleared++;
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        sw.Stop();

        await _audit.LogAsync("SecurityRepair", "System", "Repaired", Guid.Empty,
            $"Roles created: {result.RolesCreated}, Templates upserted: {result.TemplatesUpserted}, " +
            $"Cache cleared: {result.PermissionsCacheCleared}, Duration: {sw.ElapsedMilliseconds}ms");

        return result;
    }

    public async Task<SecurityDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var diagnostics = new SecurityDiagnosticsDto();

        var allRoles = await _roleManager.Roles.ToListAsync(ct);

        foreach (var role in allRoles)
        {
            if (!BaseBusinessRoles.Contains(role.Name!))
                continue;

            var templates = await _db.RolePermissionTemplates
                .Where(t => t.RoleName == role.Name)
                .ToListAsync(ct);

            var usersInRole = await _userManager.GetUsersInRoleAsync(role.Name!);
            var activeCount = usersInRole.Count(u => u.IsActive && !u.IsDeleted);

            diagnostics.Roles.Add(new RoleDiagnosticsDto
            {
                RoleName = role.Name!,
                TemplateCount = templates.Count,
                ActiveUserCount = activeCount,
                PermissionCodes = templates.Select(t => t.PermissionCode).ToHashSet(),
            });
        }

        var allActiveUsers = await _userManager.Users
            .Where(u => u.IsActive && !u.IsDeleted)
            .ToListAsync(ct);

        diagnostics.TotalActiveUsers = allActiveUsers.Count;

        foreach (var user in allActiveUsers)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var baseRoles = roles.Where(r => BaseBusinessRoles.Contains(r)).ToList();

            if (baseRoles.Count == 0)
                diagnostics.UsersWithoutBaseRole++;
            else if (baseRoles.Count > 1)
                diagnostics.UsersWithMultipleRoles++;
        }

        var hasIssues = diagnostics.UsersWithoutBaseRole > 0 || diagnostics.UsersWithMultipleRoles > 0;
        var hasTemplates = diagnostics.Roles.All(r => r.TemplateCount > 0);
        diagnostics.Status = hasIssues ? "Проблемы обнаружены" : (hasTemplates ? "Готово" : "Шаблоны не заполнены");

        return diagnostics;
    }
}
