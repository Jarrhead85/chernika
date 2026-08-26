using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public record CompositionVersionSummary(
    Guid Id,
    Guid ObjectId,
    string Version,
    ProductCompositionStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    DateTime? ApprovedAt,
    string? Comment,
    Guid? PredecessorId,
    string? AuthorName = null,
    string? ObjectCode = null,
    string? ObjectName = null,
    bool IsActive = false,
    int? PartCount = null,
    int? AggregateCount = null,
    int? CoveredCount = null,
    int? NodeCount = null,
    int? ItemCount = null);
