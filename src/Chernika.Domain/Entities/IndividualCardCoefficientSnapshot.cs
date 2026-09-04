namespace Chernika.Domain.Entities;

public class IndividualCardCoefficientSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    // Scalar source references, kept without FK per snapshot history rules:
    // coefficients are archived/restored in C2 and history must not follow them.
    public Guid SourceCoefficientId { get; set; }
    public Guid SourceCoefficientTypeId { get; set; }

    public string CoefficientTypeName { get; set; } = string.Empty;
    public string CoefficientName { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string? ConditionDescription { get; set; }
    public string? NormativeBasis { get; set; }
    public int SortOrder { get; set; }
    public DateTime CapturedAt { get; set; }
}
