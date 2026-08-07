namespace Chernika.Infrastructure.Services;

public sealed class HKExpirationOptions
{
    public int[] WarningDays { get; set; } = new[] { 90, 30, 7 };

    public string DailyRunTimeUtc { get; set; } = "01:00";

    public int ReviewTaskDueDays { get; set; } = 14;

    public bool RunOnStartup { get; set; } = true;
}
