using Chernika.Domain;

namespace Chernika.UnitTests;

public class PermissionCatalogInvariantTests
{
    [Fact]
    public void PermissionCodes_All_MatchesPermissionCatalog_All()
    {
        var codes = PermissionCodes.All;
        var catalog = PermissionCatalog.All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(codes, catalog);
    }

    [Fact]
    public void TaskAndNotificationPermissions_AreRegistered()
    {
        var codes = PermissionCodes.All;

        Assert.Contains("Task.View", codes);
        Assert.Contains("Task.Assign", codes);
        Assert.Contains("Task.Complete", codes);
        Assert.Contains("Task.Cancel", codes);
        Assert.Contains("Notification.View", codes);
        Assert.Contains("Notification.MarkRead", codes);
    }

    [Fact]
    public void PermissionCatalog_Entries_HaveUniqueCodes()
    {
        var codes = PermissionCatalog.All.Select(p => p.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
    }
}
