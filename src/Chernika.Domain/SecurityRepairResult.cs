namespace Chernika.Domain;

public class SecurityRepairResult
{
    public int RolesCreated { get; set; }
    public int TemplatesAdded { get; set; }
    public int ViewerToGuestMigrated { get; set; }
    public int CacheCleared { get; set; }
    public List<UserDiagnosticInfo> UsersWithoutBaseRole { get; set; } = new();
    public List<UserDiagnosticInfo> UsersWithMultipleBaseRoles { get; set; } = new();
    public List<UserDiagnosticInfo> UsersMissingBranch { get; set; } = new();
}

public class UserDiagnosticInfo
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public List<string> CurrentRoles { get; set; } = new();
    public string? RecommendedAction { get; set; }
}

public class SecurityDiagnosticsDto
{
    public List<RoleDiagnosticsDto> Roles { get; set; } = new();
    public int TotalActiveUsers { get; set; }
    public int UsersWithoutBaseRole { get; set; }
    public int UsersWithMultipleRoles { get; set; }
    public int UsersMissingBranch { get; set; }
    public int MissingTemplates { get; set; }
    public int UnexpectedTemplates { get; set; }
    public int InvalidTemplates { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<UserDiagnosticInfo> UsersWithoutBaseRoleList { get; set; } = new();
    public List<UserDiagnosticInfo> UsersWithMultipleRolesList { get; set; } = new();
    public List<UserDiagnosticInfo> UsersMissingBranchList { get; set; } = new();
}

public class RoleDiagnosticsDto
{
    public string RoleName { get; set; } = string.Empty;
    public int TemplateCount { get; set; }
    public int ExpectedTemplateCount { get; set; }
    public int ActiveUserCount { get; set; }
    public IReadOnlySet<string>? PermissionCodes { get; set; }
    public IReadOnlySet<string>? ExpectedPermissionCodes { get; set; }
    public int MissingPermissionCodes { get; set; }
    public int UnexpectedPermissionCodes { get; set; }
}

public interface ISecurityDataRepairService
{
    Task<SecurityRepairResult> RepairAsync(CancellationToken ct = default);
    Task<SecurityDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default);
}
