using Chernika.Domain.Enums;
using Chernika.Domain.Models;

namespace Chernika.Domain.Entities;

/// <summary>
/// Immutable copy of a normative gap as it was at Draft creation/refresh time.
/// Preserves the historical explanation of why a Draft was partial.
/// </summary>
public class IndividualCardNormativeGapSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    public IndividualCardNormativeGapKind Kind { get; set; }
    public IndividualCardObjectLevel RelatedLevel { get; set; }
    public Guid? RelatedObjectId { get; set; }
    public string RelatedObjectType { get; set; } = string.Empty;
    public string? RelatedObjectCode { get; set; }
    public string RelatedObjectName { get; set; } = string.Empty;
    public Guid? RelatedHKCardId { get; set; }
    public string Message { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CapturedAt { get; set; }
}
