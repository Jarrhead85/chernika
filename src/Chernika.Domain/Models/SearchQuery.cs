using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>
/// Расширенный глобальный поиск: сводный запрос с фильтрами, сортировкой
/// и постраничным выводом (server-side). Используется страницей /поиск и API.
/// </summary>
public sealed class SearchQuery
{
    public string Text { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }
    public HKCardStatus? HKCardStatus { get; set; }
    public Guid? BranchId { get; set; }
    public string SortBy { get; set; } = "CreatedAt";
    public bool SortDescending { get; set; } = true;
    public int MaxResults { get; set; } = 50;

    public HKObjectLevel? HKObjectLevel { get; set; }
    public HKValidityFilter? HKValidity { get; set; }
    public bool? HasAttachment { get; set; }

    public IndividualCardStatus? IndividualCardStatus { get; set; }
    public IndividualCardObjectLevel? IndividualCardObjectLevel { get; set; }
    public bool? IsFormed { get; set; }
    public bool? HasCoefficients { get; set; }

    public RelatedResultsScope? RelatedScope { get; set; }

    public WorkTaskStatus? TaskStatus { get; set; }
    public WorkTaskPriority? TaskPriority { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}
