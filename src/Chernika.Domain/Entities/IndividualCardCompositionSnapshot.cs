using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class IndividualCardCompositionSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    public IndividualCardObjectLevel SourceLevel { get; set; }

    // Скалярные ссылки на источники: история должна переживать архивацию и новые ревизии источника,
    // поэтому внешние ключи на изменяемые строки составов не создаются.
    public Guid SourceCompositionId { get; set; }
    public string SourceCompositionVersion { get; set; } = string.Empty;
    public DateTime? SourceApprovedAt { get; set; }

    public Guid TargetObjectId { get; set; }
    public string TargetObjectCode { get; set; } = string.Empty;
    public string TargetObjectName { get; set; } = string.Empty;

    /// <summary>Множитель строки: ComplexCompositionItem.Quantity для комплекса, иначе 1.</summary>
    public int Quantity { get; set; } = 1;

    public DateTime CapturedAt { get; set; }

    public ICollection<IndividualCardAggregateSnapshot> Aggregates { get; set; }
        = new List<IndividualCardAggregateSnapshot>();
}
