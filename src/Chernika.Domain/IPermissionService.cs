namespace Chernika.Domain;

public interface IPermissionService
{
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(string userId, CancellationToken ct = default);
    Task<bool> HasPermissionAsync(string userId, string permissionCode, CancellationToken ct = default);
    Task DemandPermissionAsync(string permissionCode, CancellationToken ct = default);
    void InvalidateCache(string userId);
}
