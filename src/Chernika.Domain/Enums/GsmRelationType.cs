namespace Chernika.Domain.Enums;

/// <summary>
/// Тип направленной связи между марками ГСМ (Primary → Related).
/// <para>
/// <see cref="DuplicateAndReserve"/> — одна запись, отображается в двух списках
/// справочника (дублирующие и резервные).
/// <see cref="Foreign"/> — взаимоисключающее значение: связанная марка не должна
/// быть включена в номенклатуру по ГОСТ.
/// </para>
/// </summary>
public enum GsmRelationType
{
    Duplicate,
    Reserve,
    DuplicateAndReserve,
    Foreign
}
