namespace Chernika.Domain;

public class SecurityRepairResult
{
    public int RolesCreated { get; set; }
    public int TemplatesUpserted { get; set; }
    public int ViewerToGuestMigrated { get; set; }
    public int PermissionsCacheCleared { get; set; }
    public List<UserDiagnosticInfo> UsersWithoutBaseRole { get; set; } = new();
    public List<UserDiagnosticInfo> UsersWithMultipleBaseRoles { get; set; } = new();
    public List<UserDiagnosticInfo> UsersRepaired { get; set; } = new();
}

public class UserDiagnosticInfo
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<string> CurrentRoles { get; set; } = new();
}

public class SecurityDiagnosticsDto
{
    public List<RoleDiagnosticsDto> Roles { get; set; } = new();
    public int TotalActiveUsers { get; set; }
    public int UsersWithoutBaseRole { get; set; }
    public int UsersWithMultipleRoles { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class RoleDiagnosticsDto
{
    public string RoleName { get; set; } = string.Empty;
    public int TemplateCount { get; set; }
    public int ActiveUserCount { get; set; }
    public IReadOnlySet<string>? PermissionCodes { get; set; }
}

public interface ISecurityDataRepairService
{
    Task<SecurityRepairResult> RepairAsync(CancellationToken ct = default);
    Task<SecurityDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default);
}
