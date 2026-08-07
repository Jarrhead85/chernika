namespace Chernika.Domain;

/// <summary>
/// Whitelist безопасной навигации из уведомлений.
/// Разрешены только относительные маршруты приложения, открывающие ХК или задачу.
/// </summary>
public static class NotificationRouteGuard
{
    private static readonly string[] AllowedPrefixes =
    [
        "/хк/",
        "/задачи/",
        "/уведомления",
    ];

    public static bool IsSafeTarget(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.Contains("://", StringComparison.Ordinal)
        && !url.StartsWith("//", StringComparison.Ordinal)
        && AllowedPrefixes.Any(prefix => url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static string? Normalize(string? url) => IsSafeTarget(url) ? url : null;
}
