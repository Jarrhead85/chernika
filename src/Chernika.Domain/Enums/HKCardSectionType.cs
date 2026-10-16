namespace Chernika.Domain.Enums;

/// <summary>
/// Раздел карты: «Масла», «Смазки», «Топлива».
/// <para>
/// Это НЕ уровень техники. Раздел не входит в <see cref="HKObjectLevel"/> и не
/// влияет на определение уровня карты: уровень выводится только по техническим
/// позициям и конечным строкам.
/// </para>
/// </summary>
public enum HKCardSectionType
{
    Oils = 1,
    Greases = 2,
    Fuels = 3
}