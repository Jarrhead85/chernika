namespace Chernika.Domain.Models;

/// <summary>
/// Модель редактирования марки ГСМ для сервиса и API.
/// <para>
/// Осознанно НЕ является EF-сущностью: принимать <c>GsmMaterial</c> целиком
/// нельзя, потому что тогда частичный UI/API DTO приводит к обнулению полей,
/// которых в запросе не было (например, <c>IntendedUse</c> или <c>Note</c>).
/// </para>
/// <para>
/// <c>GroupName</c> и <c>SubgroupNames</c> — классификация. Пустая группа и пустой
/// список означают «без классификации» и допустимы только для уже существующей
/// legacy-марки: придумывать группу нельзя.
/// </para>
/// </summary>
public sealed class GsmMaterialWriteRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Nd { get; set; }
    public bool InGostNomenclature { get; set; }
    public string? IntendedUse { get; set; }
    public bool SuitabilityGround { get; set; }
    public bool SuitabilityAir { get; set; }
    public bool SuitabilitySea { get; set; }
    public string? NatoIndex { get; set; }
    public string? Note { get; set; }

    /// <summary>Единственная группа марки; null или пробелы — классификации нет.</summary>
    public string? GroupName { get; set; }

    /// <summary>Подгруппы этой группы. Допускается несколько; пустые и дубли отбрасываются.</summary>
    public List<string> SubgroupNames { get; set; } = new();
}

/// <summary>
/// Одна строка первого сводного справочника. Строка ровно одна на
/// <c>GsmMaterial.Id</c>: подгруппы и связи не размножают строку марки.
/// </summary>
public sealed class GsmMaterialSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public bool IsDraft { get; init; }

    /// <summary>Единственная группа марки; null у legacy-марки без классификации.</summary>
    public string? GroupName { get; init; }

    /// <summary>Подгруппы группы, по возрастанию имени.</summary>
    public IReadOnlyList<string> SubgroupNames { get; init; } = Array.Empty<string>();

    public string? Nd { get; init; }
    public bool InGostNomenclature { get; init; }
    public string? IntendedUse { get; init; }
    public bool SuitabilityGround { get; init; }
    public bool SuitabilityAir { get; init; }
    public bool SuitabilitySea { get; init; }
    public string? NatoIndex { get; init; }
    public string? Note { get; init; }

    /// <summary>Read-only списки связей. <c>DuplicateAndReserve</c> попадает и сюда, и в резервные.</summary>
    public IReadOnlyList<string> DuplicateNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ReserveNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForeignNames { get; init; } = Array.Empty<string>();

    /// <summary>Есть ли классификация вообще — для фильтра «Без классификации» и подсказок UI.</summary>
    public bool HasClassification => GroupName is not null;
}

/// <summary>Полная карточка марки для формы редактирования.</summary>
public sealed class GsmMaterialEditView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public bool IsDraft { get; init; }
    public string? Nd { get; init; }
    public bool InGostNomenclature { get; init; }
    public string? IntendedUse { get; init; }
    public bool SuitabilityGround { get; init; }
    public bool SuitabilityAir { get; init; }
    public bool SuitabilitySea { get; init; }
    public string? NatoIndex { get; init; }
    public string? Note { get; init; }
    public string? GroupName { get; init; }
    public IReadOnlyList<string> SubgroupNames { get; init; } = Array.Empty<string>();
    public bool HasClassification => GroupName is not null;
}
