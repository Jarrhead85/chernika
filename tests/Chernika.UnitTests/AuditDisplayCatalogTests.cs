using Chernika.Domain;

namespace Chernika.UnitTests;

public class AuditDisplayCatalogTests
{
    [Theory]
    [InlineData("Task.Created", "Задача создана")]
    [InlineData("Task.Assigned", "Задача назначена исполнителю")]
    [InlineData("Task.Started", "Задача взята в работу")]
    [InlineData("Task.Completed", "Задача выполнена")]
    [InlineData("Task.Cancelled", "Задача отменена")]
    [InlineData("Task.Overdue", "Задача просрочена")]
    [InlineData("Task.Deleted", "Задача удалена")]
    [InlineData("Notification.Created", "Уведомление создано")]
    [InlineData("Notification.Read", "Уведомление прочитано")]
    [InlineData("Notification.ReadAll", "Все уведомления прочитаны")]
    public void GetAction_ResolvesTaskAndNotificationActions(string action, string expectedTitle)
    {
        var display = AuditDisplayCatalog.GetAction(action);

        Assert.Equal(expectedTitle, display.Title);
        Assert.NotEqual("Неизвестное действие", display.Title);
    }

    [Fact]
    public void GetEntityTypeDisplay_ResolvesNotification()
    {
        Assert.Equal("Уведомление", AuditDisplayCatalog.GetEntityTypeDisplay("Notification"));
        Assert.Equal("Задача", AuditDisplayCatalog.GetEntityTypeDisplay("WorkTask"));
    }

    [Fact]
    public void GetFilterActions_ReturnsTaskAndNotificationGroups()
    {
        var taskActions = AuditDisplayCatalog.GetFilterActions("Tasks");
        Assert.Contains("Task.Created", taskActions);
        Assert.Contains("Task.Completed", taskActions);

        var notificationActions = AuditDisplayCatalog.GetFilterActions("Notifications");
        Assert.Contains("Notification.Read", notificationActions);
        Assert.Contains("Notification.ReadAll", notificationActions);
    }
}
