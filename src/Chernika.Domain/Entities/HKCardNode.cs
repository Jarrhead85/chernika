using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

/// <summary>
/// Позиция собственного дерева карты. Принадлежит только своей карте.
/// Не является заменой доменной сущности <see cref="Node"/> — это структурная
/// позиция внутри конкретного документа.
/// </summary>
public class HKCardNode
{
    public Guid Id { get; set; }
    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;

    /// <summary>
    /// Родительская позиция внутри той же карты. null — верхняя позиция
    /// под корнем карты.
    /// </summary>
    public Guid? ParentId { get; set; }
    public HKCardNode? Parent { get; set; }

    public HKObjectLevel Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public int SortOrder { get; set; }

    /// <summary>
    /// Раздел самостоятельной ветки.
    /// <para>
    /// Заполняется ТОЛЬКО у верхней позиции (<see cref="ParentId"/> = null).
    /// У вложенной позиции значение равно null: она наследует раздел своего
    /// корня. Независимо редактируемый раздел у каждого вложенного узла создал
    /// бы противоречивую принадлежность одной ветки.
    /// </para>
    /// </summary>
    public Guid? SectionId { get; set; }
    public HKCardSection? Section { get; set; }
}
