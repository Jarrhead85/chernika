using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed record IndividualCardVersionPreflightRequest(
    Guid SourceIndividualCardId,
    Guid? RootHKCardId = null);

public sealed record CreateIndividualCardVersionRequest(
    Guid SourceIndividualCardId,
    Guid? RootHKCardId = null);

public sealed record ArchiveIndividualCardRequest(Guid IndividualCardId);

/// <summary>Explicit diff entry state: Added / Removed / Changed / Unchanged.</summary>
public sealed record IndividualCardDiffEntryDto(
    string State,
    string What,
    string Display,
    string? Before,
    string? After);

/// <summary>Informational per-material total of the SOURCE Formed card
/// (alternative brands are never summed into a grand total).</summary>
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
