namespace Chernika.Domain.Models;

/// <summary>
/// Состояние нормативной готовности одной строки состава.
/// </summary>
public sealed record ReadinessRow(
    Guid ChildId,
    string ChildCode,
    string ChildName,
    string Status,
    Guid? HkCardId,
    string? HkVersion)
{
    public const string Ready = "Ready";
    public const string Missing = "Missing";
    public const string Expired = "Expired";
    public const string FutureEffective = "FutureEffective";
    public const string ArchivedOrClosed = "ArchivedOrClosed";

    public bool IsProblem => Status != Ready;
}
