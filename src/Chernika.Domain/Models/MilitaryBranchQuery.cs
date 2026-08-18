namespace Chernika.Domain.Models;

public sealed class MilitaryBranchQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 15;

    public string? Search { get; init; }
    public string? ArmedForcesType { get; init; }
    public bool? ShowDeleted { get; init; } = false;
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }
}
