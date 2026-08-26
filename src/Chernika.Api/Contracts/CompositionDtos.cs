using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;

namespace Chernika.Api.Contracts;

// ── Aggregate Composition ────────────────────────────────────────

public record AggregateCompositionDto(
    Guid Id,
    Guid AggregateId,
    string Version,
    ProductCompositionStatus Status,
    DateTime CreatedAt,
    bool IsActive,
    string? Comment,
    IReadOnlyList<AggregateCompositionNodeDto> Nodes);

public record AggregateCompositionNodeDto(
    Guid Id,
    Guid AggregateCompositionId,
    Guid NodeId,
    string NodeCode,
    string NodeName,
    int Quantity,
    int SortOrder,
    string? Notes);

public static class AggregateCompositionMapper
{
    public static AggregateCompositionDto ToDto(AggregateComposition c) => new(
        c.Id, c.AggregateId, c.Version, c.Status, c.CreatedAt, c.IsActive, c.Comment,
        c.Nodes.OrderBy(n => n.SortOrder).Select(ToNodeDto).ToList());

    public static AggregateCompositionDto ToDto(AggregateCompositionReadModel c) => new(
        c.Id, c.AggregateId, c.Version, c.Status, c.CreatedAt, c.IsActive, c.Comment,
        c.Nodes.OrderBy(n => n.SortOrder).Select(ToNodeDto).ToList());

    public static AggregateCompositionNodeDto ToNodeDto(AggregateCompositionNode n) => new(
        n.Id, n.AggregateCompositionId, n.NodeId,
        n.Node.Code, n.Node.Name,
        n.Quantity, n.SortOrder, n.Notes);

    public static AggregateCompositionNodeDto ToNodeDto(AggregateCompositionNodeReadModel n) => new(
        n.Id, n.AggregateCompositionId, n.NodeId,
        n.Node?.Code ?? "", n.Node?.Name ?? "",
        n.Quantity, n.SortOrder, n.Notes);
}

// ── Complex Composition ──────────────────────────────────────────

public record ComplexCompositionDto(
    Guid Id,
    Guid ComplexId,
    string Version,
    ProductCompositionStatus Status,
    DateTime CreatedAt,
    bool IsActive,
    string? Comment,
    IReadOnlyList<ComplexCompositionItemDto> Items);

public record ComplexCompositionItemDto(
    Guid Id,
    Guid ComplexCompositionId,
    Guid EquipmentModelId,
    string ModelIndex,
    string ModelName,
    int Quantity,
    int SortOrder,
    string? Notes);

public static class ComplexCompositionMapper
{
    public static ComplexCompositionDto ToDto(ComplexComposition c) => new(
        c.Id, c.ComplexId, c.Version, c.Status, c.CreatedAt, c.IsActive, c.Comment,
        c.Items.OrderBy(i => i.SortOrder).Select(ToItemDto).ToList());

    public static ComplexCompositionDto ToDto(ComplexCompositionReadModel c) => new(
        c.Id, c.ComplexId, c.Version, c.Status, c.CreatedAt, c.IsActive, c.Comment,
        c.Items.OrderBy(i => i.SortOrder).Select(ToItemDto).ToList());

    public static ComplexCompositionItemDto ToItemDto(ComplexCompositionItem i) => new(
        i.Id, i.ComplexCompositionId, i.EquipmentModelId,
        i.EquipmentModel.Index, i.EquipmentModel.Name,
        i.Quantity, i.SortOrder, i.Notes);

    public static ComplexCompositionItemDto ToItemDto(ComplexCompositionItemReadModel i) => new(
        i.Id, i.ComplexCompositionId, i.EquipmentModelId,
        i.EquipmentModel?.Index ?? "", i.EquipmentModel?.Name ?? "",
        i.Quantity, i.SortOrder, i.Notes);
}
