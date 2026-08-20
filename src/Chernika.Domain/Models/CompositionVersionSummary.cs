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
    Guid? PredecessorId);