namespace Chernika.Domain.Models;

/// <summary>
/// Кандидат в согласующие: пользователь, которому карту можно назначить.
/// <para>
/// Список формируется сервером по действующим правилам назначения: человек
/// активен, состоит в филиале карты и имеет действующее право на
/// согласование.
/// </para>
/// </summary>
public sealed record HKApproverCandidate(string Id, string FullName, string? Position);