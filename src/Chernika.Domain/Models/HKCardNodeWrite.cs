using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>
/// Позиция собственного дерева карты во входных данных.
/// <para>
/// <see cref="Id"/> назначает <b>вызывающий код</b> и обязателен: сервер не
/// генерирует идентификаторы. Сохранение идёт синхронизацией по <c>Id</c>,
/// поэтому стабильный идентификатор позволяет обновлять существующую позицию,
/// не пересоздавая её, — вместе со связанными конечными строками.
/// </para>
/// </summary>
public record HKCardNodeWrite(
    Guid Id,
    Guid? ParentId,
    HKObjectLevel Level,
    string Name,
    int Quantity,
    int SortOrder);