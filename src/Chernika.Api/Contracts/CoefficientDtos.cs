using Chernika.Domain.Entities;

namespace Chernika.Api.Contracts;

public record CoefficientTypeDto(Guid Id, string Name, int SortOrder, int CoefficientCount, int ActiveCoefficientCount, bool IsDeleted);
public record CreateCoefficientTypeRequest(string Name, int? SortOrder = null);
public record UpdateCoefficientTypeRequest(Guid Id, string Name, int SortOrder);

public record CoefficientDto(Guid Id, Guid CoefficientTypeId, string? TypeName, string Name, string? ConditionDescription, decimal Value, bool IsActive, int SortOrder);
public record CreateCoefficientRequest(Guid CoefficientTypeId, string Name, string? ConditionDescription, decimal Value, bool IsActive = true, int SortOrder = 0);
public record UpdateCoefficientRequest(Guid CoefficientTypeId, string Name, string? ConditionDescription, decimal Value, bool IsActive, int SortOrder = 0);

public static class CoefficientTypeMapper
{
    public static CoefficientTypeDto ToDto(CoefficientType t, int coefficientCount = 0, int activeCoefficientCount = 0) =>
        new(t.Id, t.Name, t.SortOrder, coefficientCount, activeCoefficientCount, t.IsDeleted);

    public static CoefficientType FromCreate(CreateCoefficientTypeRequest r) => new()
    {
        Name = r.Name,
        SortOrder = r.SortOrder ?? 0
    };

    public static void ApplyUpdate(CoefficientType t, UpdateCoefficientTypeRequest r)
    {
        t.Name = r.Name;
        t.SortOrder = r.SortOrder;
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
