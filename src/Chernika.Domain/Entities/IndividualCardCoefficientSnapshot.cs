namespace Chernika.Domain.Entities;

public class IndividualCardCoefficientSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    // Скалярные ссылки на источники: внешние ключи не создаются по правилам историчности снапшотов:
    // коэффициенты архивируются и восстанавливаются в C2, история не должна за ними следовать.
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
