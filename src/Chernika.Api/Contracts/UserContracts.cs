using Chernika.Infrastructure.Services;

namespace Chernika.Api.Contracts;

public record UserDto(
    string Id,
    string UserName,
    string FullName,
    string Position,
    string RoleName,
    bool IsActive,
    bool IsDeleted,
    Guid? BranchId,
    DateTime CreatedAt,
    DateTime? DeletedAt
);

public record CreateUserRequest(string UserName, string Password, string FullName, string Position, string Role, Guid? BranchId = null);
public record UpdateUserRequest(string FullName, string Position, string Role, Guid? BranchId = null);
public record DeleteUserRequest(string Reason);
public record RestoreUserRequest(string Role, Guid? BranchId);

public record UserOverrideDto(
    Guid Id,
    string UserId,
    string PermissionCode,
    bool IsGranted,
    string? Reason,
    string GrantedByUserId,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record SetOverrideRequest(string PermissionCode, bool IsGranted, string? Reason);
public record PermissionOverrideRequest(string PermissionCode, string Reason);

public static class UserMapper
{
    public static UserDto ToDto(UserListItem item) => new(
        item.Id, item.UserName, item.FullName, item.Position,
        item.RoleName, item.IsActive, item.IsDeleted, item.BranchId, item.CreatedAt, item.DeletedAt
    );

    public static UserOverrideDto ToDto(UserPermissionOverrideDto item) => new(
        item.Id, item.UserId, item.PermissionCode, item.IsGranted,
        item.Reason, item.GrantedByUserId, item.CreatedAt, item.UpdatedAt
    );
}
