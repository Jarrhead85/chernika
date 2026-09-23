namespace Chernika.Domain.Entities;

public class IndividualCardNodeSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardAggregateSnapshotId { get; set; }
    public IndividualCardAggregateSnapshot AggregateSnapshot { get; set; } = null!;

    // Скалярная ссылка на источник: внешний ключ не создаётся по правилам историчности снапшотов.
    public Guid NodeId { get; set; }
    public string NodeCode { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
}
