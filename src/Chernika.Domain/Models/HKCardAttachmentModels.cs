using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>E0-стиль DTO вложения ХК (metadata), возвращается вместо entity.</summary>
public sealed record HKCardAttachmentInfoDto(
    Guid Id,
    string OriginalFileName,
    long SizeBytes,
    string? UploadedByUserName,
    DateTime UploadedAt);

/// <summary>
/// Совокупный результат операции с PDF-основанием (загрузка, замена, удаление).
/// <para>
/// Операция с файлом — не только файл: она может сбросить согласование
/// реквизитов, поэтому вызывающая сторона обязана получить обновлённое
/// состояние карты и этапов одним результатом, чтобы экран обновился без
/// перезагрузки всей формы (несохранённый ввод других частей не теряется).
/// Внутренние ключи хранилища (StorageKey) и физические пути наружу не
/// передаются.
/// </para>
/// </summary>
public sealed record HKCardAttachmentMutationResult(
    Guid CardId,
    /// <summary>Новое вложение после загрузки/замены; после удаления — null.</summary>
    HKCardAttachmentInfoDto? Attachment,
    /// <summary>Актуальная версия строки карты после операции (xmin).</summary>
    uint CardRowVersion,
    /// <summary>Общий статус карты после пересчёта по этапам.</summary>
    HKCardStatus CardStatus,
    /// <summary>Состояние этапа реквизитов после операции (null — записи этапа ещё нет).</summary>
    HKApprovalStageState? DetailsState,
    /// <summary>Состояние этапа состава после операции (null — записи этапа ещё нет).</summary>
    HKApprovalStageState? CompositionState,
    /// <summary>Сброс согласования реквизитов из-за смены действующего основания.</summary>
    bool ApprovalInvalidated);

/// <summary>Содержимое вложения ХК для read-delivery endpoint.</summary>
public sealed record HKCardAttachmentContentDto(
    string OriginalFileName,
    string ContentType,
    Stream Content);
