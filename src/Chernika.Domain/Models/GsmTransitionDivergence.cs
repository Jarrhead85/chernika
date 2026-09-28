namespace Chernika.Domain.Models;

/// <summary>
/// Расхождение между прежним полем и его переходной копией в период PR-2…PR-5.
/// <para>
/// Источником истины в этот период остаётся старое поле: штатный UI и API
/// редактируют <c>Gost</c>/<c>Description</c>, а <c>Nd</c>/<c>IntendedUse</c>
/// не редактируются вовсе. Поэтому расхождение всегда означает «новое поле
/// отстало», а не «старое поле испорчено».
/// </para>
/// </summary>
public sealed class GsmTransitionDivergence
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }

    /// <summary>Прежнее значение ГОСТ (источник истины до переключения в PR-5).</summary>
    public string? Gost { get; init; }

    /// <summary>Текущее значение НД; <c>null</c> означает «не перенесено».</summary>
    public string? Nd { get; init; }

    public bool NdDiffers { get; init; }

    public string? Description { get; init; }

    public string? IntendedUse { get; init; }

    public bool IntendedUseDiffers { get; init; }

    /// <summary>Значения, которые запишет сверка: новое = прежнее (с обрезкой пробелов).</summary>
    public string? ExpectedNd { get; init; }

    public string? ExpectedIntendedUse { get; init; }
}

/// <summary>
/// Результат однократной сверки переходных полей.
/// <para>
/// Операция однонаправленная: она допустима только пока источником истины
/// является старое поле. После переключения UI/сервисов на <c>Nd</c>/<c>IntendedUse</c>
/// (PR-5) повторный запуск затёр бы более новые значения новых полей.
/// </para>
/// </summary>
public sealed class GsmTransitionReconciliation
{
    /// <summary>Сколько марок просмотрено, включая soft-deleted.</summary>
    public int Inspected { get; init; }

    /// <summary>Сколько значений <c>Nd</c> приведено к последнему сохранённому ГОСТ.</summary>
    public int NdFixed { get; init; }

    /// <summary>Сколько значений <c>IntendedUse</c> приведено к последнему сохранённому описанию.</summary>
    public int IntendedUseFixed { get; init; }

    /// <summary>Сколько значений очищено, потому что прежнее поле оказалось пустым.</summary>
    public int NdCleared { get; init; }

    public int IntendedUseCleared { get; init; }

    public IReadOnlyList<Guid> ChangedMaterialIds { get; init; } = Array.Empty<Guid>();

    public int TotalFixed => NdFixed + IntendedUseFixed;
}
