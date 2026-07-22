using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public static class DatabaseInit
{
    public static async Task InitializeAsync(AppDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, bool seedDemoData = false)
    {
        await db.Database.MigrateAsync();
        await SecuritySeedService.SeedAsync(db, userManager, roleManager);

        if (seedDemoData)
        {
            await DemoDataSeeder.SeedAsync(db, userManager);
        }
    }
}
