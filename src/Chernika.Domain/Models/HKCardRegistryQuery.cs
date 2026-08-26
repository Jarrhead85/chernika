using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public enum HKCardExpirationFilter
{
    All = 0,
    Expiring90Days = 1,
    Expiring30Days = 2,
    Expired = 3
}

public sealed class HKCardRegistryQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? SearchText { get; set; }
    public HKCardStatus? Status { get; set; }
    public HKObjectLevel? ObjectLevel { get; set; }
    public Guid? BranchId { get; set; }
    public bool OnlyMine { get; set; }
    public bool RequiresMyAction { get; set; }
    public string? AuthorId { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }
    public DateTime? ApprovedFrom { get; set; }
    public DateTime? ApprovedTo { get; set; }
    public HKCardExpirationFilter ExpirationFilter { get; set; } = HKCardExpirationFilter.All;
    public bool? HasPdf { get; set; }
    public string SortBy { get; set; } = "created";
    public bool SortDescending { get; set; } = true;
}

public sealed class HKCardRegistryListItemDto
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
    public DateTime? EffectiveDate { get; init; }
    public DateTime? ExpirationDate { get; init; }
    public string? AuthorId { get; init; }
    public string? AuthorName { get; set; }
    public string? RequestOrganization { get; init; }
    public string? IncomingLetterNumber { get; init; }
    public string? OutgoingLetterNumber { get; init; }
    public bool HasPdf { get; init; }
}

public sealed class HKCardRegistryAuthorDto
{
    public string Id { get; init; } = "";
    public string FullName { get; init; } = "";
}
