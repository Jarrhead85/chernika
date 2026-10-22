namespace Chernika.Infrastructure.Services;

/// <summary>
/// Чтение принятой редакции карты для просмотра состава.
/// <para>
/// Узкий контракт: просмотр принятого состава не должен зависеть от
/// возможностей редактора и правки структуры. Служба остаётся источником
/// данных, компонент — только потребителем.
/// </para>
/// </summary>
public interface IHKAcceptedCompositionReader
{
    /// <summary>
    /// Принятая редакция карты целиком: дерево, итоги и реквизиты снимка.
    /// <para>
    /// null — принятой редакции у карты нет. Подстановка рабочего дерева была
    /// бы незаметной подменой черновика принятой картой.
    /// </para>
    /// </summary>
    Task<HKAcceptedCompositionDto?> GetAcceptedCompositionAsync(
        Guid cardId, CancellationToken ct = default);
}
