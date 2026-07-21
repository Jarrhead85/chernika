namespace Chernika.Domain.Entities;

public class RolePermissionTemplate
{
    public Guid Id { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string PermissionCode { get; set; } = string.Empty;
}
