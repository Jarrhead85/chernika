using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed record RecalculateIndividualCardDraftRequest(
    Guid IndividualCardId,
    IReadOnlyList<Guid> CoefficientIds);

public sealed record FormIndividualCardRequest(Guid IndividualCardId);

public sealed record IndividualCardCalculationProblemDto(
    string Code,
    string Message,
    Guid? HKCardId,
    Guid? HKCardItemId,
    Guid? NodeSnapshotId,
    int SortOrder);

public sealed record IndividualCardCoefficientSnapshotDto(
    Guid Id,
    Guid SourceCoefficientId,
    Guid SourceCoefficientTypeId,
    string CoefficientTypeName,
    string CoefficientName,
    decimal Value,
    string? ConditionDescription,
    string? NormativeBasis,
    int SortOrder);

public sealed record IndividualCardCalculationMaterialDto(
    Guid Id,
    Guid SourceGsmMaterialId,
    string MaterialName,
    string MaterialType,
    string? Gost,
    GsmCategory Category,
    decimal CalculatedVolume,
    string UnitOfMeasure,
    int SortOrder);

public sealed record IndividualCardCalculationRowDto(
    Guid Id,
    Guid NodeSnapshotId,
    Guid HKCardId,
    string HKCardCode,
    string HKCardVersion,
    string AssemblyUnitCode,
    string AssemblyUnitName,
    int AssemblyUnitQuantity,
    int NodeQuantity,
    int AggregateQuantity,
    int ProductQuantity,
    decimal SourceVolume,
    decimal BaseVolume,
    decimal CalculatedVolume,
    string UnitOfMeasure,
    int SortOrder,
    IReadOnlyList<IndividualCardCalculationMaterialDto> Materials);

public sealed record IndividualCardPrimaryTotalDto(
    string MaterialName,
    string Gost,
    string UnitOfMeasure,
    decimal TotalVolume,
    int ItemCount);

public sealed record IndividualCardCalculationDto(
    Guid IndividualCardId,
    string Code,
    string Version,
    IndividualCardObjectLevel ObjectLevel,
    string ObjectLevelDisplay,
    IndividualCardStatus Status,
    Guid BranchId,
    IReadOnlyList<IndividualCardCoefficientSnapshotDto> Coefficients,
    decimal TotalCoefficient,
    decimal TotalNorm,
    IReadOnlyList<IndividualCardCalculationRowDto> Rows,
    IReadOnlyList<IndividualCardPrimaryTotalDto> PrimaryTotals,
    IReadOnlyList<IndividualCardCalculationProblemDto> Problems,
    bool IsReadyToForm);
