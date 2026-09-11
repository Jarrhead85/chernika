namespace Chernika.Domain.Models;

/// <summary>Профиль текущего пользователя (только отображаемые/безопасные данные).</summary>
public sealed record MyAccountDto(
    string Id,
    string Login,
    string? FullName,
    string? Position,
    string BaseRoleDisplay,
    string? BranchName,
    DateTime CreatedAt,
    bool IsActive,
    bool HasAvatar,
    long AvatarVersion,
    bool MustChangePassword);

/// <summary>Запрос изменения собственного профиля (доступно только ФИО).</summary>
public sealed class UpdateMyProfileRequest
{
    public string FullName { get; set; } = string.Empty;
}

/// <summary>Запрос смены собственного пароля.</summary>
public sealed class ChangeMyPasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Содержимое аватара текущего пользователя.</summary>
public sealed record MyAvatarContentDto(string ContentType, byte[] Content);

/// <summary>Запрос сброса пароля администратором.</summary>
public sealed class ResetUserPasswordByAdminRequest
{
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Итог сброса пароля администратором (без секретов).</summary>
public sealed record ResetUserPasswordResult(bool Success, string? Error);
