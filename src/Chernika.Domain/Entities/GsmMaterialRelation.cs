using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

/// <summary>
/// Направленная связь между марками ГСМ: Primary → Related.
/// <para>
/// Справочная подсказка при работе со строкой ХК: связь не добавляет материал
/// автоматически — фактический выбор делает пользователь
/// (<see cref="HKCardItemMaterial.Category"/> остаётся независимым полем строки).
/// </para>
/// <para>
/// Удаление связи — мягкое (<see cref="IsDeleted"/>); уникальность активной
/// направленной пары обеспечивается частичным индексом.
/// </para>
/// </summary>
public class GsmMaterialRelation
{
    public Guid Id { get; set; }
    public Guid PrimaryGsmMaterialId { get; set; }
    public GsmMaterial PrimaryGsmMaterial { get; set; } = null!;
    public Guid RelatedGsmMaterialId { get; set; }
    public GsmMaterial RelatedGsmMaterial { get; set; } = null!;
    public GsmRelationType RelationType { get; set; }
    public string? Note { get; set; }
    public bool IsDeleted { get; set; }
}
