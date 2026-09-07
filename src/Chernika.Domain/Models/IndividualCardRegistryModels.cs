using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public sealed record IndividualCardRegistryQuery(
    string? SearchText = null,
    IndividualCardObjectLevel? ObjectLevel = null,
    IndividualCardStatus? Status = null,
    Guid? BranchId = null,
    DateTime? CreatedFrom = null,
    DateTime? CreatedTo = null,
    bool OnlyMine = false,
    bool OnlyWithNormativeGaps = false,
    string SortBy = "CreatedAt",
    bool SortDescending = true,
    int Page = 1,
    int PageSize = 50);

public sealed record IndividualCardRegistryItemDto(
    Guid Id,
    string Code,
    string Version,
    IndividualCardObjectLevel ObjectLevel,
    string ObjectLevelDisplay,
    string ObjectCode,
    string ObjectName,
    string? ContextText,
    IndividualCardStatus Status,
    Guid BranchId,
    string BranchName,
    DateTime CreatedAt,
    DateTime? FormedAt,
    string CreatedByUserId,
    string? AuthorName,
    bool HasNormativeGaps,
    int HKSourceCount,
    int CalculationProblemCount,
    Guid? SupersedesIndividualCardId,
    bool HasSuccessor);

public sealed record IndividualCardVersionChainItemDto(
    Guid Id,
    string Code,
    string Version,
    int RevisionNumber,
    IndividualCardStatus Status,
    DateTime CreatedAt,
    DateTime? FormedAt,
    DateTime? ArchivedAt,
    string CreatedByUserId,
    Guid? SupersedesIndividualCardId);

public sealed record IndividualCardAuditItemDto(
    Guid Id,
    DateTime CreatedAt,
    string Action,
    string? UserDisplayName,
    string? Details);

public sealed record IndividualCardDetailDto(
    Guid Id,
    string Code,
    string Version,
    int RevisionNumber,
    IndividualCardStatus Status,
    IndividualCardObjectLevel ObjectLevel,
    string ObjectLevelDisplay,
    string ObjectCode,
    string ObjectName,
    string? ContextText,
    Guid BranchId,
    string? BranchName,
    string CreatedByUserId,
    string? AuthorName,
    DateTime CreatedAt,
    DateTime? FormedAt,
    DateTime? ArchivedAt,
    IReadOnlyList<IndividualCardVersionChainItemDto> History,
    IReadOnlyList<IndividualCardCompositionSnapshotDto> Compositions,
    IReadOnlyList<IndividualCardHKSourceSnapshotDto> HKSources,
    IReadOnlyList<IndividualCardNormativeGapSnapshotDto> NormativeGaps,
    IReadOnlyList<IndividualCardCoefficientSnapshotDto> Coefficients,
    decimal TotalCoefficient,
    decimal TotalNorm,
    IReadOnlyList<IndividualCardCalculationRowDto> Rows,
    IReadOnlyList<IndividualCardPrimaryTotalDto> PrimaryTotals,
    IReadOnlyList<IndividualCardCalculationProblemDto> Problems,
    IReadOnlyList<IndividualCardAuditItemDto> Audit);
