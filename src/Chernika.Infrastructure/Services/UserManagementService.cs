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

    public async Task<List<UserListItem>> GetUsersAsync(string? statusFilter = null, string? roleFilter = null, string? search = null, int page = 1, int pageSize = 50)
    {
        var query = _userManager.Users.AsQueryable();

        if (statusFilter == "active")
            query = query.Where(u => u.IsActive && !u.IsDeleted);
        else if (statusFilter == "blocked")
            query = query.Where(u => !u.IsActive && !u.IsDeleted);
        else if (statusFilter == "deleted")
            query = query.Where(u => u.IsDeleted);
        else if (statusFilter != "all")
            query = query.Where(u => !u.IsDeleted);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(u =>
                (u.UserName != null && u.UserName.Contains(search)) ||
                (u.FullName != null && u.FullName.Contains(search)));

        var allFiltered = await query.OrderBy(u => u.UserName).ToListAsync();

        var userIds = allFiltered.Select(u => u.Id).ToList();
        var userRoles = await _db.UserRoles
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, RoleName = r.Name })
            .ToListAsync();

        var roleLookup = userRoles
            .GroupBy(ur => ur.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RoleName).FirstOrDefault());

        var result = new List<UserListItem>();
        foreach (var u in allFiltered)
        {
            roleLookup.TryGetValue(u.Id, out var baseRole);

            if (!string.IsNullOrEmpty(roleFilter) && baseRole != roleFilter)
                continue;

            result.Add(new UserListItem
            {
                Id = u.Id,
                UserName = u.UserName ?? "",
                FullName = u.DisplayNameSnapshot ?? u.FullName ?? "",
                Position = u.Position ?? "",
                RoleName = baseRole ?? "",
                IsActive = u.IsActive,
                IsDeleted = u.IsDeleted,
                BranchId = u.BranchId,
                CreatedAt = u.CreatedAt,
                DeletedAt = u.DeletedAt,
            });
        }

        return result.Skip((page - 1) * pageSize).Take(pageSize).ToList();
    }

    public async Task<int> GetUsersCountAsync(string? statusFilter = null, string? roleFilter = null, string? search = null)
    {
        var query = _userManager.Users.AsQueryable();

        if (statusFilter == "active")
            query = query.Where(u => u.IsActive && !u.IsDeleted);
        else if (statusFilter == "blocked")
            query = query.Where(u => !u.IsActive && !u.IsDeleted);
        else if (statusFilter == "deleted")
            query = query.Where(u => u.IsDeleted);
        else if (statusFilter != "all")
            query = query.Where(u => !u.IsDeleted);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(u =>
                (u.UserName != null && u.UserName.Contains(search)) ||
                (u.FullName != null && u.FullName.Contains(search)));

        return await query.CountAsync();
    }

    public async Task<List<BranchListItem>> GetBranchesAsync()
    {
        return await _db.Branches
            .OrderBy(b => b.Code)
            .Select(b => new BranchListItem { Id = b.Id, Name = b.Name, Code = b.Code })
            .ToListAsync();
    }

    public async Task<(bool Success, string? Error)> CreateUserAsync(string userName, string password, string fullName, string position, string roleName, Guid? branchId = null)
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

        var validRoles = Enum.GetNames<UserRole>();
        if (!validRoles.Contains(roleName))
            return (false, $"Роль «{roleName}» не является допустимой бизнес-ролью");

        if (roleName != nameof(UserRole.SystemAdmin) && branchId == null)
            return (false, "Филиал обязателен для роли отличной от SystemAdmin");

        if (branchId.HasValue && !await _db.Branches.AnyAsync(b => b.Id == branchId.Value))
            return (false, "Указанный филиал не существует");

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@chernika.local",
            FullName = fullName,
            Position = position,
            BranchId = branchId,
            IsActive = true,
            EmailConfirmed = true,
        };

        var createResult = await _userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
            return (false, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        await _userManager.AddToRoleAsync(user, roleName);

        await _audit.LogAsync("User", user.Id, "Created", actorId, $"Создан пользователь {userName} с ролью {roleName}, филиал={branchId}");

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateUserAsync(string userId, string fullName, string position, string roleName, Guid? branchId = null)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (actorId.ToString() == userId)
            return (false, "Нельзя изменять собственные данные через этот экран");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (false, "Пользователь не найден");

        if (user.IsDeleted)
            return (false, "Нельзя изменять удалённого пользователя");

        if (!await _roleManager.RoleExistsAsync(roleName))
            return (false, $"Роль «{roleName}» не существует");

        var validRoles = Enum.GetNames<UserRole>();
        if (!validRoles.Contains(roleName))
            return (false, $"Роль «{roleName}» не является допустимой бизнес-ролью");

        if (roleName != nameof(UserRole.SystemAdmin) && branchId == null)
            return (false, "Филиал обязателен для роли отличной от SystemAdmin");

        if (branchId.HasValue && !await _db.Branches.AnyAsync(b => b.Id == branchId.Value))
            return (false, "Указанный филиал не существует");

        user.FullName = fullName;
        user.Position = position;
        user.BranchId = branchId;
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
            return (false, string.Join("; ", updateResult.Errors.Select(e => e.Description)));

        var currentRoles = await _userManager.GetRolesAsync(user);
        var baseRole = currentRoles.FirstOrDefault(r =>
            r == nameof(UserRole.SystemAdmin) ||
            r == nameof(UserRole.NormAdmin) ||
            r == nameof(UserRole.Operator) ||
            r == nameof(UserRole.HeadOfDepartment) ||
            r == nameof(UserRole.Guest)) ?? "";

        if (baseRole != roleName)
        {
            if (baseRole == nameof(UserRole.SystemAdmin) && !await HasOtherActiveSystemAdminAsync(userId))
                return (false, "Нельзя изменить роль единственного активного системного администратора");

            await _userManager.RemoveFromRolesAsync(user, currentRoles);
            await _userManager.AddToRoleAsync(user, roleName);
            _permissions.InvalidateCache(userId);

            await _audit.LogAsync("User", userId, "RoleChanged", actorId, $"Роль изменена с {baseRole} на {roleName}");
        }

        await _audit.LogAsync("User", userId, "Updated", actorId, $"Обновлены данные пользователя {user.UserName}");

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ToggleBlockAsync(string userId)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (actorId.ToString() == userId)
            return (false, "Нельзя заблокировать самого себя");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (false, "Пользователь не найден");
        if (user.IsDeleted)
            return (false, "Нельзя блокировать удалённого пользователя");

        if (user.IsActive)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var isSysAdmin = roles.Contains(nameof(UserRole.SystemAdmin));
            if (isSysAdmin && !await HasOtherActiveSystemAdminAsync(userId))
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
        await _userManager.UpdateSecurityStampAsync(user);
        _permissions.InvalidateCache(userId);

        await _audit.LogAsync("User", userId, user.IsActive ? "Unblocked" : "Blocked", actorId);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteUserAsync(string userId, string reason)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (actorId.ToString() == userId)
            return (false, "Нельзя удалить самого себя");

        if (string.IsNullOrWhiteSpace(reason))
            return (false, "Причина обязательна");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (false, "Пользователь не найден");
        if (user.IsDeleted)
            return (false, "Пользователь уже удалён");

        var roles = await _userManager.GetRolesAsync(user);
        var isSysAdmin = roles.Contains(nameof(UserRole.SystemAdmin));
        if (isSysAdmin && !await HasOtherActiveSystemAdminAsync(userId))
            return (false, "Нельзя удалить единственного активного системного администратора");

        user.IsDeleted = true;
        user.IsActive = false;
        user.DeletedAt = DateTime.UtcNow;
        user.DeletedByUserId = actorId.ToString();
        user.DisplayNameSnapshot = user.DisplayNameSnapshot ?? user.FullName;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
            return (false, string.Join("; ", updateResult.Errors.Select(e => e.Description)));

        await _userManager.SetLockoutEnabledAsync(user, true);
        await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        await _userManager.UpdateSecurityStampAsync(user);

        var overrides = await _db.UserPermissionOverrides.Where(x => x.UserId == userId).ToListAsync();
        if (overrides.Count > 0)
        {
            _db.UserPermissionOverrides.RemoveRange(overrides);
            await _db.SaveChangesAsync();
        }

        _permissions.InvalidateCache(userId);

        await _audit.LogAsync("User", userId, "Deleted", actorId, $"Причина: {reason}");

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> RestoreUserAsync(string userId, string roleName, Guid? branchId)
    {
        var actorId = _currentUser.GetRequiredUserId();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (false, "Пользователь не найден");
        if (!user.IsDeleted)
            return (false, "Пользователь не удалён");

        if (string.IsNullOrWhiteSpace(roleName))
            return (false, "Роль обязательна");

        var validRoles = Enum.GetNames<UserRole>();
        if (!validRoles.Contains(roleName))
            return (false, $"Роль «{roleName}» не является допустимой бизнес-ролью");

        if (roleName != nameof(UserRole.SystemAdmin) && branchId == null)
            return (false, "Филиал обязателен для роли отличной от SystemAdmin");

        user.IsDeleted = false;
        user.IsActive = true;
        user.DeletedAt = null;
        user.DeletedByUserId = null;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
            return (false, string.Join("; ", updateResult.Errors.Select(e => e.Description)));

        await _userManager.SetLockoutEndDateAsync(user, null);
        await _userManager.SetLockoutEnabledAsync(user, false);
        await _userManager.UpdateSecurityStampAsync(user);

        var currentRoles = await _userManager.GetRolesAsync(user);
        if (currentRoles.Any())
            await _userManager.RemoveFromRolesAsync(user, currentRoles);
        await _userManager.AddToRoleAsync(user, roleName);

        if (branchId.HasValue)
            user.BranchId = branchId;

        await _userManager.UpdateAsync(user);
        _permissions.InvalidateCache(userId);

        await _audit.LogAsync("User", userId, "Restored", actorId, $"Роль: {roleName}, филиал: {branchId}");

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

    public async Task<UserEffectivePermissionsDto?> GetEffectivePermissionsAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return null;

        var roles = await _userManager.GetRolesAsync(user);
        var baseRole = roles.FirstOrDefault(r =>
            r == nameof(UserRole.SystemAdmin) ||
            r == nameof(UserRole.NormAdmin) ||
            r == nameof(UserRole.Operator) ||
            r == nameof(UserRole.HeadOfDepartment) ||
            r == nameof(UserRole.Guest));

        var isSystemAdmin = baseRole == nameof(UserRole.SystemAdmin);
        var templatePerms = isSystemAdmin
            ? PermissionCodes.All.ToHashSet()
            : (await _db.RolePermissionTemplates
                .Where(x => x.RoleName == baseRole)
                .Select(x => x.PermissionCode)
                .ToListAsync()).ToHashSet();

        var overrides = await _db.UserPermissionOverrides
            .Where(x => x.UserId == userId)
            .ToDictionaryAsync(x => x.PermissionCode);

        var result = new UserEffectivePermissionsDto
        {
            UserId = userId,
            UserName = user.UserName ?? "",
            BaseRole = baseRole ?? "",
            Permissions = new List<UserEffectivePermissionDto>(),
        };

        foreach (var def in PermissionCatalog.All)
        {
            var grantedByRole = templatePerms.Contains(def.Code);
            overrides.TryGetValue(def.Code, out var overrideEntry);

            bool isEffective;
            string source;

            if (isSystemAdmin)
            {
                isEffective = true;
                source = "SystemAdmin";
            }
            else if (overrideEntry != null)
            {
                isEffective = overrideEntry.IsGranted;
                source = overrideEntry.IsGranted ? "Grant" : "Deny";
            }
            else
            {
                isEffective = grantedByRole;
                source = grantedByRole ? "Role" : "";
            }

            result.Permissions.Add(new UserEffectivePermissionDto
            {
                Code = def.Code,
                Module = def.Module,
                Name = def.Name,
                Description = def.Description,
                GrantedByRole = grantedByRole,
                OverrideIsGranted = overrideEntry?.IsGranted,
                IsEffective = isEffective,
                Source = source,
                OverrideReason = overrideEntry?.Reason,
            });
        }

        return result;
    }

    public async Task<(UserEffectivePermissionsDto? Result, string? Error)> GrantPermissionAsync(string userId, string permissionCode, string reason)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actorUserId = actorId.ToString();

        if (actorUserId == userId)
            return (null, "Нельзя изменять полномочия самого себя");

        if (!PermissionCodes.All.Contains(permissionCode))
            return (null, $"Неизвестный код полномочия: {permissionCode}");

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            return (null, "Причина обязательна и не может превышать 500 символов");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (null, "Пользователь не найден");
        if (user.IsDeleted)
            return (null, "Пользователь удалён");
        if (!user.IsActive)
            return (null, "Пользователь заблокирован");

        var userRoles = await _userManager.GetRolesAsync(user);
        if (userRoles.Contains(nameof(UserRole.SystemAdmin)))
            return (null, "Нельзя менять полномочия системного администратора");

        var existing = await _db.UserPermissionOverrides
            .FirstOrDefaultAsync(x => x.UserId == userId && x.PermissionCode == permissionCode);

        if (existing != null && existing.IsGranted)
            return (null, "Право уже индивидуально разрешено");

        var oldState = existing != null ? (existing.IsGranted ? "Grant" : "Deny") : null;

        if (existing != null)
        {
            existing.IsGranted = true;
            existing.Reason = reason;
            existing.GrantedByUserId = actorUserId;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PermissionCode = permissionCode,
                IsGranted = true,
                Reason = reason,
                GrantedByUserId = actorUserId,
                CreatedAt = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync();
        _permissions.InvalidateCache(userId);

        await _audit.LogAsync("UserPermissionOverride", userId, "OverrideGranted", actorId,
            $"Code={permissionCode}, Old={oldState}, New=Grant, Reason={reason}");

        var result = await GetEffectivePermissionsAsync(userId);
        return (result, null);
    }

    public async Task<(UserEffectivePermissionsDto? Result, string? Error)> DenyPermissionAsync(string userId, string permissionCode, string reason)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actorUserId = actorId.ToString();

        if (actorUserId == userId)
            return (null, "Нельзя изменять полномочия самого себя");

        if (!PermissionCodes.All.Contains(permissionCode))
            return (null, $"Неизвестный код полномочия: {permissionCode}");

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            return (null, "Причина обязательна и не может превышать 500 символов");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (null, "Пользователь не найден");
        if (user.IsDeleted)
            return (null, "Пользователь удалён");
        if (!user.IsActive)
            return (null, "Пользователь заблокирован");

        var userRoles = await _userManager.GetRolesAsync(user);
        if (userRoles.Contains(nameof(UserRole.SystemAdmin)))
            return (null, "Нельзя менять полномочия системного администратора");

        var existing = await _db.UserPermissionOverrides
            .FirstOrDefaultAsync(x => x.UserId == userId && x.PermissionCode == permissionCode);

        if (existing != null && !existing.IsGranted)
            return (null, "Право уже индивидуально запрещено");

        var oldState = existing != null ? (existing.IsGranted ? "Grant" : "Deny") : null;

        if (existing != null)
        {
            existing.IsGranted = false;
            existing.Reason = reason;
            existing.GrantedByUserId = actorUserId;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PermissionCode = permissionCode,
                IsGranted = false,
                Reason = reason,
                GrantedByUserId = actorUserId,
                CreatedAt = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync();
        _permissions.InvalidateCache(userId);

        await _audit.LogAsync("UserPermissionOverride", userId, "OverrideDenied", actorId,
            $"Code={permissionCode}, Old={oldState}, New=Deny, Reason={reason}");

        var result = await GetEffectivePermissionsAsync(userId);
        return (result, null);
    }

    public async Task<(UserEffectivePermissionsDto? Result, string? Error)> RevokePermissionAsync(string userId, string permissionCode)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actorUserId = actorId.ToString();

        if (actorUserId == userId)
            return (null, "Нельзя изменять полномочия самого себя");

        if (!PermissionCodes.All.Contains(permissionCode))
            return (null, $"Неизвестный код полномочия: {permissionCode}");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return (null, "Пользователь не найден");

        var userRoles = await _userManager.GetRolesAsync(user);
        if (userRoles.Contains(nameof(UserRole.SystemAdmin)))
            return (null, "Нельзя менять полномочия системного администратора");

        var existing = await _db.UserPermissionOverrides
            .FirstOrDefaultAsync(x => x.UserId == userId && x.PermissionCode == permissionCode);

        if (existing == null)
            return (null, "Индивидуальное решение не найдено");

        var oldState = existing.IsGranted ? "Grant" : "Deny";

        _db.UserPermissionOverrides.Remove(existing);
        await _db.SaveChangesAsync();
        _permissions.InvalidateCache(userId);

        await _audit.LogAsync("UserPermissionOverride", userId, "OverrideRevoked", actorId,
            $"Code={permissionCode}, Old={oldState}, New=Revoke");

        var result = await GetEffectivePermissionsAsync(userId);
        return (result, null);
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
    public bool IsDeleted { get; set; }
    public Guid? BranchId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class BranchListItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
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
