using Chernika.Domain.Entities;

namespace Chernika.Api.Contracts;

public record EquipmentInstanceDto(
    Guid Id,
    string SerialNumber,
    string Index,
    string Name,
    Guid EquipmentModelId,
    string? ModelName,
    string? Type,
    string? Brand,
    string? Modification,
    string? Description);

public record CreateEquipmentInstanceRequest(
    string SerialNumber,
    string Index,
    string Name,
    Guid EquipmentModelId,
    string? Description);

public record UpdateEquipmentInstanceRequest(
    string SerialNumber,
    string Index,
    string Name,
    Guid EquipmentModelId,
    string? Description);

public static class EquipmentMapper
{
    public static EquipmentInstanceDto ToDto(EquipmentInstance i) => new(
        i.Id, i.SerialNumber, i.Index, i.Name,
        i.EquipmentModelId, i.EquipmentModel?.Name,
        i.EquipmentModel?.Type, i.EquipmentModel?.Brand, i.EquipmentModel?.Modification,
        i.Description);

    public static EquipmentInstance FromCreate(CreateEquipmentInstanceRequest r) => new()
    {
        SerialNumber = r.SerialNumber,
        Index = r.Index,
        Name = r.Name,
        EquipmentModelId = r.EquipmentModelId,
        Description = r.Description
    };

    public static void ApplyUpdate(EquipmentInstance inst, UpdateEquipmentInstanceRequest r)
    {
        inst.SerialNumber = r.SerialNumber;
        inst.Index = r.Index;
        inst.Name = r.Name;
        inst.EquipmentModelId = r.EquipmentModelId;
        inst.Description = r.Description;
    }
}
