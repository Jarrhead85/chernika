using Chernika.Infrastructure.Services;

namespace Chernika.Api.Contracts;

public record UserDto(
    string Id,
    string UserName,
    string FullName,
    string Position,
    string RoleName,
    bool IsActive,
    Guid? BranchId,
    DateTime CreatedAt
);

public record CreateUserRequest(string UserName, string Password, string FullName, string Position, string Role);
public record UpdateUserRequest(string FullName, string Position, string Role);

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

public static class UserMapper
{
    public static UserDto ToDto(UserListItem item) => new(
        item.Id, item.UserName, item.FullName, item.Position,
        item.RoleName, item.IsActive, item.BranchId, item.CreatedAt
    );

    public static UserOverrideDto ToDto(UserPermissionOverrideDto item) => new(
        item.Id, item.UserId, item.PermissionCode, item.IsGranted,
        item.Reason, item.GrantedByUserId, item.CreatedAt, item.UpdatedAt
    );
}
