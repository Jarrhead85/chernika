using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed record IndividualCardVersionPreflightRequest(
    Guid SourceIndividualCardId,
    Guid? RootHKCardId = null);

public sealed record CreateIndividualCardVersionRequest(
    Guid SourceIndividualCardId,
    Guid? RootHKCardId = null);

public sealed record ArchiveIndividualCardRequest(Guid IndividualCardId);

/// <summary>Облегчённая шапка сформированной или архивной карты только для просмотра
/// для панели действий D5: без требования CreateVersion/Archive и без данных расчёта.</summary>
public sealed record IndividualCardActionHeaderDto(
    Guid Id,
    string Code,
    string Version,
    IndividualCardObjectLevel ObjectLevel,
    string ObjectLevelDisplay,
    string ObjectName,
    Guid BranchId,
    IndividualCardStatus Status);

/// <summary>Явное состояние записи различий: Added / Removed / Changed / Unchanged.</summary>
public sealed record IndividualCardDiffEntryDto(
    string State,
    string What,
    string Display,
    string? Before,
    string? After);

/// <summary>Информационный итог по марке ГСМ исходной сформированной карты
/// (альтернативные марки никогда не суммируются в общий итог).</summary>
public sealed record IndividualCardPrimaryTotalComparisonDto(
    string MaterialName,
    string Gost,
    string UnitOfMeasure,
    decimal TotalVolume);

public sealed record IndividualCardVersionComparisonDto(
    Guid SourceId,
    string SourceCode,
    string SourceVersion,
    string SourceObjectLevelDisplay,
    string SourceObjectName,
    Guid BranchId,
    IndividualCardPreflightRootState RootState,
    IReadOnlyList<IndividualCardHKCandidateDto> RootCandidates,
    IndividualCardHKCandidateDto? SelectedRoot,
    bool IsReadyToCreateDraft,
    IReadOnlyList<IndividualCardNormativeGapDto> NormativeGaps,
    IReadOnlyList<IndividualCardDiffEntryDto> CompositionChanges,
    IReadOnlyList<IndividualCardDiffEntryDto> HKChanges,
    IReadOnlyList<IndividualCardCoefficientSnapshotDto> PreviousCoefficients,
    bool HasCalculation,
    IReadOnlyList<IndividualCardPrimaryTotalComparisonDto> PreviousPrimaryTotals);
