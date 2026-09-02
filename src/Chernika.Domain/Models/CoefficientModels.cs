namespace Chernika.Domain.Models;

public sealed class CoefficientListQuery
{
    public string? SearchText { get; set; }
    public Guid? CoefficientTypeId { get; set; }
    public ReferenceStatusFilter StatusFilter { get; set; } = ReferenceStatusFilter.Active;
    public bool? HasNormativeBasis { get; set; }
    public string SortBy { get; set; } = "type";
    public bool SortDescending { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class CoefficientListItemDto
{
    public Guid Id { get; set; }
    public Guid CoefficientTypeId { get; set; }
    public string CoefficientTypeName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string? ConditionDescription { get; set; }
    public string? NormativeBasis { get; set; }
    public int SortOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public sealed class CreateCoefficientRequest
{
    public Guid CoefficientTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string? ConditionDescription { get; set; }
    public string? NormativeBasis { get; set; }
    public int? SortOrder { get; set; }
}

public sealed class UpdateCoefficientRequest
{
    public Guid Id { get; set; }
    public Guid CoefficientTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string? ConditionDescription { get; set; }
    public string? NormativeBasis { get; set; }
    public int SortOrder { get; set; }
}
