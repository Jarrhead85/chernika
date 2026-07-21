using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class UserManagementService
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly ICurrentUserService _currentUser;
    private readonly IPermissionService _permissions;
    private readonly AuditService _audit;

    public UserManagementService(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        AuditService audit)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _currentUser = currentUser;
        _permissions = permissions;
        _audit = audit;
    }

    public async Task<List<UserListItem>> GetUsersAsync()
    {
        var users = await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();
        var result = new List<UserListItem>();
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            result.Add(new UserListItem
            {
                Id = u.Id,
                UserName = u.UserName ?? "",
                FullName = u.FullName ?? "",
                Position = u.Position ?? "",
                RoleName = roles.FirstOrDefault() ?? "",
                IsActive = u.IsActive,
                BranchId = u.BranchId,
                CreatedAt = u.CreatedAt,
            });
        }
        return result;
    }

    public async Task<(bool Success, string? Error)> CreateUserAsync(string userName, string password, string fullName, string position, string roleName)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (string.IsNullOrWhiteSpace(userName))
            return (false, "Логин обязателен");
        if (string.IsNullOrWhiteSpace(password))
            return (false, "Пароль обязателен");
        if (string.IsNullOrWhiteSpace(roleName))
            return (false, "Роль обязательна");

        if (!await _roleManager.RoleExistsAsync(roleName))
            return (false, $"Роль «{roleName}» не существует");

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@chernika.local",
            FullName = fullName,
            Position = position,
            IsActive = true,
            EmailConfirmed = true,
        };

        var createResult = await _userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
            return (false, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        await _userManager.AddToRoleAsync(user, roleName);

        await _audit.LogAsync("User", user.Id, "Created", actorId, $"Создан пользователь {userName} с ролью {roleName}");

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateUserAsync(string userId, string fullName, string position, string roleName)
    {
        var actorId = _currentUser.GetRequiredUserId();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (false, "Пользователь не найден");

        if (!await _roleManager.RoleExistsAsync(roleName))
            return (false, $"Роль «{roleName}» не существует");

        user.FullName = fullName;
        user.Position = position;
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
            return (false, string.Join("; ", updateResult.Errors.Select(e => e.Description)));

        var currentRoles = await _userManager.GetRolesAsync(user);
        var oldRole = currentRoles.FirstOrDefault() ?? "";

        if (oldRole != roleName)
        {
            if (oldRole == nameof(UserRole.SystemAdmin) && !await HasOtherActiveSystemAdminAsync(userId))
                return (false, "Нельзя изменить роль единственного активного системного администратора");

            await _userManager.RemoveFromRolesAsync(user, currentRoles);
            await _userManager.AddToRoleAsync(user, roleName);
            _permissions.InvalidateCache(userId);

            await _audit.LogAsync("User", userId, "RoleChanged", actorId, $"Роль изменена с {oldRole} на {roleName}");
        }

        await _audit.LogAsync("User", userId, "Updated", actorId, $"Обновлены данные пользователя {user.UserName}");

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ToggleBlockAsync(string userId)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (userId == actorId.ToString())
            return (false, "Нельзя заблокировать самого себя");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (false, "Пользователь не найден");

        if (user.IsActive)
        {
            var roles = await _userManager.GetRolesAsync(user);
            if (roles.Contains(nameof(UserRole.SystemAdmin)) && !await HasOtherActiveSystemAdminAsync(userId))
                return (false, "Нельзя деактивировать единственного активного системного администратора");
        }

        user.IsActive = !user.IsActive;

        if (user.IsActive)
        {
            await _userManager.SetLockoutEndDateAsync(user, null);
            await _userManager.SetLockoutEnabledAsync(user, false);
        }
        else
        {
            await _userManager.SetLockoutEnabledAsync(user, true);
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        }

        await _userManager.UpdateAsync(user);
        _permissions.InvalidateCache(userId);

        await _audit.LogAsync("User", userId, user.IsActive ? "Unblocked" : "Blocked", actorId);

        return (true, null);
    }

    public async Task<List<UserPermissionOverrideDto>> GetOverridesAsync(string userId)
    {
        var overrides = await _db.UserPermissionOverrides
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.PermissionCode)
            .ToListAsync();

        return overrides.Select(o => new UserPermissionOverrideDto
        {
            Id = o.Id,
            UserId = o.UserId,
            PermissionCode = o.PermissionCode,
            IsGranted = o.IsGranted,
            Reason = o.Reason,
            GrantedByUserId = o.GrantedByUserId,
            CreatedAt = o.CreatedAt,
            UpdatedAt = o.UpdatedAt,
        }).ToList();
    }

    public async Task<(bool Success, string? Error)> SetOverrideAsync(string userId, string permissionCode, bool isGranted, string? reason)
    {
        var actorId = _currentUser.GetRequiredUserId();

        var existing = await _db.UserPermissionOverrides
            .FirstOrDefaultAsync(x => x.UserId == userId && x.PermissionCode == permissionCode);

        if (existing != null)
        {
            existing.IsGranted = isGranted;
            existing.Reason = reason;
            existing.GrantedByUserId = actorId.ToString();
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PermissionCode = permissionCode,
                IsGranted = isGranted,
                Reason = reason,
                GrantedByUserId = actorId.ToString(),
                CreatedAt = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync();
        _permissions.InvalidateCache(userId);
        await _audit.LogAsync("UserPermissionOverride", userId, isGranted ? "OverrideGranted" : "OverrideDenied", actorId, $"{permissionCode} = {isGranted}");

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> RemoveOverrideAsync(string userId, string permissionCode)
    {
        var actorId = _currentUser.GetRequiredUserId();

        var existing = await _db.UserPermissionOverrides
            .FirstOrDefaultAsync(x => x.UserId == userId && x.PermissionCode == permissionCode);

        if (existing == null)
            return (false, "Переопределение не найдено");

        _db.UserPermissionOverrides.Remove(existing);
        await _db.SaveChangesAsync();
        _permissions.InvalidateCache(userId);
        await _audit.LogAsync("UserPermissionOverride", userId, "OverrideRemoved", actorId, permissionCode);

        return (true, null);
    }

    private async Task<bool> HasOtherActiveSystemAdminAsync(string excludeUserId)
    {
        var sysAdmins = await _userManager.GetUsersInRoleAsync(nameof(UserRole.SystemAdmin));
        return sysAdmins.Any(u => u.IsActive && u.Id != excludeUserId);
    }
}

public class UserListItem
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public Guid? BranchId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserPermissionOverrideDto
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string PermissionCode { get; set; } = string.Empty;
    public bool IsGranted { get; set; }
    public string? Reason { get; set; }
    public string GrantedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
