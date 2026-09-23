using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class IndividualCardHKSourceSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    /// <summary>Идентичность позиции в разрешённом дереве предварительной проверки; повторы
    /// одного SourceHKCardId в разных ветках считаются разными вхождениями.</summary>
    public Guid PreflightOccurrenceId { get; set; }

    public Guid? ParentHKSourceSnapshotId { get; set; }
    public IndividualCardHKSourceSnapshot? Parent { get; set; }
    public ICollection<IndividualCardHKSourceSnapshot> Children { get; set; }
        = new List<IndividualCardHKSourceSnapshot>();

    // Скалярная ссылка на источник: ХК может быть позже заархивирована или переименована;
    // поля отображения ниже — неизменяемые копии, зафиксированные в момент снапшота.
    public Guid SourceHKCardId { get; set; }

    public IndividualCardObjectLevel ObjectLevel { get; set; }
    public Guid SourceObjectId { get; set; }
    public string SourceObjectCode { get; set; } = string.Empty;
    public string SourceObjectName { get; set; } = string.Empty;

    public string HKCardCode { get; set; } = string.Empty;
    public string HKCardVersion { get; set; } = string.Empty;
    public Guid BranchId { get; set; }
    public DateTime? HKCardApprovedAt { get; set; }
    public DateTime? HKCardEffectiveDate { get; set; }
    public DateTime? HKCardExpirationDate { get; set; }

    public int SortOrder { get; set; }
    public DateTime CapturedAt { get; set; }

    /// <summary>Исторический признак полноты, зафиксированный из источника предварительной проверки.</summary>
    public bool IsComplete { get; set; }
}
