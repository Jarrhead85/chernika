using Chernika.Domain.Entities;
using Chernika.Domain.Enums;

namespace Chernika.Api.Contracts;

public record IndividualCardListItemDto(
    Guid Id,
    Guid? EquipmentInstanceId,
    string? InstanceName,
    string? NodeName,
    string? HKCardCode,
    string Version,
    decimal TotalNorm,
    DateTime CreatedAt);

public record IndividualCardDetailDto(
    Guid Id,
    Guid? EquipmentInstanceId,
    Guid? HKCardId,
    Guid? NodeId,
    string Version,
    decimal TotalNorm,
    string? Notes,
    DateTime CreatedAt,
    IReadOnlyList<IndividualCardItemDto> Items,
    IReadOnlyList<CoefficientRefDto> AppliedCoefficients);

public record IndividualCardItemDto(
    Guid Id,
    Guid? HKCardItemId,
    Guid AssemblyUnitId,
    string? AssemblyUnitName,
    decimal BaseVolume,
    decimal CalculatedVolume,
    int Quantity,
    string? UnitOfMeasure,
    string? Periodicity,
    string? Notes,
    int SortOrder,
    IReadOnlyList<GsmMaterialRefDto> PrimaryMaterials,
    IReadOnlyList<GsmMaterialRefDto> DuplicateMaterials,
    IReadOnlyList<GsmMaterialRefDto> ReserveMaterials,
    IReadOnlyList<GsmMaterialRefDto> ForeignMaterials);

public record CoefficientRefDto(Guid Id, string Name, decimal Value);

public record GenerateIndividualCardsRequest(List<Guid>? CoefficientIds = null);

public record UpdateCardNotesRequest(string? Notes);

public static class MaterialCategorizer
{
    public static IReadOnlyList<GsmMaterialRefDto> PrimaryMaterials(HKCardItem hk) => Categorize(hk, GsmCategory.Primary);
    public static IReadOnlyList<GsmMaterialRefDto> DuplicateMaterials(HKCardItem hk) => Categorize(hk, GsmCategory.Duplicate);
    public static IReadOnlyList<GsmMaterialRefDto> ReserveMaterials(HKCardItem hk) => Categorize(hk, GsmCategory.Reserve);
    public static IReadOnlyList<GsmMaterialRefDto> ForeignMaterials(HKCardItem hk) => Categorize(hk, GsmCategory.Foreign);

    private static IReadOnlyList<GsmMaterialRefDto> Categorize(HKCardItem hk, GsmCategory category) =>
        hk.Materials
            .Where(m => m.Category == category)
            .Select(ToMaterialRef)
            .ToList();

    private static GsmMaterialRefDto ToMaterialRef(HKCardItemMaterial m) =>
        new(m.GsmMaterialId, m.GsmMaterial.Name, m.GsmMaterial.Type, m.GsmMaterial.Gost);
}

public static class IndividualCardMapper
{
    public static IndividualCardListItemDto ToListItem(IndividualCard c) => new(
        c.Id, c.EquipmentInstanceId,
        c.EquipmentInstance?.Name,
        c.Node?.Name,
        c.HKCard?.Code,
        c.Version, c.TotalNorm, c.CreatedAt);

    public static IndividualCardDetailDto ToDetail(IndividualCard c) => new(
        c.Id, c.EquipmentInstanceId, c.HKCardId, c.NodeId,
        c.Version, c.TotalNorm, c.Notes, c.CreatedAt,
        c.Items
            .Select(ToItemDto)
            .OrderBy(i => i.SortOrder)
            .ToList(),
        c.AppliedCoefficients.Select(co => new CoefficientRefDto(co.Id, co.Name, co.Value)).ToList());

    private static IndividualCardItemDto ToItemDto(IndividualCardItem i)
    {
        var hk = i.HKCardItem;

        return new IndividualCardItemDto(
            i.Id,
            i.HKCardItemId,
            hk?.AssemblyUnitId ?? Guid.Empty,
            hk?.AssemblyUnit?.Name,
            i.BaseVolume,
            i.CalculatedVolume,
            i.Quantity,
            hk?.UnitOfMeasure,
            hk?.Periodicity,
            hk?.Notes,
            hk?.SortOrder ?? 0,
            hk is null ? [] : MaterialCategorizer.PrimaryMaterials(hk),
            hk is null ? [] : MaterialCategorizer.DuplicateMaterials(hk),
            hk is null ? [] : MaterialCategorizer.ReserveMaterials(hk),
            hk is null ? [] : MaterialCategorizer.ForeignMaterials(hk));
    }
}
