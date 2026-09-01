namespace Chernika.Domain.Models;

public enum ReferenceStatusFilter
{
    Active = 0,
    Archived = 1,
    All = 2
}

public sealed record CoefficientTypeListQuery(
    string? SearchText = null,
    ReferenceStatusFilter StatusFilter = ReferenceStatusFilter.Active,
    string SortBy = "sort",
    bool SortDescending = false,
    int Page = 1,
    int PageSize = 50);

public sealed record CoefficientTypeListItemDto(
    Guid Id,
    string Name,
    int SortOrder,
    int CoefficientCount,
    bool IsDeleted,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt);

public sealed record CreateCoefficientTypeRequest(
    string Name,
    int? SortOrder = null);

public sealed record UpdateCoefficientTypeRequest(
    Guid Id,
    string Name,
    int SortOrder);