using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public static class DatabaseInit
{
    public static async Task InitializeAsync(AppDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        await db.Database.MigrateAsync();
        await MigrateViewerToGuestAsync(userManager, roleManager);
        await DbSeeder.SeedAsync(db, userManager, roleManager);
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
