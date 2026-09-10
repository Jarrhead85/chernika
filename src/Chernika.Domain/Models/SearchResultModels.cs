namespace Chernika.Domain.Models;

/// <summary>
/// Результат расширенного поиска: безопасная DTO-проекция без навигационных
/// графов и технических идентификаторов в текстах.
/// </summary>
public sealed record SearchResultDto(
    Guid EntityId,
    string EntityType,
    string EntityTypeDisplay,
    string Title,
    string? Subtitle,
    string? Code,
    string? Version,
    string? Status,
    string? StatusDisplay,
    Guid? BranchId,
    string? BranchName,
    DateTime? CreatedAt,
    DateTime? ApprovedDate,
    string? MatchContext,
    string NavigationUrl,
    bool CanOpen);

/// <summary>Страница результатов расширенного поиска.</summary>
public sealed record SearchPageDto(
    IReadOnlyList<SearchResultDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);
