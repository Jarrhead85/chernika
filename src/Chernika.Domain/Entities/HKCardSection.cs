using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

/// <summary>
/// Раздел конкретной карты: «Масла», «Смазки», «Топлива».
/// <para>
/// Раздел — визуальная группа строк, а не структурная позиция. У него нет
/// количества экземпляров и собственных норм: при расчёте раздел всегда
/// условно равен 1 и не умножает потребность.
/// </para>
/// <para>
/// Разделы не создаются автоматически при открытии карты: пустое
/// информационное состояние не является записью базы.
/// </para>
/// </summary>
public class HKCardSection
{
    public Guid Id { get; set; }

    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;

    /// <summary>Тип раздела. Произвольные пользовательские типы не вводятся.</summary>
    public HKCardSectionType SectionType { get; set; }

    /// <summary>
    /// Порядок раздела среди разделов карты.
    /// <para>
    /// Верхний порядок раздела и самостоятельных веток без раздела должен быть
    /// однозначным, поэтому раздел участвует в общей нумерации верхнего уровня.
    /// </para>
    /// </summary>
    public int SortOrder { get; set; }

    public ICollection<HKCardNode> Nodes { get; set; } = new List<HKCardNode>();
    public ICollection<HKCardItem> Items { get; set; } = new List<HKCardItem>();
}