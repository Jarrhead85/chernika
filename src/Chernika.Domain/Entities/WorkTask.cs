namespace Chernika.Domain.Entities;

public class WorkTask
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string AssigneeId { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
