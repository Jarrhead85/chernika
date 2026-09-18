using Chernika.Domain.Entities;

namespace Chernika.Api.Contracts;

public record EquipmentInstanceDto(
    Guid Id,
    string SerialNumber,
    string Index,
    string Name,
    Guid EquipmentModelId,
    string? ModelName,
    Guid? EquipmentTypeId,
    string? EquipmentTypeDisplay,
    string? Brand,
    string? Modification,
    string? Description);

public record CreateEquipmentInstanceRequest(
    string SerialNumber,
    string Index,
    string Name,
    Guid EquipmentModelId,
    Guid? EquipmentTypeId,
    string? Brand,
    string? Modification,
    string? Description);

public record UpdateEquipmentInstanceRequest(
    string SerialNumber,
    string Index,
    string Name,
    Guid EquipmentModelId,
    Guid? EquipmentTypeId,
    string? Brand,
    string? Modification,
    string? Description);

public static class EquipmentMapper
{
    public static EquipmentInstanceDto ToDto(EquipmentInstance i) => new(
        i.Id, i.SerialNumber, i.Index, i.Name,
        i.EquipmentModelId, i.EquipmentModel?.Name,
        i.EquipmentTypeId,
        i.EquipmentType is null
            ? null
            : string.IsNullOrEmpty(i.EquipmentType.TypeGroup)
                ? i.EquipmentType.Name
                : $"{i.EquipmentType.TypeGroup} / {i.EquipmentType.Name}",
        i.Brand, i.Modification,
        i.Description);

    public static EquipmentInstance FromCreate(CreateEquipmentInstanceRequest r) => new()
    {
        SerialNumber = r.SerialNumber,
        Index = r.Index,
        Name = r.Name,
        EquipmentModelId = r.EquipmentModelId,
        EquipmentTypeId = r.EquipmentTypeId,
        Brand = r.Brand,
        Modification = r.Modification,
        Description = r.Description
    };

    public static void ApplyUpdate(EquipmentInstance inst, UpdateEquipmentInstanceRequest r)
    {
        inst.SerialNumber = r.SerialNumber;
        inst.Index = r.Index;
        inst.Name = r.Name;
        inst.EquipmentModelId = r.EquipmentModelId;
        inst.EquipmentTypeId = r.EquipmentTypeId;
        inst.Brand = r.Brand;
        inst.Modification = r.Modification;
        inst.Description = r.Description;
    }
}
