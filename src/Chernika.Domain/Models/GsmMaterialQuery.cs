namespace Chernika.Domain.Models;

public sealed class GsmMaterialQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 15;
    public string? Search { get; init; }
    public bool? ShowDeleted { get; init; } = false;
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }

    // ── Фильтры первого сводного справочника (PR-3) ──────────────────────
    // Все применяются серверно к строкам марок, а не к подгруппам или связям,
    // поэтому TotalCount остаётся количеством марок.

    /// <summary>Только марки с признаком включения в номенклатуру по ГОСТ.</summary>
    public bool? InGostNomenclature { get; init; }

    /// <summary>Только марки, пригодные хотя бы для одного из видов применения.</summary>
    public bool? SuitabilityAny { get; init; }

    /// <summary>Индекс НАТО: подстрока, регистр не важен.</summary>
    public string? NatoIndex { get; init; }

    /// <summary>Группа: подстрока, сравнение как в DB expression-index.</summary>
    public string? GroupName { get; init; }

    /// <summary>Подгруппа: подстрока, сравнение как в DB expression-index.</summary>
    public string? SubgroupName { get; init; }

    /// <summary>Только марки без классификации (включая черновики предложений).</summary>
    public bool? OnlyUnclassified { get; init; }
}
