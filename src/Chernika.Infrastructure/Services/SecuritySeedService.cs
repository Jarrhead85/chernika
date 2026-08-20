using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public static class SecuritySeedService
{
    public static async Task SeedAsync(AppDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        await MigrateViewerToGuestAsync(userManager, roleManager);

        foreach (var roleName in Enum.GetNames<UserRole>())
        {
            if (!await roleManager.RoleExistsAsync(roleName))
                await roleManager.CreateAsync(new IdentityRole(roleName));
        }

        var catalogCodes = PermissionCodes.All;
        var catalogDefined = PermissionCatalog.All.Select(p => p.Code).ToHashSet();
        if (!catalogCodes.SetEquals(catalogDefined))
            throw new InvalidOperationException(
                $"Invariant violated: PermissionCodes.All ({catalogCodes.Count} codes) != PermissionCatalog.All ({catalogDefined.Count} codes). " +
                $"Missing in catalog: {catalogCodes.Except(catalogDefined).FirstOrDefault() ?? "none"}. " +
                $"Extra in catalog: {catalogDefined.Except(catalogCodes).FirstOrDefault() ?? "none"}.");
        var codesToSeed = catalogCodes;

        await db.RolePermissionTemplates
            .Where(x => !codesToSeed.Contains(x.PermissionCode))
            .ExecuteDeleteAsync();

        var existing = await db.RolePermissionTemplates
            .Select(x => new { x.RoleName, x.PermissionCode })
            .ToListAsync();

        var existingSet = existing
            .GroupBy(x => x.RoleName)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToHashSet());

        foreach (var roleName in Enum.GetNames<UserRole>())
        {
            IEnumerable<string> permsForRole;
            if (roleName == nameof(UserRole.SystemAdmin))
            {
                permsForRole = codesToSeed;
            }
            else
            {
                permsForRole = RolePermissionDefaults.GetForRole(roleName);
            }

            existingSet.TryGetValue(roleName, out var existingCodes);

            foreach (var perm in permsForRole)
            {
                if (existingCodes == null || !existingCodes.Contains(perm))
                {
                    db.RolePermissionTemplates.Add(new RolePermissionTemplate
                    {
                        Id = Guid.NewGuid(),
                        RoleName = roleName,
                        PermissionCode = perm,
                    });
                }
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task MigrateViewerToGuestAsync(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        const string oldRole = "Viewer";
        const string newRole = "Guest";

        if (!await roleManager.RoleExistsAsync(oldRole))
            return;

        if (!await roleManager.RoleExistsAsync(newRole))
            await roleManager.CreateAsync(new IdentityRole(newRole));

        var viewers = await userManager.GetUsersInRoleAsync(oldRole);
        foreach (var user in viewers)
        {
            await userManager.RemoveFromRoleAsync(user, oldRole);
            await userManager.AddToRoleAsync(user, newRole);
        }

        var roleEntity = await roleManager.FindByNameAsync(oldRole);
        if (roleEntity != null)
            await roleManager.DeleteAsync(roleEntity);
    }
}

internal static class RolePermissionDefaults
{
    private static readonly Dictionary<string, string[]> RolePermissions = new()
    {
        [nameof(UserRole.SystemAdmin)] = PermissionCodes.All.ToArray(),
        [nameof(UserRole.NormAdmin)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKNodeCreate, PermissionCodes.HKNodeEditDraft, PermissionCodes.HKNodeSubmit,
            PermissionCodes.HKAggregateCreate, PermissionCodes.HKAggregateEditDraft, PermissionCodes.HKAggregateSubmit,
            PermissionCodes.HKEquipmentCreate, PermissionCodes.HKEquipmentEditDraft, PermissionCodes.HKEquipmentSubmit,
            PermissionCodes.HKComplexCreate, PermissionCodes.HKComplexEditDraft, PermissionCodes.HKComplexSubmit,
            PermissionCodes.HKReview, PermissionCodes.HKApprove, PermissionCodes.HKArchive,
            PermissionCodes.HKDeleteDraft, PermissionCodes.HKDeleteRevisionRequired,
            PermissionCodes.HKAttachmentView, PermissionCodes.HKAttachmentEdit,
            PermissionCodes.ReferenceView, PermissionCodes.ReferenceEdit,
            PermissionCodes.CompositionView, PermissionCodes.CompositionEdit,
            PermissionCodes.CompositionComplexCreate, PermissionCodes.CompositionComplexEditDraft, PermissionCodes.CompositionComplexSubmit, PermissionCodes.CompositionComplexReturnForRevision, PermissionCodes.CompositionComplexApprove, PermissionCodes.CompositionComplexCreateVersion,
            PermissionCodes.CompositionEquipmentModelCreate, PermissionCodes.CompositionEquipmentModelEditDraft, PermissionCodes.CompositionEquipmentModelSubmit, PermissionCodes.CompositionEquipmentModelReturnForRevision, PermissionCodes.CompositionEquipmentModelApprove, PermissionCodes.CompositionEquipmentModelCreateVersion,
            PermissionCodes.CompositionAggregateCreate, PermissionCodes.CompositionAggregateEditDraft, PermissionCodes.CompositionAggregateSubmit, PermissionCodes.CompositionAggregateReturnForRevision, PermissionCodes.CompositionAggregateApprove, PermissionCodes.CompositionAggregateCreateVersion,
            PermissionCodes.IndividualCardView, PermissionCodes.IndividualCardGenerate,
            PermissionCodes.ReportExport,
            PermissionCodes.AuditView,
            PermissionCodes.TaskView, PermissionCodes.TaskAssign,
            PermissionCodes.TaskComplete, PermissionCodes.TaskCancel,
            PermissionCodes.NotificationView, PermissionCodes.NotificationMarkRead,
        ],
        [nameof(UserRole.Operator)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKNodeCreate, PermissionCodes.HKNodeEditDraft, PermissionCodes.HKNodeSubmit,
            PermissionCodes.HKAttachmentView,
            PermissionCodes.HKDeleteDraft, PermissionCodes.HKDeleteRevisionRequired,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView, PermissionCodes.IndividualCardGenerate,
            PermissionCodes.ReportExport,
            PermissionCodes.TaskViewOwn,
            PermissionCodes.TaskView, PermissionCodes.TaskComplete,
            PermissionCodes.NotificationView, PermissionCodes.NotificationMarkRead,
        ],
        [nameof(UserRole.HeadOfDepartment)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKAttachmentView,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView,
            PermissionCodes.ReportExport,
            PermissionCodes.AuditView,
            PermissionCodes.TaskViewOwn,
            PermissionCodes.TaskView, PermissionCodes.TaskComplete, PermissionCodes.TaskAssign,
            PermissionCodes.NotificationView, PermissionCodes.NotificationMarkRead,
        ],
        [nameof(UserRole.Guest)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKAttachmentView,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView,
            PermissionCodes.TaskViewOwn,
            PermissionCodes.TaskView,
            PermissionCodes.NotificationView, PermissionCodes.NotificationMarkRead,
        ],
    };

    public static string[] GetForRole(string roleName) =>
        RolePermissions.TryGetValue(roleName, out var perms) ? perms : [];
}
