namespace Chernika.Domain.Models;

public class HKCardComponentDto
{
    public Guid Id { get; init; }
    public Guid ParentHKCardId { get; init; }
    public Guid ChildHKCardId { get; init; }
    public int SortOrder { get; init; }
    public DateTime AddedAt { get; init; }
    public string ChildCode { get; init; } = "";
    public string ChildVersion { get; init; } = "";
    public DateTime? ChildApprovedAt { get; init; }
    public string? ChildObjectName { get; init; }
}

public class AggregatedRowDto
{
    public string SourceCardCode { get; init; } = "";
    public string SourceCardVersion { get; init; } = "";
    public Guid SourceCardId { get; init; }
    public string AssemblyUnitName { get; init; } = "";
    public decimal Volume { get; init; }
    public string? UnitOfMeasure { get; init; }
    public string? GsmMaterialName { get; init; }

    /// <summary>
    /// НД марки из <c>GsmMaterial.Nd</c>. ХК не хранит собственного снимка
    /// марки, поэтому показывается актуальное значение; прежнее поле
    /// <c>Gost</c> заменено на <c>Nd</c> в PR-5.
    /// </summary>
    public string? Nd { get; init; }

    /// <summary>
    /// Марка строки мягко удалена. Строка ХК при этом сохранена и продолжает
    /// читаться: материал из строки не удаляется (см. FK RESTRICT и
    /// AttachGsmMaterialsIgnoringSoftDeleteAsync).
    /// </summary>
    public bool MaterialIsDeleted { get; init; }

    public string? Category { get; init; }
}

public record AddComponentRequest(Guid ChildCardId);
