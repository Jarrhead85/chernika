namespace Chernika.Domain.Entities;

public class IndividualCardItem
{
    public Guid Id { get; set; }
    public Guid IndividualCardId { get; set; }
    public IndividualCard IndividualCard { get; set; } = null!;

    // Легаси-поле D0 сохраняется до отдельного PR по очистке.
    public Guid? HKCardItemId { get; set; }
    public HKCardItem? HKCardItem { get; set; }

    // Скалярная ссылка на дерево снапшотов той же индивидуальной карты.
    // Намеренно без внешнего ключа: снапшоты узлов каскадно удаляются вместе
    // с картой, а ограничивающий внешний ключ мог бы нарушить порядок каскада.
    public Guid? NodeSnapshotId { get; set; }

    // Неизменяемая идентичность источника D4, заполняется из точного вхождения
    // снапшота узла и HKCardItem при пересчёте. Переименования ХК в справочнике
    // не должны менять то, что историческая строка сообщает о своём источнике.
    public Guid SourceHKSourceSnapshotId { get; set; }
    public Guid SourceHKCardId { get; set; }
    public string SourceHKCardCode { get; set; } = string.Empty;
    public string SourceHKCardVersion { get; set; } = string.Empty;
    public Guid SourceHKCardItemId { get; set; }

    public string AssemblyUnitCode { get; set; } = string.Empty;
    public string AssemblyUnitName { get; set; } = string.Empty;
    public int AssemblyUnitQuantity { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public string? Periodicity { get; set; }
    public string? Notes { get; set; }

    public decimal SourceVolume { get; set; }
    public decimal BaseVolume { get; set; }
    public decimal CalculatedVolume { get; set; }
    public int SortOrder { get; set; }

    // Легаси-поле D0 сохраняется до отдельного PR по очистке.
    public int Quantity { get; set; }

    public ICollection<IndividualCardItemMaterialSnapshot> MaterialSnapshots { get; set; }
        = new List<IndividualCardItemMaterialSnapshot>();
}
