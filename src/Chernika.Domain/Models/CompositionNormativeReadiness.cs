using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

/// <summary>Состояние нормативной готовности объекта конструктивного состава.</summary>
public enum CompositionReadinessStatus
{
    Ready = 1,
    MissingHK = 2,
    MissingComposition = 3,
    NoActiveComposition = 4,
}

/// <summary>
/// Готовность объекта состава: наличие действующей утверждённой ХК и
/// актуального утверждённого состава. Навигационные цели строит сервис.
/// </summary>
public sealed record CompositionNormativeReadinessDto(
    Guid ObjectId,
    HKObjectLevel ObjectLevel,
    string ObjectCode,
    string ObjectName,
    CompositionReadinessStatus Status,
    string StatusDisplay,
    string Message,
    Guid? CurrentApprovedHKCardId,
    string? CurrentApprovedHKCardCode,
    string? CurrentApprovedHKCardVersion,
    string? HKActionLabel,
    string? HKNavigationTarget,
    Guid? CurrentCompositionId,
    string? CompositionVersion,
    string? CompositionActionLabel,
    string? CompositionNavigationTarget);
