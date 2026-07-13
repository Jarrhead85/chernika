using Chernika.Domain.Entities;
using Chernika.Domain.Enums;

namespace Chernika.Api.Contracts;

public record CoefficientTypeDto(Guid Id, string Name, string Group, string? Description, int SortOrder);
public record CreateCoefficientTypeRequest(string Name, CoefficientGroup Group, string? Description, int SortOrder = 0);
public record UpdateCoefficientTypeRequest(string Name, CoefficientGroup Group, string? Description, int SortOrder = 0);

public record CoefficientDto(Guid Id, Guid CoefficientTypeId, string? TypeName, string Name, string? ConditionDescription, decimal Value, bool IsActive, int SortOrder);
public record CreateCoefficientRequest(Guid CoefficientTypeId, string Name, string? ConditionDescription, decimal Value, bool IsActive = true, int SortOrder = 0);
public record UpdateCoefficientRequest(Guid CoefficientTypeId, string Name, string? ConditionDescription, decimal Value, bool IsActive, int SortOrder = 0);

public static class CoefficientTypeMapper
{
    public static CoefficientTypeDto ToDto(CoefficientType t) =>
        new(t.Id, t.Name, t.Group.ToString(), t.Description, t.SortOrder);

    public static CoefficientType FromCreate(CreateCoefficientTypeRequest r) => new()
    {
        Name = r.Name, Group = r.Group, Description = r.Description, SortOrder = r.SortOrder
    };

    public static void ApplyUpdate(CoefficientType t, UpdateCoefficientTypeRequest r)
    {
        t.Name = r.Name; t.Group = r.Group; t.Description = r.Description; t.SortOrder = r.SortOrder;
    }
}

public static class CoefficientMapper
{
    public static CoefficientDto ToDto(Coefficient c) =>
        new(c.Id, c.CoefficientTypeId, c.CoefficientType?.Name, c.Name, c.ConditionDescription, c.Value, c.IsActive, c.SortOrder);

    public static Coefficient FromCreate(CreateCoefficientRequest r) => new()
    {
        CoefficientTypeId = r.CoefficientTypeId, Name = r.Name, ConditionDescription = r.ConditionDescription,
        Value = r.Value, IsActive = r.IsActive, SortOrder = r.SortOrder
    };

    public static void ApplyUpdate(Coefficient c, UpdateCoefficientRequest r)
    {
        c.CoefficientTypeId = r.CoefficientTypeId; c.Name = r.Name; c.ConditionDescription = r.ConditionDescription;
        c.Value = r.Value; c.IsActive = r.IsActive; c.SortOrder = r.SortOrder;
    }
}
