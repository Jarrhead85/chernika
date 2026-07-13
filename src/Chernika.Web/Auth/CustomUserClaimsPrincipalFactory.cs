using System.Security.Claims;
using Chernika.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Chernika.Web.Auth;

public class CustomUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public CustomUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrEmpty(user.FullName))
            identity.AddClaim(new Claim("FullName", user.FullName));
        if (!string.IsNullOrEmpty(user.Position))
            identity.AddClaim(new Claim("Position", user.Position));
        if (user.BranchId.HasValue)
            identity.AddClaim(new Claim("BranchId", user.BranchId.Value.ToString()));
        return identity;
    }
}
