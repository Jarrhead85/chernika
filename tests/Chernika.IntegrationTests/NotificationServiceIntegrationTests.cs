using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class NotificationServiceIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public NotificationServiceIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateForUsersAsync_CreatesBatchForDistinctUsers()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await s.Notifications.CreateForUsersAsync(
            new[] { _fixture.OperatorA.Id, _fixture.NormAdminA.Id, _fixture.OperatorA.Id },
            new CreateNotificationCommand(
                Type: NotificationType.Information,
                Title: "Плановое обновление",
                EntityType: "System",
                EntityId: Guid.NewGuid(),
                DeduplicationKey: "info:update:1"));

        var rows = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Title == "Плановое обновление")
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, n => n.UserId == _fixture.OperatorA.Id);
        Assert.Contains(rows, n => n.UserId == _fixture.NormAdminA.Id);
    }

    [Fact]
    public async Task CreateForUsersAsync_RespectsDeduplicationKey()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var command = new CreateNotificationCommand(
            Type: NotificationType.Information,
            Title: "Повторное уведомление",
            DeduplicationKey: "info:repeat:1");

        await s.Notifications.CreateForUsersAsync(new[] { _fixture.OperatorA.Id }, command);
        await s.Notifications.CreateForUsersAsync(new[] { _fixture.OperatorA.Id }, command);

        var rows = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.DeduplicationKey == "info:repeat:1" && n.UserId == _fixture.OperatorA.Id)
            .ToListAsync();

        Assert.Single(rows);
    }

    [Fact]
    public async Task AddAsync_ReturnsNull_WhenDeduplicationKeyExists()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var command = new CreateNotificationCommand(
            Type: NotificationType.TaskAssigned,
            Title: "Задача",
            DeduplicationKey: "dedup:1");

        var first = await s.Notifications.AddAsync(_fixture.OperatorA.Id, command);
        Assert.NotNull(first);
        await s.Db.SaveChangesAsync();

        var second = await s.Notifications.AddAsync(_fixture.OperatorA.Id, command);
        Assert.Null(second);
        await s.Db.SaveChangesAsync();

        var rows = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.DeduplicationKey == "dedup:1")
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task CreateFromWorkflowAsync_AddsNotificationAndAudit_WithoutOwnSave()
    {
        await using var s = _fixture.CreateScope();
        var actorId = Guid.Parse(_fixture.SystemAdminUser.Id);
        s.User.CurrentUserId = actorId;
        var entityId = Guid.NewGuid();

        var notification = await s.Notifications.CreateFromWorkflowAsync(
            _fixture.OperatorA.Id,
            new CreateNotificationCommand(
                Type: NotificationType.Information,
                Title: "Атомарное уведомление",
                EntityType: "System",
                EntityId: entityId,
                DeduplicationKey: "atomic:" + entityId),
            actorId);

        Assert.NotNull(notification);
        Assert.Equal(0, await s.Db.Notifications.CountAsync(n => n.Id == notification!.Id));

        await s.Db.SaveChangesAsync();

        var row = await s.Db.Notifications.AsNoTracking().SingleAsync(n => n.Id == notification!.Id);
        Assert.False(row.IsRead);

        var audit = await s.Db.AuditLogs.AsNoTracking()
            .SingleOrDefaultAsync(a => a.EntityType == "Notification" && a.EntityId == notification!.Id.ToString()
                && a.Action == "Notification.Created");
        Assert.NotNull(audit);
        Assert.Equal(actorId, audit!.UserId);
    }

    [Fact]
    public async Task CreateFromWorkflowAsync_DuplicateDeduplicationKey_ReturnsNull_AndNoAudit()
    {
        await using var s = _fixture.CreateScope();
        var actorId = Guid.Parse(_fixture.SystemAdminUser.Id);
        s.User.CurrentUserId = actorId;
        var entityId = Guid.NewGuid();

        var command = new CreateNotificationCommand(
            Type: NotificationType.Information,
            Title: "Дед уведомление",
            DeduplicationKey: "wfd:" + entityId);

        var first = await s.Notifications.CreateFromWorkflowAsync(_fixture.OperatorA.Id, command, actorId);
        Assert.NotNull(first);
        await s.Db.SaveChangesAsync();

        var second = await s.Notifications.CreateFromWorkflowAsync(_fixture.OperatorA.Id, command, actorId);
        Assert.Null(second);
        await s.Db.SaveChangesAsync();

        var rows = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.DeduplicationKey == "wfd:" + entityId)
            .ToListAsync();
        Assert.Single(rows);

        var audits = await s.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityType == "Notification" && a.EntityId == first!.Id.ToString()
                && a.Action == "Notification.Created");
        Assert.Equal(1, audits);
    }

    [Fact]
    public async Task GetMyNotificationsAsync_ReturnsOnlyOwnAndUnreadFilter()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var entityId = Guid.NewGuid();
        await s.Notifications.CreateForUsersAsync(
            new[] { _fixture.OperatorA.Id, _fixture.NormAdminA.Id },
            new CreateNotificationCommand(
                Type: NotificationType.Information,
                Title: "Фильтр",
                EntityId: entityId,
                DeduplicationKey: "filter:" + entityId));

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var all = await s.Notifications.GetMyNotificationsAsync(new NotificationQuery { PageSize = 50 });
        Assert.NotEmpty(all.Items);
        Assert.Contains(all.Items, n => n.EntityId == entityId);

        var unread = await s.Notifications.GetMyNotificationsAsync(new NotificationQuery { UnreadOnly = true });
        Assert.Contains(unread.Items, n => n.EntityId == entityId);

        var count = await s.Notifications.GetUnreadCountAsync();
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task MarkAsReadAsync_MarksOwnNotification()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var notificationId = await CreateNotificationAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await s.Notifications.MarkAsReadAsync(notificationId);

        var row = await s.Db.Notifications.AsNoTracking().FirstAsync(n => n.Id == notificationId);
        Assert.True(row.IsRead);
        Assert.NotNull(row.ReadAtUtc);

        var readAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "Notification" && a.EntityId == notificationId.ToString() && a.Action == "Notification.Read");
        Assert.True(readAudit);
    }

    [Fact]
    public async Task MarkAsReadAsync_OfOtherUsersNotification_Throws()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var notificationId = await CreateNotificationAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Notifications.MarkAsReadAsync(notificationId));

        var row = await s.Db.Notifications.AsNoTracking().FirstAsync(n => n.Id == notificationId);
        Assert.False(row.IsRead);
    }

    [Fact]
    public async Task MarkAllAsReadAsync_MarksAllOwnAndAuditsReadAll()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var n1 = await CreateNotificationAsync(s, _fixture.OperatorA.Id);
        var n2 = await CreateNotificationAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await s.Notifications.MarkAllAsReadAsync();

        var rows = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Id == n1 || n.Id == n2)
            .ToListAsync();
        Assert.All(rows, n => Assert.True(n.IsRead));

        var readAllAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == "Notification.ReadAll" && a.UserId == Guid.Parse(_fixture.OperatorA.Id));
        Assert.True(readAllAudit);
    }

    private async Task<Guid> CreateNotificationAsync(TestScope s, string userId)
    {
        await s.Notifications.CreateForUsersAsync(
            new[] { userId },
            new CreateNotificationCommand(
                Type: NotificationType.TaskAssigned,
                Title: "Уведомление " + Guid.NewGuid().ToString("N")[..8],
                DeduplicationKey: "test:" + Guid.NewGuid().ToString("N")));

        return await s.Db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Select(n => n.Id)
            .FirstAsync();
    }
}
