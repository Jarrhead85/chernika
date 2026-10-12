using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>
/// Позиция собственного дерева карты во входных данных.
/// Идентификатор задаёт вызывающий код: сохранение идёт полной заменой дерева,
/// поэтому сервер присваивает идентификаторы сам.
/// </summary>
public record HKCardNodeWrite(
    Guid Id,
    Guid? ParentId,
    HKObjectLevel Level,
    string Name,
    int Quantity,
    int SortOrder);