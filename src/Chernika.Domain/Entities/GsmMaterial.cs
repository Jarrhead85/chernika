namespace Chernika.Domain.Entities;

public class GsmMaterial
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Gost { get; set; }
    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool IsDraft { get; set; }

    // ── Расширение схемы (PR-2: expand) ───────────────────────────────────
    // Старые Type/Gost/Description остаются переходными полями: их продолжает
    // читать и писать действующий код до переключения в PR-5 и удаления в PR-6.

    /// <summary>НД: текстовое представление прежнего <see cref="Gost"/>, может содержать несколько документов.</summary>
    public string? Nd { get; set; }

    /// <summary>Включена в номенклатуру по ГОСТ. Требуется для проверки связи Foreign.</summary>
    public bool InGostNomenclature { get; set; }

    /// <summary>Назначение и условия применения (переходная копия прежнего <see cref="Description"/>).</summary>
    public string? IntendedUse { get; set; }

    public bool SuitabilityGround { get; set; }
    public bool SuitabilityAir { get; set; }
    public bool SuitabilitySea { get; set; }

    public string? NatoIndex { get; set; }

    /// <summary>Общее примечание к марке (примечание к связи хранится в <see cref="GsmMaterialRelation.Note"/>).</summary>
    public string? Note { get; set; }

    public ICollection<HKCardItemMaterial> HKCardItemMaterials { get; set; } = new List<HKCardItemMaterial>();
    public ICollection<GsmMaterialClassification> Classifications { get; set; } = new List<GsmMaterialClassification>();
}
