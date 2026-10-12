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
}
