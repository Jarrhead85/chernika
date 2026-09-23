namespace Chernika.Domain.Entities;

/// <summary>
/// Неизменяемый снапшот проблемы валидации, зафиксированный при успешном
/// пересчёте черновика. История переживает перезагрузки (в отличие от списков проблем в памяти);
/// Формирование блокируется, пока существует хотя бы один снапшот проблемы.
/// </summary>
public class IndividualCardCalculationProblemSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public Guid? HKCardId { get; set; }
    public Guid? HKCardItemId { get; set; }
    public Guid? NodeSnapshotId { get; set; }

    public int SortOrder { get; set; }
    public DateTime CapturedAt { get; set; }
}
