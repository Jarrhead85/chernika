namespace Chernika.Domain;

public interface IPermissionService
{
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(string userId, CancellationToken ct = default);
    Task<bool> HasPermissionAsync(string userId, string permissionCode, CancellationToken ct = default);
    Task<bool> HasPermissionAsync(string userId, params string[] permissionCodes);
    Task DemandPermissionAsync(string permissionCode, CancellationToken ct = default);

    /// <summary>
    /// Защищённое системное исключение: пользователь имеет роль SystemAdmin.
    /// </summary>
    /// <remarks>
    /// Определяется здесь, а не размазано по сервисам и страницам: иначе одна
    /// опечатка в строковом литерале тихо отключала бы исключение в части
    /// функционала. Исключение снимает только ограничения организации и шаблона
    /// роли и не отменяет бизнес-правила.
    /// </remarks>
    Task<bool> IsSystemAdminAsync(string userId, CancellationToken ct = default);

    /// <summary>То же для текущего пользователя; без пользователя — false.</summary>
    Task<bool> IsSystemAdminAsync(CancellationToken ct = default);

    void InvalidateCache(string userId);
}
