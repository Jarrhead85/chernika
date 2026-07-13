namespace Chernika.Api.Contracts;

public record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);
