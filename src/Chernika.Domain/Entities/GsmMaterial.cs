namespace Chernika.Domain.Entities;

public class GsmMaterial
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool IsDraft { get; set; }

    // ── Переходные колонки удалены (PR-2 expand → PR-5 переключение → PR-6 удаление)
    //
    // Прежние `Type` (varchar(256) NOT NULL), `Gost` (varchar(256)) и
    // `Description` (text) удалены из сущности и из схемы миграцией фазы B.
    // Источник истины — `Nd` и `IntendedUse`, классификация — в
    // `GsmMaterialClassifications`. Исторические значения прежних полей не
    // хранятся в марке: где они нужны для чтения (снимки ИК), они лежат в
    // `IndividualCardItemMaterialSnapshots.Gost/MaterialType` как данные снимка.

    /// <summary>НД: нормативное обозначение, может содержать несколько документов.</summary>
    public string? Nd { get; set; }

    /// <summary>Включена в номенклатуру по ГОСТ. Требуется для проверки связи Foreign.</summary>
    public bool InGostNomenclature { get; set; }

    /// <summary>Назначение и условия применения.</summary>
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
