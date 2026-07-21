using Chernika.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Chernika.Infrastructure.Services;

public class PermissionRequirement : IAuthorizationRequirement
{
    public IReadOnlyList<string> PermissionCodes { get; }
    public PermissionRequirement(params string[] permissionCodes) => PermissionCodes = permissionCodes;
}

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionService _permissions;
    private readonly IHttpContextAccessor _httpContext;

    public PermissionAuthorizationHandler(IPermissionService permissions, IHttpContextAccessor httpContext)
    {
        _permissions = permissions;
        _httpContext = httpContext;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var userId = _httpContext.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return;

        foreach (var code in requirement.PermissionCodes)
        {
            if (await _permissions.HasPermissionAsync(userId, code))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}
