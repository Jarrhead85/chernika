using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public enum CompositionRegistryLevel
{
    Aggregate = 1,
    EquipmentModel = 2,
    Complex = 3,
    All = 4
}

public enum CompositionPresenceFilter
{
    All = 0,
    WithComposition = 1,
    WithoutComposition = 2
}

public sealed class CompositionRegistryQuery
{
    public CompositionRegistryLevel Level { get; init; } = CompositionRegistryLevel.Aggregate;
    public string? SearchText { get; init; }
    public ProductCompositionStatus? Status { get; init; }
    public CompositionPresenceFilter Presence { get; init; } = CompositionPresenceFilter.All;
    public bool ShowArchivedVersions { get; init; }
    public bool SearchAllLevels { get; init; }
    public Guid? BranchId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string SortBy { get; init; } = "name";
    public bool SortDescending { get; init; }
}

public sealed class CompositionRegistryRow
{
    public CompositionRegistryLevel Level { get; init; }
    public Guid ObjectId { get; init; }
    public string ObjectCode { get; init; } = "";
    public string ObjectName { get; init; } = "";
    public Guid? CompositionId { get; init; }
    public string? Version { get; init; }
    public ProductCompositionStatus? Status { get; init; }
    public bool IsHistoricalArchiveRow { get; init; }
    public bool HasArchivedVersions { get; init; }
    public int PartCount { get; init; }
    public int AggregateCount { get; init; }
    public int NodeCount { get; init; }
    public int ItemCount { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
