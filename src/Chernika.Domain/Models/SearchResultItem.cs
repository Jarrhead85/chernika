namespace Chernika.Domain.Models;

public class SearchResultItem
{
    public string EntityType { get; set; } = "";
    public string EntityTypeDisplay { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? ContextInfo { get; set; }
    public string Url { get; set; } = "";
    public string? MatchField { get; set; }
}
