using Chernika.Domain;
using Chernika.Domain.Enums;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Сохранённое состояние карты для решений о доступе и согласовании.
/// <para>
/// Отдельная проекция, а не отслеживаемый экземпляр. В штатных вызовах
/// Blazor и API объект карты — тот же отслеживаемый экземпляр, который вернёт
/// сервис, и вызывающий код мог изменить его статус, филиал или автора до
/// проверки. Проверка по такому объекту проверяла бы подменённые значения.
/// </para>
/// </summary>
public sealed record PersistedCardState(
    Guid Id,
    HKCardStatus Status,
    Guid BranchId,
    Guid? AuthorId,
    bool IsAutonomous,
    string Code,
    string Version,
    uint RowVersion);

/// <summary>
/// Сохранённое состояние одного этапа согласования.
/// <para>
/// Отдельная проекция по той же причине, что и <see cref="PersistedCardState"/>:
/// отслеживаемый объект этапа вызывающий код также мог изменить заранее.
/// </para>
/// </summary>
public sealed record PersistedStageState(
    Guid Id,
    HKApprovalStage Stage,
    HKApprovalStageState State,
    string? AssignedApproverUserId,
    DateTime? ApprovedAtUtc,
    uint RowVersion);

/// <summary>
/// Как именно текущий пользователь получил доступ к карте.
/// <para>
/// Скрытого обхода здесь нет: единственное исключение — системный
/// администратор, и оно явное, попадает в аудит и отражается в этом значении.
/// </para>
/// </summary>
internal enum HkCardAccessKind
{
    /// <summary>Обычный доступ: пользователь состоит в филиале карты.</summary>
    Branch = 1,

    /// <summary>Явное административное исключение для системного администратора.</summary>
    SystemAdmin = 2
}

/// <summary>
/// Контекст доступа к конкретной карте: проверенные права, сохранённые данные
/// карты и способ доступа.
/// <para>
/// Формируется ОДИН раз на команду и переиспользуется всеми её проверками.
/// Повторное чтение того же факта внутри команды давало бы разные ответы при
/// гонке.
/// </para>
/// </summary>
internal sealed record HkCardAccessContext(
    PersistedCardState Card,
    HkCardAccessKind AccessKind,
    Guid ActorUserId);

/// <summary>
/// Исключение доступа к карте.
/// <para>
/// Отдельный тип, а не <see cref="UnauthorizedAccessException"/>: интерфейсу нужно
/// различать «нет прав на действие» и «нет доступа к этой карте», чтобы не
/// показывать действие, которое всё равно будет отклонено.
/// </para>
/// </summary>
/// <summary>
/// Конкурентное изменение: версия карты, считанная экраном, не совпадает с
/// сохранённой, либо строка этапа изменена другим решением.
/// <para>
/// Выделенный тип нужен, чтобы HTTP-слой отличал конфликт (409) от недопустимой
/// команды (400): тот же <see cref="InvalidOperationException"/> раньше означал и то,
/// и другое.
/// </para>
/// </summary>
public sealed class HkCardVersionConflictException : InvalidOperationException
{
    public HkCardVersionConflictException(string message) : base(message) { }
}

public sealed class HkCardAccessDeniedException : UnauthorizedAccessException
{
    public HkCardAccessDeniedException(string message) : base(message) { }
}