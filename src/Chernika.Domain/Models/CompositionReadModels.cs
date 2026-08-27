using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public record EquipmentModelRef(Guid Id, string Index, string Name);
public record AggregateRef(Guid Id, string Code, string Name, string? Description);
public record NodeRef(Guid Id, string Code, string Name);
public record ComplexRef(Guid Id, string Code, string Name);

public record ProductCompositionReadModel(
    Guid Id,
    Guid EquipmentModelId,
    string Version,
    ProductCompositionStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    DateTime? ApprovedAt,
    string? Comment,
    bool IsActive,
    Guid? SupersedesProductCompositionId,
    string? AuthorName,
    EquipmentModelRef? EquipmentModel,
    IReadOnlyList<ProductCompositionPartReadModel> Parts,
    IReadOnlyList<ProductCompositionAggregateReadModel> UngroupedAggregates);

public record ProductCompositionPartReadModel(
    Guid Id,
    Guid ProductCompositionId,
    string Name,
    string? Description,
    int SortOrder,
    IReadOnlyList<ProductCompositionAggregateReadModel> Aggregates);

public record ProductCompositionAggregateReadModel(
    Guid Id,
    Guid ProductCompositionId,
    Guid? PartId,
    Guid AggregateId,
    int Quantity,
    int SortOrder,
    string? Notes,
    AggregateRef? Aggregate);

public record AggregateCompositionReadModel(
    Guid Id,
    Guid AggregateId,
    string Version,
    ProductCompositionStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    DateTime? ApprovedAt,
    string? Comment,
    bool IsActive,
    Guid? SupersedesAggregateCompositionId,
    string? AuthorName,
    AggregateRef? Aggregate,
    IReadOnlyList<AggregateCompositionNodeReadModel> Nodes);

public record AggregateCompositionNodeReadModel(
    Guid Id,
    Guid AggregateCompositionId,
    Guid NodeId,
    int Quantity,
    int SortOrder,
    string? Notes,
    NodeRef? Node);

public record ComplexCompositionReadModel(
    Guid Id,
    Guid ComplexId,
    string Version,
    ProductCompositionStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    DateTime? ApprovedAt,
    string? Comment,
    bool IsActive,
    Guid? SupersedesComplexCompositionId,
    string? AuthorName,
    ComplexRef? Complex,
    IReadOnlyList<ComplexCompositionItemReadModel> Items);

public record ComplexCompositionItemReadModel(
    Guid Id,
    Guid ComplexCompositionId,
    Guid EquipmentModelId,
    int Quantity,
    int SortOrder,
    string? Notes,
    EquipmentModelRef? EquipmentModel);
