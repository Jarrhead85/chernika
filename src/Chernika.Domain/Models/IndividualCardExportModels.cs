using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>
/// E0: единая immutable read-model экспорта ИК (PDF-бланк E1 и XLSX E2).
/// Строится только из IndividualCard и snapshot-данных; live-чтения —
/// только display-метаданные (филиал, автор).
/// </summary>
public sealed record IndividualCardExportDto(
    Guid IndividualCardId,
    string Code,
    string Version,
    int RevisionNumber,
    IndividualCardStatus Status,
    string StatusDisplay,
    IndividualCardObjectLevel ObjectLevel,
    string ObjectLevelDisplay,
    string TargetObjectCode,
    string TargetObjectName,
    string? TargetContext,
    Guid BranchId,
    string BranchName,
    string CreatedByUserId,
    string? CreatedByName,
    DateTime CreatedAt,
    DateTime? FormedAt,
    DateTime? ArchivedAt,
    IReadOnlyList<IndividualCardExportWarningDto> Warnings,
    IReadOnlyList<IndividualCardExportCompositionDto> Compositions,
    IReadOnlyList<IndividualCardExportHKSourceDto> HKSources,
    IReadOnlyList<IndividualCardExportCoefficientDto> Coefficients,
    decimal TotalCoefficient,
    IReadOnlyList<IndividualCardExportRowDto> Rows,
    IReadOnlyList<IndividualCardExportPrimaryMaterialDto> PrimaryMaterials,
    IReadOnlyList<IndividualCardVersionChainItemDto> History);

/// <summary>Warning block экспорта: черновичный дисклеймер, нормативные
/// пробелы и проблемы расчёта, зафиксированные в снимках.</summary>
public sealed record IndividualCardExportWarningDto(
    string Code,
    string Message,
    int SortOrder);

/// <summary>Раздел «Версия конструктивного состава».</summary>
public sealed record IndividualCardExportCompositionDto(
    Guid SnapshotId,
    IndividualCardObjectLevel SourceLevel,
    Guid SourceCompositionId,
    string SourceCompositionVersion,
    DateTime? SourceApprovedAt,
    Guid TargetObjectId,
    string TargetObjectCode,
    string TargetObjectName,
    int TargetQuantity,
    DateTime CapturedAt,
    IReadOnlyList<IndividualCardExportAggregateDto> Aggregates);

public sealed record IndividualCardExportAggregateDto(
    Guid SnapshotId,
    Guid AggregateId,
    string Code,
    string Name,
    int Quantity,
    int SortOrder,
    IReadOnlyList<IndividualCardExportNodeDto> Nodes);

public sealed record IndividualCardExportNodeDto(
    Guid SnapshotId,
    Guid NodeId,
    string Code,
    string Name,
    int Quantity,
    int SortOrder);

/// <summary>Раздел «Нормативные источники ХК»: дерево occurrence-использований
/// без дедупликации по SourceHKCardId.</summary>
public sealed record IndividualCardExportHKSourceDto(
    Guid SnapshotId,
    Guid PreflightOccurrenceId,
    Guid? ParentHKSourceSnapshotId,
    Guid SourceHKCardId,
    IndividualCardObjectLevel ObjectLevel,
    string ObjectLevelDisplay,
    string SourceObjectCode,
    string SourceObjectName,
    string HKCardCode,
    string HKCardVersion,
    DateTime? ApprovedAt,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    bool IsComplete,
    int SortOrder,
    DateTime CapturedAt);

public sealed record IndividualCardExportCoefficientDto(
    string TypeName,
    string Name,
    decimal Value,
    string? ConditionDescription,
    string? NormativeBasis,
    int SortOrder);

/// <summary>Расчётная строка: immutable D4-данные позиции; материалы
/// разбиты по исходным категориям без перекатегоризации.</summary>
public sealed record IndividualCardExportRowDto(
    int SortOrder,
    Guid SourceHKSourceSnapshotId,
    Guid SourceHKCardId,
    string SourceHKCardCode,
    string SourceHKCardVersion,
    string AssemblyUnitCode,
    string AssemblyUnitName,
    int AssemblyUnitQuantity,
    decimal SourceVolume,
    decimal BaseVolume,
    decimal CalculatedVolume,
    string UnitOfMeasure,
    string? Periodicity,
    string? Notes,
    int NodeQuantity,
    int AggregateQuantity,
    int ProductQuantity,
    IReadOnlyList<IndividualCardExportMaterialDto> PrimaryMaterials,
    IReadOnlyList<IndividualCardExportMaterialDto> DuplicateMaterials,
    IReadOnlyList<IndividualCardExportMaterialDto> ReserveMaterials,
    IReadOnlyList<IndividualCardExportMaterialDto> ForeignMaterials);

public sealed record IndividualCardExportMaterialDto(
    string MaterialName,
    string MaterialType,
    string? Gost,
    decimal CalculatedVolume,
    string UnitOfMeasure,
    int SortOrder);

/// <summary>Раздел «Основные марки ГСМ»: информационное значение по марке,
/// без общего итога между альтернативными марками.</summary>
public sealed record IndividualCardExportPrimaryMaterialDto(
    string MaterialName,
    string? Gost,
    string UnitOfMeasure,
    decimal Value,
    int RowCount);
