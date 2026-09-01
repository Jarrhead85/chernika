namespace Chernika.Domain.Models;

public sealed record CoefficientTypeListQuery(
    string? SearchText = null,
    bool? ArchiveFilter = false,
    string SortBy = "sort",
    bool SortDescending = false,
    int Page = 1,
    int PageSize = 50);

public sealed record CoefficientTypeListItemDto(
    Guid Id,
    string Name,
    int SortOrder,
    int CoefficientCount,
    int ActiveCoefficientCount,
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
