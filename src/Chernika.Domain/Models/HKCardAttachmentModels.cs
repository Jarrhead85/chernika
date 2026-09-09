namespace Chernika.Domain.Models;

/// <summary>E0-стиль DTO вложения ХК (metadata), возвращается вместо entity.</summary>
public sealed record HKCardAttachmentInfoDto(
    Guid Id,
    string OriginalFileName,
    long SizeBytes,
    string? UploadedByUserName,
    DateTime UploadedAt);

/// <summary>Содержимое вложения ХК для read-delivery endpoint.</summary>
public sealed record HKCardAttachmentContentDto(
    string OriginalFileName,
    string ContentType,
    Stream Content);
