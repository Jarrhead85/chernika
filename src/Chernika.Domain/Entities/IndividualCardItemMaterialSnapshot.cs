using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

/// <summary>
/// Снапшот альтернативной марки ГСМ для строки расчёта.
/// Основные учитываются в итогах по марке ГСМ; дублирующие, резервные и зарубежные —
/// альтернативы с тем же объёмом, не увеличивающие общий итог.
/// </summary>
public class IndividualCardItemMaterialSnapshot
{
    public Guid Id { get; set; }
    public Guid IndividualCardItemId { get; set; }
    public IndividualCardItem IndividualCardItem { get; set; } = null!;

    // Скалярная ссылка на источник: внешний ключ не создаётся по правилам историчности снапшотов.
    public Guid SourceGsmMaterialId { get; set; }

    public string MaterialName { get; set; } = string.Empty;
    public string MaterialType { get; set; } = string.Empty;
    public string? Gost { get; set; }
    public GsmCategory Category { get; set; }

    // Тот же рассчитанный объём, что у родительской строки; альтернативы не увеличивают итог.
    public decimal CalculatedVolume { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
