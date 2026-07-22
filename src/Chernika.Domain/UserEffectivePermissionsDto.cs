namespace Chernika.Domain;

public class UserEffectivePermissionDto
{
    public string Code { get; set; } = null!;
    public string Module { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public bool GrantedByRole { get; set; }
    public bool? OverrideIsGranted { get; set; }
    public bool IsEffective { get; set; }
    public string Source { get; set; } = null!;
    public string? OverrideReason { get; set; }
}

public class UserEffectivePermissionsDto
{
    public string UserId { get; set; } = null!;
    public string UserName { get; set; } = null!;
    public string BaseRole { get; set; } = null!;
    public List<UserEffectivePermissionDto> Permissions { get; set; } = new();
}
