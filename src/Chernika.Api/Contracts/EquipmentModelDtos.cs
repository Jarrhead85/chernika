using Chernika.Domain.Entities;

namespace Chernika.Api.Contracts;

public record EquipmentModelDto(
    Guid Id,
    string Index,
    string Name,
    string? Type,
    string? Brand,
    string? Modification,
    string? Description);

public record EquipmentModelDetailDto(
    Guid Id,
    string Index,
    string Name,
    string? Type,
    string? Brand,
    string? Modification,
    string? Description,
    IReadOnlyList<EquipmentInstanceDto> Instances,
    IReadOnlyList<ProductCompositionDto> ProductCompositions);

public record ProductCompositionDto(
    Guid Id,
    Guid EquipmentModelId,
    string? Comment,
    DateTime CreatedAt,
    bool IsActive,
    IReadOnlyList<ProductCompositionPartDto> Parts);

public record ProductCompositionPartDto(
    Guid Id,
    Guid ProductCompositionId,
    string Name,
    string? Description,
    int SortOrder,
    IReadOnlyList<ProductCompositionNodeDto> Nodes);

public record ProductCompositionNodeDto(
    Guid Id,
    Guid PartId,
    Guid NodeId,
    int Quantity,
    NodeRefDto Node);

public record NodeRefDto(
    Guid Id,
    string Code,
    string Name,
    string? Description);

public record CreateEquipmentModelRequest(
    string Index,
    string Name,
    string? Type,
    string? Brand,
    string? Modification,
    string? Description);

public record UpdateEquipmentModelRequest(
    string Index,
    string Name,
    string? Type,
    string? Brand,
    string? Modification,
    string? Description);

public record CreateProductCompositionRequest(
    Guid EquipmentModelId,
    string? Comment);

public static class EquipmentModelMapper
{
    public static EquipmentModelDto ToDto(EquipmentModel m) => new(
        m.Id, m.Index, m.Name, m.Type, m.Brand, m.Modification, m.Description);

    public static EquipmentModelDetailDto ToDetail(EquipmentModel m) => new(
        m.Id, m.Index, m.Name, m.Type, m.Brand, m.Modification, m.Description,
        m.Instances.Select(ToInstanceDto).ToList(),
        m.ProductCompositions.Select(ToCompDto).ToList());

    private static EquipmentInstanceDto ToInstanceDto(EquipmentInstance i) =>
        EquipmentMapper.ToDto(i);

    public static ProductCompositionDto ToCompDto(ProductComposition c) => new(
        c.Id, c.EquipmentModelId, c.Comment, c.CreatedAt, c.IsActive,
        c.Parts.OrderBy(p => p.SortOrder).Select(ToPartDto).ToList());

    public static ProductCompositionPartDto ToPartDto(ProductCompositionPart p) => new(
        p.Id, p.ProductCompositionId, p.Name, p.Description, p.SortOrder,
        p.Nodes.Select(ToNodeDto).ToList());

    public static ProductCompositionNodeDto ToNodeDto(ProductCompositionNode n) => new(
        n.Id, n.PartId, n.NodeId, n.Quantity,
        new NodeRefDto(n.Node.Id, n.Node.Code, n.Node.Name, n.Node.Description));

    public static EquipmentModel FromCreate(CreateEquipmentModelRequest r) => new()
    {
        Index = r.Index,
        Name = r.Name,
        Type = r.Type,
        Brand = r.Brand,
        Modification = r.Modification,
        Description = r.Description
    };

    public static void ApplyUpdate(EquipmentModel m, UpdateEquipmentModelRequest r)
    {
        m.Index = r.Index;
        m.Name = r.Name;
        m.Type = r.Type;
        m.Brand = r.Brand;
        m.Modification = r.Modification;
        m.Description = r.Description;
    }
}

public static class ProductCompositionMapper
{
    public static ProductCompositionDto ToDetail(ProductComposition c) =>
        EquipmentModelMapper.ToCompDto(c);

    public static ProductCompositionPartDto ToPartDto(ProductCompositionPart p) =>
        EquipmentModelMapper.ToPartDto(p);

    public static ProductCompositionNodeDto ToNodeDto(ProductCompositionNode n) =>
        EquipmentModelMapper.ToNodeDto(n);

    public static ProductComposition FromCreate(CreateProductCompositionRequest r) => new()
    {
        EquipmentModelId = r.EquipmentModelId,
        Comment = r.Comment
    };
}
