namespace Chernika.Domain.Entities;

public class UserPermissionOverride
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string PermissionCode { get; set; } = string.Empty;
    public bool IsGranted { get; set; }
    public string? Reason { get; set; }
    public string GrantedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
