using Chernika.Domain.Entities;
using Chernika.Domain.Enums;

namespace Chernika.Api.Contracts;

public record HKCardListItemDto(
    Guid Id,
    string Code,
    string Version,
    HKCardStatus Status,
    string? BranchName,
    string? NodeName,
    DateTime CreatedAt,
    DateTime? ApprovedDate);

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
    Guid BranchId,
    string? BranchName,
    Guid NodeId,
    string? NodeName,
    string? Purpose,
    string? NormativeBasis,
    string? Notes,
    Guid? AuthorId,
    Guid? ReviewerId,
    DateTime? ApprovedDate,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    DateTime CreatedAt,
    DateTime UpdatedAt,
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
    Guid NodeId,
    string? Purpose,
    string? NormativeBasis,
    string? Notes,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate);

public record UpdateHKCardRequest(
    Guid NodeId,
    string? Purpose,
    string? NormativeBasis,
    string? Notes,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate);

public record StatusChangeRequest(HKCardStatus NewStatus, string? Comment = null);

public static class HKCardMapper
{
    public static HKCardListItemDto ToListItem(HKCard c) => new(
        c.Id, c.Code, c.Version, c.Status,
        c.Branch?.Name, c.Node?.Name,
        c.CreatedAt, c.ApprovedDate);

    public static HKCardDetailDto ToDetail(HKCard c) => new(
        c.Id, c.Code, c.Version, c.Status,
        c.BranchId, c.Branch?.Name,
        c.NodeId, c.Node?.Name,
        c.Purpose, c.NormativeBasis, c.Notes,
        c.AuthorId, c.ReviewerId,
        c.ApprovedDate, c.EffectiveDate, c.ExpirationDate,
        c.CreatedAt, c.UpdatedAt,
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
        BranchId = r.BranchId,
        NodeId = r.NodeId,
        Purpose = r.Purpose,
        NormativeBasis = r.NormativeBasis,
        Notes = r.Notes,
        EffectiveDate = r.EffectiveDate,
        ExpirationDate = r.ExpirationDate
    };

    public static void ApplyUpdate(HKCard card, UpdateHKCardRequest r)
    {
        card.NodeId = r.NodeId;
        card.Purpose = r.Purpose;
        card.NormativeBasis = r.NormativeBasis;
        card.Notes = r.Notes;
        card.EffectiveDate = r.EffectiveDate;
        card.ExpirationDate = r.ExpirationDate;
    }
}
