using Chernika.Domain;

namespace Chernika.Infrastructure.Dtos;

public class AuditLogDisplayDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }

    public string ActionCode { get; set; } = string.Empty;
    public string ActionDisplay { get; set; } = string.Empty;
    public AuditSeverity ActionSeverity { get; set; }

    public string EntityTypeCode { get; set; } = string.Empty;
    public string EntityTypeDisplay { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string EntityDisplayName { get; set; } = string.Empty;
    public bool IsEntitySnapshotMissing { get; set; }

    public string ActorFullName { get; set; } = string.Empty;
    public string ActorLogin { get; set; } = string.Empty;
    public string DetailsDisplay { get; set; } = string.Empty;
}
