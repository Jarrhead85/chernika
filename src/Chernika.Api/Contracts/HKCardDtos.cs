using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;

namespace Chernika.Api.Contracts;

/// <summary>
/// Ссылка на марку ГСМ в строках ХК.
/// <para>
/// Источник истины — <c>GsmMaterial.Nd</c> и <c>GsmMaterialClassifications</c>.
/// Прежние <c>Type</c>/<c>Gost</c> больше не передаются: они удаляются в PR-6, и
/// держать их в действующем контракте значит закрепить переходные поля в API.
/// </para>
/// </summary>
public record GsmMaterialRefDto(
    Guid Id,
    string Name,
    string? Nd,
    string? GroupName,
    IReadOnlyList<string> SubgroupNames);

/// <summary>
/// Единое построение ссылки на марку для всех картных контрактов. Навигация
/// <c>GsmMaterial</c> может оказаться незагруженной: у марки есть глобальный
/// фильтр по мягкому удалению, поэтому удалённая марка в строке ХК даёт
/// <c>null</c>. Историческая строка при этом остаётся читаемой — материал не
/// удаляется из строки ХК.
/// </summary>
public static class GsmMaterialRefFactory
{
    public static GsmMaterialRefDto Create(HKCardItemMaterial m)
    {
        // Классификация нормализована: одна группа на марку (инвариант B),
        // подгрупп может быть несколько после снятия переходного запрета PR-3.
        var own = m.GsmMaterial?.Classifications?
            .Where(c => !string.IsNullOrWhiteSpace(c.GroupName))
            .ToList() ?? new List<Domain.Entities.GsmMaterialClassification>();
        var group = own
            .Select(c => c.GroupName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(g => g, StringComparer.Ordinal)
            .FirstOrDefault();
        var subgroups = own
            .Select(c => c.SubgroupName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new GsmMaterialRefDto(
            m.GsmMaterialId,
            m.GsmMaterial?.Name ?? "",
            m.GsmMaterial?.Nd,
            group,
            subgroups);
    }
}

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
    Guid? SupersedesHKCardId,
    IReadOnlyList<HKCardItemDto> Items,
    IReadOnlyList<Chernika.Domain.Models.HKCardVersionDto> Versions);

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

public record ArchiveHKCardRequest(Guid ReplacementCardId, string Reason);

public record CreateNewHKCardVersionResponse(Guid NewCardId);

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

    public static HKCardDetailDto ToDetail(HKCard c, IReadOnlyList<HKCardVersionDto>? versions = null) => new(
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
        c.SupersedesHKCardId,
        c.Items.Select(ToItemDto).ToList(),
        versions ?? Array.Empty<HKCardVersionDto>());

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
        GsmMaterialRefFactory.Create(m);

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
