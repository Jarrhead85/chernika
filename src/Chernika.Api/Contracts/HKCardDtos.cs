using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;

namespace Chernika.Api.Contracts;

public record GsmMaterialRefDto(
    Guid Id,
    string Name,
    string Type,
    string? Gost);

public record HKCardDetailDto(
    Guid Id,
    string Code,
    string Version,
    HKCardStatus Status,
    HKObjectLevel ObjectLevel,
    Guid BranchId,
    string? BranchName,
    Guid? ComplexId,
    Guid? EquipmentModelId,
    Guid? AggregateId,
    Guid? NodeId,
    string? ObjectName,
    string? Purpose,
    string? NormativeBasis,
    string? Notes,
    string? RequestOrganization,
    string? RequestSenderFullName,
    DateTime? RequestReceivedDate,
    string? RequestDetails,
    string? IncomingLetterNumber,
    string? OutgoingLetterNumber,
    Guid? AuthorId,
    Guid? ReviewerId,
    DateTime? ApprovedDate,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    uint RowVersion,
    IReadOnlyList<HKCardItemDto> Items);

public record HKCardItemDto(
    Guid Id,
    Guid AssemblyUnitId,
    string? AssemblyUnitName,
    int Quantity,
    decimal Volume,
    string? UnitOfMeasure,
    string? Periodicity,
    string? Notes,
    int SortOrder,
    IReadOnlyList<GsmMaterialRefDto> PrimaryMaterials,
    IReadOnlyList<GsmMaterialRefDto> DuplicateMaterials,
    IReadOnlyList<GsmMaterialRefDto> ReserveMaterials,
    IReadOnlyList<GsmMaterialRefDto> ForeignMaterials);

public record CreateHKCardRequest(
    Guid BranchId,
    HKObjectLevel ObjectLevel,
    Guid? ComplexId,
    Guid? EquipmentModelId,
    Guid? AggregateId,
    Guid? NodeId,
    string? Purpose,
    string? NormativeBasis,
    string? Notes,
    string? RequestOrganization,
    string? RequestSenderFullName,
    DateTime? RequestReceivedDate,
    string? RequestDetails,
    string? IncomingLetterNumber,
    string? OutgoingLetterNumber,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate);

public record UpdateHKCardRequest(
    HKObjectLevel ObjectLevel,
    Guid? ComplexId,
    Guid? EquipmentModelId,
    Guid? AggregateId,
    Guid? NodeId,
    string? Purpose,
    string? NormativeBasis,
    string? Notes,
    string? RequestOrganization,
    string? RequestSenderFullName,
    DateTime? RequestReceivedDate,
    string? RequestDetails,
    string? IncomingLetterNumber,
    string? OutgoingLetterNumber,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    uint RowVersion);

public record StatusChangeRequest(HKCardStatus NewStatus, string? Comment = null);

public record DeleteHKCardRequest(string Reason);

public static class HKCardMapper
{
    public static string? GetObjectName(HKCard c) => c.ObjectLevel switch
    {
        HKObjectLevel.Complex => c.Complex?.Name,
        HKObjectLevel.EquipmentModel => c.EquipmentModel?.Name,
        HKObjectLevel.Aggregate => c.Aggregate?.Name,
        HKObjectLevel.Node => c.Node?.Name,
        _ => null
    };

    public static HKCardDetailDto ToDetail(HKCard c) => new(
        c.Id, c.Code, c.Version, c.Status, c.ObjectLevel,
        c.BranchId, c.Branch?.Name,
        c.ComplexId, c.EquipmentModelId, c.AggregateId, c.NodeId,
        GetObjectName(c),
        c.Purpose, c.NormativeBasis, c.Notes,
        c.RequestOrganization, c.RequestSenderFullName,
        c.RequestReceivedDate, c.RequestDetails,
        c.IncomingLetterNumber, c.OutgoingLetterNumber,
        c.AuthorId, c.ReviewerId,
        c.ApprovedDate, c.EffectiveDate, c.ExpirationDate,
        c.CreatedAt, c.UpdatedAt, c.RowVersion,
        c.Items.Select(ToItemDto).ToList());

    private static HKCardItemDto ToItemDto(HKCardItem i) =>
        new(
            i.Id,
            i.AssemblyUnitId,
            i.AssemblyUnit?.Name,
            i.Quantity,
            i.Volume,
            i.UnitOfMeasure,
            i.Periodicity,
            i.Notes,
            i.SortOrder,
            i.Materials
                .Where(m => m.Category == GsmCategory.Primary)
                .Select(ToMaterialRef)
                .ToList(),
            i.Materials
                .Where(m => m.Category == GsmCategory.Duplicate)
                .Select(ToMaterialRef)
                .ToList(),
            i.Materials
                .Where(m => m.Category == GsmCategory.Reserve)
                .Select(ToMaterialRef)
                .ToList(),
            i.Materials
                .Where(m => m.Category == GsmCategory.Foreign)
                .Select(ToMaterialRef)
                .ToList());

    private static GsmMaterialRefDto ToMaterialRef(HKCardItemMaterial m) =>
        new(
            m.GsmMaterialId,
            m.GsmMaterial.Name,
            m.GsmMaterial.Type,
            m.GsmMaterial.Gost);

    public static HKCard FromCreate(CreateHKCardRequest r) => new()
    {
        ObjectLevel = r.ObjectLevel,
        ComplexId = r.ComplexId,
        EquipmentModelId = r.EquipmentModelId,
        AggregateId = r.AggregateId,
        NodeId = r.NodeId,
        BranchId = r.BranchId,
        Purpose = r.Purpose,
        NormativeBasis = r.NormativeBasis,
        Notes = r.Notes,
        RequestOrganization = r.RequestOrganization,
        RequestSenderFullName = r.RequestSenderFullName,
        RequestReceivedDate = r.RequestReceivedDate,
        RequestDetails = r.RequestDetails,
        IncomingLetterNumber = r.IncomingLetterNumber,
        OutgoingLetterNumber = r.OutgoingLetterNumber,
        EffectiveDate = r.EffectiveDate,
        ExpirationDate = r.ExpirationDate
    };

    public static void ApplyUpdate(HKCard card, UpdateHKCardRequest r)
    {
        card.ObjectLevel = r.ObjectLevel;
        card.ComplexId = r.ComplexId;
        card.EquipmentModelId = r.EquipmentModelId;
        card.AggregateId = r.AggregateId;
        card.NodeId = r.NodeId;
        card.Purpose = r.Purpose;
        card.NormativeBasis = r.NormativeBasis;
        card.Notes = r.Notes;
        card.RequestOrganization = r.RequestOrganization;
        card.RequestSenderFullName = r.RequestSenderFullName;
        card.RequestReceivedDate = r.RequestReceivedDate;
        card.RequestDetails = r.RequestDetails;
        card.IncomingLetterNumber = r.IncomingLetterNumber;
        card.OutgoingLetterNumber = r.OutgoingLetterNumber;
        card.EffectiveDate = r.EffectiveDate;
        card.ExpirationDate = r.ExpirationDate;
        card.RowVersion = r.RowVersion;
    }
}
