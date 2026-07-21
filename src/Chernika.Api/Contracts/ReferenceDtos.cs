using Chernika.Domain.Entities;

namespace Chernika.Api.Contracts;

public record BranchDto(Guid Id, string Name, string? Code, string? Description);
public record CreateBranchRequest(string Name, string? Code, string? Description);
public record UpdateBranchRequest(string Name, string? Code, string? Description);

public record NodeDto(Guid Id, string Code, string Name, string? Description);
public record CreateNodeRequest(string Code, string Name, string? Description);
public record UpdateNodeRequest(string Code, string Name, string? Description);

public record AssemblyUnitDto(Guid Id, string Code, string Name, string? Description);
public record CreateAssemblyUnitRequest(string Code, string Name, string? Description);
public record UpdateAssemblyUnitRequest(string Code, string Name, string? Description);

public record GsmMaterialDto(Guid Id, string Name, string Type, string? Gost, string? Description);
public record CreateGsmMaterialRequest(string Name, string Type, string? Gost, string? Description);
public record UpdateGsmMaterialRequest(string Name, string Type, string? Gost, string? Description);

public static class BranchMapper
{
    public static BranchDto ToDto(Branch b) => new(b.Id, b.Name, b.Code, b.Description);
    public static Branch FromCreate(CreateBranchRequest r) => new() { Name = r.Name, Code = r.Code, Description = r.Description };
    public static void ApplyUpdate(Branch b, UpdateBranchRequest r) { b.Name = r.Name; b.Code = r.Code; b.Description = r.Description; }
}

public static class NodeMapper
{
    public static NodeDto ToDto(Node n) => new(n.Id, n.Code, n.Name, n.Description);
    public static Node FromCreate(CreateNodeRequest r) => new() { Code = r.Code, Name = r.Name, Description = r.Description };
    public static void ApplyUpdate(Node n, UpdateNodeRequest r) { n.Code = r.Code; n.Name = r.Name; n.Description = r.Description; }
}

public static class AssemblyUnitMapper
{
    public static AssemblyUnitDto ToDto(AssemblyUnit a) => new(a.Id, a.Code, a.Name, a.Description);
    public static AssemblyUnit FromCreate(CreateAssemblyUnitRequest r) => new() { Code = r.Code, Name = r.Name, Description = r.Description };
    public static void ApplyUpdate(AssemblyUnit a, UpdateAssemblyUnitRequest r) { a.Code = r.Code; a.Name = r.Name; a.Description = r.Description; }
}

public static class GsmMaterialMapper
{
    public static GsmMaterialDto ToDto(GsmMaterial m) => new(m.Id, m.Name, m.Type, m.Gost, m.Description);
    public static GsmMaterial FromCreate(CreateGsmMaterialRequest r) => new() { Name = r.Name, Type = r.Type, Gost = r.Gost, Description = r.Description };
    public static void ApplyUpdate(GsmMaterial m, UpdateGsmMaterialRequest r) { m.Name = r.Name; m.Type = r.Type; m.Gost = r.Gost; m.Description = r.Description; }
}

// ── Aggregate ────────────────────────────────────────────────────

public record AggregateDto(Guid Id, string Code, string Name, string? Description);
public record CreateAggregateApiRequest(string Code, string Name, string? Description);
public record UpdateAggregateApiRequest(string Code, string Name, string? Description);

public static class AggregateMapper
{
    public static AggregateDto ToDto(Aggregate a) => new(a.Id, a.Code, a.Name, a.Description);
    public static Aggregate FromCreate(CreateAggregateApiRequest r) => new() { Code = r.Code, Name = r.Name, Description = r.Description };
    public static void ApplyUpdate(Aggregate a, UpdateAggregateApiRequest r) { a.Code = r.Code; a.Name = r.Name; a.Description = r.Description; }
}

// ── Complex ──────────────────────────────────────────────────────

public record ComplexDto(Guid Id, string Code, string Name, string? Description);
public record CreateComplexApiRequest(string Code, string Name, string? Description);
public record UpdateComplexApiRequest(string Code, string Name, string? Description);

public static class ComplexMapper
{
    public static ComplexDto ToDto(Complex c) => new(c.Id, c.Code, c.Name, c.Description);
    public static Complex FromCreate(CreateComplexApiRequest r) => new() { Code = r.Code, Name = r.Name, Description = r.Description };
    public static void ApplyUpdate(Complex c, UpdateComplexApiRequest r) { c.Code = r.Code; c.Name = r.Name; c.Description = r.Description; }
}
