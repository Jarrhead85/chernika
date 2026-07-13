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
        await DbSeeder.SeedAsync(db, userManager, roleManager);
    }
}
