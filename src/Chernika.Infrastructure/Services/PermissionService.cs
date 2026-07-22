using System.Collections.Frozen;
using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Chernika.Infrastructure.Services;

public class PermissionService : IPermissionService
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICurrentUserService _currentUser;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PermissionService(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        ICurrentUserService currentUser,
        IMemoryCache cache)
    {
        _db = db;
        _userManager = userManager;
        _currentUser = currentUser;
        _cache = cache;
    }

    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(string userId, CancellationToken ct = default)
    {
        var cacheKey = $"permissions:{userId}";
        if (_cache.TryGetValue<IReadOnlySet<string>>(cacheKey, out var cached))
            return cached!;

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null || !user.IsActive)
            return FrozenSet<string>.Empty;

        var roles = await _userManager.GetRolesAsync(user);
        var baseRole = roles.FirstOrDefault(r =>
            r == nameof(UserRole.SystemAdmin) ||
            r == nameof(UserRole.NormAdmin) ||
            r == nameof(UserRole.Operator) ||
            r == nameof(UserRole.HeadOfDepartment) ||
            r == nameof(UserRole.Guest));

        HashSet<string> result;
        if (baseRole == nameof(UserRole.SystemAdmin))
        {
            result = new HashSet<string>(PermissionCodes.All);
        }
        else
        {
            result = new HashSet<string>();
            if (baseRole != null)
            {
                var templatePerms = await _db.RolePermissionTemplates
                    .Where(x => x.RoleName == baseRole)
                    .Select(x => x.PermissionCode)
                    .ToListAsync(ct);
                result.UnionWith(templatePerms);
            }

            var overrides = await _db.UserPermissionOverrides
                .Where(x => x.UserId == userId)
                .ToListAsync(ct);

            foreach (var o in overrides)
            {
                if (o.IsGranted)
                    result.Add(o.PermissionCode);
                else
                    result.Remove(o.PermissionCode);
            }
        }

        var frozen = result.ToFrozenSet();
        _cache.Set(cacheKey, frozen, CacheDuration);
        return frozen;
    }

    public async Task<bool> HasPermissionAsync(string userId, string permissionCode, CancellationToken ct = default)
    {
        var perms = await GetEffectivePermissionsAsync(userId, ct);
        return perms.Contains(permissionCode);
    }

    public async Task<bool> HasPermissionAsync(string userId, params string[] permissionCodes)
    {
        var perms = await GetEffectivePermissionsAsync(userId);
        foreach (var code in permissionCodes)
        {
            if (perms.Contains(code)) return true;
        }
        return false;
    }

    public async Task DemandPermissionAsync(string permissionCode, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        if (userId == null)
            throw new UnauthorizedAccessException("Пользователь не аутентифицирован.");

        var has = await HasPermissionAsync(userId.Value.ToString(), permissionCode, ct);
        if (!has)
            throw new UnauthorizedAccessException($"Недостаточно прав. Требуется разрешение: {permissionCode}");
    }

    public void InvalidateCache(string userId)
    {
        _cache.Remove($"permissions:{userId}");
    }

    public void InvalidateAllCache()
    {
        // no built-in way to remove by pattern; rely on expiry
    }
}
