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

    private static readonly Dictionary<string, string[]> ExpectedTemplates = new()
    {
        [nameof(UserRole.SystemAdmin)] = PermissionCodes.All.ToArray(),
        [nameof(UserRole.NormAdmin)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKNodeCreate, PermissionCodes.HKNodeEditDraft, PermissionCodes.HKNodeSubmit,
            PermissionCodes.HKAggregateCreate, PermissionCodes.HKAggregateEditDraft, PermissionCodes.HKAggregateSubmit,
            PermissionCodes.HKEquipmentCreate, PermissionCodes.HKEquipmentEditDraft, PermissionCodes.HKEquipmentSubmit,
            PermissionCodes.HKComplexCreate, PermissionCodes.HKComplexEditDraft, PermissionCodes.HKComplexSubmit,
            PermissionCodes.HKReview, PermissionCodes.HKApprove, PermissionCodes.HKArchive, PermissionCodes.HKDelete,
            PermissionCodes.ReferenceView, PermissionCodes.ReferenceEdit,
            PermissionCodes.CompositionView, PermissionCodes.CompositionEdit,
            PermissionCodes.IndividualCardView, PermissionCodes.IndividualCardGenerate,
            PermissionCodes.ReportExport,
            PermissionCodes.AuditView,
        ],
        [nameof(UserRole.Operator)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKNodeCreate, PermissionCodes.HKNodeEditDraft, PermissionCodes.HKNodeSubmit,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView, PermissionCodes.IndividualCardGenerate,
            PermissionCodes.ReportExport,
            PermissionCodes.TaskViewOwn,
        ],
        [nameof(UserRole.HeadOfDepartment)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView,
            PermissionCodes.ReportExport,
            PermissionCodes.AuditView,
            PermissionCodes.TaskViewOwn,
        ],
        [nameof(UserRole.Guest)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView,
            PermissionCodes.TaskViewOwn,
        ],
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
                    var createResult = await _roleManager.CreateAsync(new IdentityRole(roleName));
                    if (!createResult.Succeeded)
                    {
                        await transaction.RollbackAsync(ct);
                        throw new InvalidOperationException(
                            $"Не удалось создать роль «{roleName}»: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
                    }
                    result.RolesCreated++;
                    await _audit.LogAsync("SecurityRepair", "System", "RoleCreated", Guid.Empty,
                        $"Создана роль: {roleName}");
                }
            }

            if (await _roleManager.RoleExistsAsync("Viewer"))
            {
                var viewers = await _userManager.GetUsersInRoleAsync("Viewer");
                foreach (var user in viewers)
                {
                    var removeResult = await _userManager.RemoveFromRoleAsync(user, "Viewer");
                    if (!removeResult.Succeeded)
                    {
                        await transaction.RollbackAsync(ct);
                        throw new InvalidOperationException(
                            $"Не удалось перевести пользователя {user.UserName} из роли «Наблюдатель»: {string.Join(", ", removeResult.Errors.Select(e => e.Description))}");
                    }

                    if (!await _userManager.IsInRoleAsync(user, "Guest"))
                    {
                        var addResult = await _userManager.AddToRoleAsync(user, "Guest");
                        if (!addResult.Succeeded)
                        {
                            await transaction.RollbackAsync(ct);
                            throw new InvalidOperationException(
                                $"Не удалось добавить пользователя {user.UserName} в роль «Гость»: {string.Join(", ", addResult.Errors.Select(e => e.Description))}");
                        }
                    }
                    result.ViewerToGuestMigrated++;
                }

                var viewerRole = await _roleManager.FindByNameAsync("Viewer");
                if (viewerRole != null)
                {
                    var remaining = await _userManager.GetUsersInRoleAsync("Viewer");
                    if (remaining.Count == 0)
                        await _roleManager.DeleteAsync(viewerRole);
                }

                if (result.ViewerToGuestMigrated > 0)
                    await _audit.LogAsync("SecurityRepair", "System", "ViewerMigrated", Guid.Empty,
                        $"Пользователей перенесено из «Наблюдатель» в «Гость»: {result.ViewerToGuestMigrated}");
            }

            var allRoleTemplates = await _db.RolePermissionTemplates
                .Select(x => new { x.RoleName, x.PermissionCode })
                .ToListAsync(ct);

            var existingByRole = allRoleTemplates
                .GroupBy(x => x.RoleName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToHashSet());

            foreach (var roleName in BaseBusinessRoles)
            {
                if (!ExpectedTemplates.TryGetValue(roleName, out var expected))
                    continue;

                var expectedSet = expected.ToHashSet();
                existingByRole.TryGetValue(roleName, out var existing);

                foreach (var code in expected)
                {
                    if (existing == null || !existing.Contains(code))
                    {
                        _db.RolePermissionTemplates.Add(new RolePermissionTemplate
                        {
                            Id = Guid.NewGuid(),
                            RoleName = roleName,
                            PermissionCode = code,
                        });
                        result.TemplatesAdded++;
                    }
                }
            }

            await _db.SaveChangesAsync(ct);

            var allActiveUsers = await _userManager.Users
                .Where(u => !u.IsDeleted)
                .Select(u => new { u.Id, u.UserName, u.FullName, u.BranchId })
                .ToListAsync(ct);

            var userIds = allActiveUsers.Select(u => u.Id).ToList();

            var userRoleAssignments = await _db.UserRoles
                .Where(ur => userIds.Contains(ur.UserId))
                .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
                .ToListAsync(ct);

            var rolesByUser = userRoleAssignments
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Name!).ToList());

            foreach (var user in allActiveUsers)
            {
                rolesByUser.TryGetValue(user.Id, out var roles);
                roles ??= [];
                var baseRoles = roles.Where(r => BaseBusinessRoles.Contains(r)).ToList();

                if (baseRoles.Count == 0)
                {
                    result.UsersWithoutBaseRole.Add(new UserDiagnosticInfo
                    {
                        UserId = user.Id,
                        UserName = user.UserName ?? "",
                        FullName = user.FullName ?? "",
                        CurrentRoles = roles,
                        RecommendedAction = "Открыть пользователя и назначить одну роль",
                    });
                }
                else if (baseRoles.Count > 1)
                {
                    result.UsersWithMultipleBaseRoles.Add(new UserDiagnosticInfo
                    {
                        UserId = user.Id,
                        UserName = user.UserName ?? "",
                        FullName = user.FullName ?? "",
                        CurrentRoles = baseRoles,
                        RecommendedAction = "Оставить одну роль",
                    });
                }
                else
                {
                    var baseRole = baseRoles[0];
                    if (baseRole != nameof(UserRole.SystemAdmin) && user.BranchId == null)
                    {
                        result.UsersMissingBranch.Add(new UserDiagnosticInfo
                        {
                            UserId = user.Id,
                            UserName = user.UserName ?? "",
                            FullName = user.FullName ?? "",
                            CurrentRoles = [baseRole],
                            RecommendedAction = "Открыть пользователя и указать филиал",
                        });
                    }
                }
            }

            var usersToClearCache = allActiveUsers.Select(u => u.Id).ToList();
            foreach (var userId in usersToClearCache)
            {
                _permissions.InvalidateCache(userId);
                result.CacheCleared++;
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        sw.Stop();

        var manualCount = result.UsersWithoutBaseRole.Count
            + result.UsersWithMultipleBaseRoles.Count
            + result.UsersMissingBranch.Count;

        await _audit.LogAsync("SecurityRepair", "System", "Repaired", Guid.Empty,
            $"Ролей создано: {result.RolesCreated}, шаблонов добавлено: {result.TemplatesAdded}, " +
            $"перенесено Viewer→Guest: {result.ViewerToGuestMigrated}, кэш обновлён: {result.CacheCleared}, " +
            $"требуют ручного исправления: {manualCount}, время: {sw.ElapsedMilliseconds}ms");

        return result;
    }

    public async Task<SecurityDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var diagnostics = new SecurityDiagnosticsDto();

        var allRoles = await _roleManager.Roles.ToListAsync(ct);
        var roleNames = allRoles.Select(r => r.Name!).ToHashSet();

        var allRoleTemplates = await _db.RolePermissionTemplates
            .Select(x => new { x.RoleName, x.PermissionCode })
            .ToListAsync(ct);

        var templatesByRole = allRoleTemplates
            .GroupBy(x => x.RoleName)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToHashSet());

        var allActiveUsers = await _userManager.Users
            .Where(u => !u.IsDeleted)
            .Select(u => new { u.Id, u.UserName, u.FullName, u.BranchId, u.IsActive })
            .ToListAsync(ct);

        var userIds = allActiveUsers.Select(u => u.Id).ToList();

        var userRoleAssignments = await _db.UserRoles
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(ct);

        var rolesByUser = userRoleAssignments
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name!).ToList());

        var validCodes = PermissionCodes.All.ToHashSet();

        foreach (var role in allRoles)
        {
            if (!BaseBusinessRoles.Contains(role.Name!))
                continue;

            templatesByRole.TryGetValue(role.Name!, out var actualCodes);
            actualCodes ??= new HashSet<string>();

            ExpectedTemplates.TryGetValue(role.Name!, out var expectedArray);
            var expectedCodes = expectedArray?.ToHashSet() ?? new HashSet<string>();

            var activeCount = allActiveUsers
                .Where(u => u.IsActive)
                .Count(u =>
                {
                    rolesByUser.TryGetValue(u.Id, out var r);
                    return r != null && r.Contains(role.Name!);
                });

            diagnostics.Roles.Add(new RoleDiagnosticsDto
            {
                RoleName = role.Name!,
                TemplateCount = actualCodes.Count,
                ExpectedTemplateCount = expectedCodes.Count,
                ActiveUserCount = activeCount,
                PermissionCodes = actualCodes,
                ExpectedPermissionCodes = expectedCodes,
                MissingPermissionCodes = expectedCodes.Except(actualCodes).Count(),
                UnexpectedPermissionCodes = actualCodes.Except(expectedCodes).Count(),
            });
        }

        var activeUsers = allActiveUsers.Where(u => u.IsActive).ToList();
        diagnostics.TotalActiveUsers = activeUsers.Count;

        var missingBranchCount = 0;

        foreach (var user in activeUsers)
        {
            rolesByUser.TryGetValue(user.Id, out var roles);
            roles ??= [];
            var baseRoles = roles.Where(r => BaseBusinessRoles.Contains(r)).ToList();

            if (baseRoles.Count == 0)
            {
                diagnostics.UsersWithoutBaseRole++;
                diagnostics.UsersWithoutBaseRoleList.Add(new UserDiagnosticInfo
                {
                    UserId = user.Id,
                    UserName = user.UserName ?? "",
                    FullName = user.FullName ?? "",
                    CurrentRoles = roles,
                    RecommendedAction = "Открыть пользователя и назначить одну роль",
                });
            }
            else if (baseRoles.Count > 1)
            {
                diagnostics.UsersWithMultipleRoles++;
                diagnostics.UsersWithMultipleRolesList.Add(new UserDiagnosticInfo
                {
                    UserId = user.Id,
                    UserName = user.UserName ?? "",
                    FullName = user.FullName ?? "",
                    CurrentRoles = baseRoles,
                    RecommendedAction = "Оставить одну роль",
                });
            }
            else
            {
                var baseRole = baseRoles[0];
                if (baseRole != nameof(UserRole.SystemAdmin) && user.BranchId == null)
                {
                    missingBranchCount++;
                    diagnostics.UsersMissingBranchList.Add(new UserDiagnosticInfo
                    {
                        UserId = user.Id,
                        UserName = user.UserName ?? "",
                        FullName = user.FullName ?? "",
                        CurrentRoles = [baseRole],
                        RecommendedAction = "Открыть пользователя и указать филиал",
                    });
                }
            }
        }

        diagnostics.UsersMissingBranch = missingBranchCount;

        diagnostics.MissingTemplates = diagnostics.Roles
            .Where(r => r.ExpectedPermissionCodes != null)
            .Sum(r => r.MissingPermissionCodes);

        diagnostics.UnexpectedTemplates = diagnostics.Roles
            .Sum(r => r.UnexpectedPermissionCodes);

        var hasIssues = diagnostics.UsersWithoutBaseRole > 0
            || diagnostics.UsersWithMultipleRoles > 0
            || diagnostics.UsersMissingBranch > 0
            || diagnostics.MissingTemplates > 0
            || diagnostics.UnexpectedTemplates > 0;

        var allTemplatesFilled = diagnostics.Roles.All(r => r.TemplateCount > 0);

        diagnostics.Status = hasIssues
            ? "Проблемы обнаружены"
            : (allTemplatesFilled ? "Готово" : "Шаблоны не заполнены");

        return diagnostics;
    }
}
