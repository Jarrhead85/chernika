namespace Chernika.Domain.Entities;

/// <summary>
/// Immutable validation problem snapshot captured during a successful Draft
/// recalculation. History survives reloads (unlike in-memory problem lists);
/// Form is blocked while any problem snapshot exists.
/// </summary>
public class IndividualCardCalculationProblemSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public Guid? HKCardId { get; set; }
    public Guid? HKCardItemId { get; set; }
    public Guid? NodeSnapshotId { get; set; }

    public int SortOrder { get; set; }
    public DateTime CapturedAt { get; set; }
}
