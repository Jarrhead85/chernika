using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

/// <summary>
/// Material alternative snapshot for a calculation item row.
/// Primary contributes to totals by GSM brand; Duplicate/Reserve/Foreign are
/// alternatives with the same volume that never increase the overall total.
/// </summary>
public class IndividualCardItemMaterialSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardItemId { get; set; }
    public IndividualCardItem IndividualCardItem { get; set; } = null!;

    // Scalar source reference, kept without FK per snapshot history rules.
    public Guid SourceGsmMaterialId { get; set; }

    public string MaterialName { get; set; } = string.Empty;
    public string MaterialType { get; set; } = string.Empty;
    public string? Gost { get; set; }
    public GsmCategory Category { get; set; }

    // Same calculated volume as the parent item; alternatives do not add to total.
    public decimal CalculatedVolume { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
