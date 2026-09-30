using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>
/// Запрос по направленным связям марок ГСМ (второй справочник, PR-4).
/// <para>
/// Счётчик и пагинация считают СВЯЗИ, а не марки: строка справочника — одна
/// направленная пара <c>Primary → Related</c>.
/// </para>
/// </summary>
public sealed class GsmRelationQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 15;
    public string? Search { get; init; }
    public bool? ShowDeleted { get; init; } = false;
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }

    /// <summary>Тип связи; null — все типы.</summary>
    public GsmRelationType? RelationType { get; init; }

    /// <summary>Фильтр по основной марке (Primary).</summary>
    public Guid? PrimaryGsmMaterialId { get; init; }
}

/// <summary>
/// Модель записи направленной связи для сервиса и API.
/// <para>
/// Осознанно НЕ является EF-сущностью: клиент не может подделать <c>IsDeleted</c>
/// или <c>Id</c> другой связи — оба значения определяются сервером.
/// </para>
/// <para>
/// Марки передаются ТОЛЬКО по <c>Guid</c> из справочника: произвольный текст
/// не принимается, серверная проверка существования обязательна в любом случае.
/// </para>
/// </summary>
public sealed class GsmRelationWriteRequest
{
    public Guid PrimaryGsmMaterialId { get; set; }
    public Guid RelatedGsmMaterialId { get; set; }
    public GsmRelationType RelationType { get; set; }

    /// <summary>Примечание к СВЯЗИ. Принадлежит связи, а не марке.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// Одна строка второго справочника связей. Направленность видима: основная
/// марка и связанная марка — разные поля.
/// </summary>
public sealed class GsmRelationSummary
{
    public Guid Id { get; init; }
    public Guid PrimaryGsmMaterialId { get; init; }
    public string PrimaryName { get; init; } = string.Empty;

    /// <summary>true, если основная марка soft-deleted (в истории удалённых связей).</summary>
    public bool PrimaryIsDeleted { get; init; }

    public Guid RelatedGsmMaterialId { get; init; }
    public string RelatedName { get; init; } = string.Empty;

    /// <summary>true, если связанная марка soft-deleted (в истории удалённых связей).</summary>
    public bool RelatedIsDeleted { get; init; }

    public GsmRelationType RelationType { get; init; }
    public string? Note { get; init; }
    public bool IsDeleted { get; init; }

    /// <summary>Включена ли связанная марка в номенклатуру по ГОСТ — для правила Foreign.</summary>
    public bool RelatedInGostNomenclature { get; init; }
}

/// <summary>Полная карточка связи для формы редактирования.</summary>
public sealed class GsmRelationEditView
{
    public Guid Id { get; init; }
    public Guid PrimaryGsmMaterialId { get; init; }
    public string PrimaryName { get; init; } = string.Empty;
    public Guid RelatedGsmMaterialId { get; init; }
    public string RelatedName { get; init; } = string.Empty;
    public GsmRelationType RelationType { get; init; }
    public string? Note { get; init; }
    public bool IsDeleted { get; init; }

    /// <summary>Связь нельзя редактировать, пока она удалена: сначала восстановление.</summary>
    public bool IsEditable => !IsDeleted;
}

/// <summary>
/// Марка для выбора в форме связи: имя и признаки, а не технические Guid в UI.
/// </summary>
public sealed class GsmRelationMaterialOption
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Nd { get; init; }
    public bool InGostNomenclature { get; init; }
}
