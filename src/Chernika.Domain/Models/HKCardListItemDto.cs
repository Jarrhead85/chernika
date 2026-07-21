using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public class HKCardListItemDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = "";
    public string Version { get; init; } = "";
    public HKCardStatus Status { get; init; }
    public HKObjectLevel ObjectLevel { get; init; }
    public Guid BranchId { get; init; }
    public string? BranchName { get; init; }
    public string? ObjectCode { get; init; }
    public string? ObjectName { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ApprovedDate { get; init; }
}
