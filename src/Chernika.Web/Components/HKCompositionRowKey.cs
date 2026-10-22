using Chernika.Domain.Structure;
using Chernika.Infrastructure.Services;

namespace Chernika.Web.Components;

/// <summary>
/// Устойчивый ключ строки дерева принятой редакции.
/// <para>
/// Ключ не зависит ни от фильтра поиска, ни от свёрнутых веток: поиск и
/// сворачивание не должны менять идентичность строк. У виртуальной группы
/// «Без раздела» ключ свой у каждой группы — общий ключ означал бы
/// одинаковые @key в дереве.
/// </para>
/// </summary>
public static class HKCompositionRowKey
{
    /// <summary>Ключ строки по её виду и идентификатору.</summary>
    public static string Of(HKAcceptedCompositionRowDto row) => row.Kind switch
    {
        HKStructureRowKind.Position => $"node:{row.NodeId}",
        HKStructureRowKind.TerminalRow => $"item:{row.ItemId}",
        _ => row.GroupKey is not null
            ? $"group:{row.GroupKey}"
            : $"section:{row.SectionId}"
    };
}
