using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

// ── Product Composition commands (existing) ──────────────────────

public record CreateCompositionRequest(Guid EquipmentModelId, string? Comment);

public record UpdateCompositionDraftRequest(Guid Id, string? Comment, DateTime? EffectiveDate, DateTime? ExpirationDate);

public record AddPartRequest(Guid CompositionId, string Name, string? Description, int SortOrder);

public record UpdatePartRequest(Guid PartId, string Name, string? Description, int SortOrder);

public record AddProductCompositionAggregateRequest(Guid PartId, Guid AggregateId, int Quantity);

public record UpdateProductCompositionAggregateRequest(Guid Id, int Quantity, int SortOrder, string? Notes);

public record ChangeCompositionStatusRequest(Guid CompositionId, ProductCompositionStatus NewStatus, string? Comment);

// ── Aggregate commands ──────────────────────────────────────────

public record CreateAggregateRequest(string Code, string Name, string? Description);

public record UpdateAggregateRequest(Guid Id, string Code, string Name, string? Description);

public record CreateAggregateCompositionRequest(Guid AggregateId, string? Comment);

public record UpdateAggregateCompositionDraftRequest(Guid Id, string? Comment, DateTime? EffectiveDate, DateTime? ExpirationDate);

public record AddAggregateCompositionNodeRequest(Guid AggregateCompositionId, Guid NodeId, int Quantity, string? Notes);

public record UpdateAggregateCompositionNodeRequest(Guid Id, int Quantity, int SortOrder, string? Notes);

public record ChangeAggregateCompositionStatusRequest(Guid CompositionId, ProductCompositionStatus NewStatus, string? Comment);

// ── Complex commands ───────────────────────────────────────────

public record CreateComplexRequest(string Code, string Name, string? Description);

public record UpdateComplexRequest(Guid Id, string Code, string Name, string? Description);

// ── Complex Composition commands ────────────────────────────────

public record CreateComplexCompositionRequest(Guid ComplexId, string? Comment);

public record UpdateComplexCompositionDraftRequest(Guid Id, string? Comment, DateTime? EffectiveDate, DateTime? ExpirationDate);

public record AddComplexCompositionItemRequest(Guid CompositionId, Guid EquipmentModelId, int Quantity);

public record UpdateComplexCompositionItemRequest(Guid Id, int Quantity, int SortOrder, string? Notes);

public record ChangeComplexCompositionStatusRequest(Guid CompositionId, ProductCompositionStatus NewStatus, string? Comment);
