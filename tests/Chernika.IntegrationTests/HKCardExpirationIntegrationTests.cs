using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardExpirationIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardExpirationIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProcessExpiringCards_90Days_CreatesWarningOnly()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateApprovedCardAsync(s, 90);

        await s.Expiration.ProcessExpiringCardsAsync();

        Assert.Equal(2, await s.Db.Notifications.AsNoTracking().CountAsync(n =>
            n.EntityId == cardId && n.Type == NotificationType.HKExpiring));
        Assert.Equal(0, await s.Db.Notifications.AsNoTracking().CountAsync(n =>
            n.EntityId == cardId && n.Type == NotificationType.HKExpired));
        Assert.Equal(0, await s.Db.WorkTasks.AsNoTracking().CountAsync(t =>
            !t.IsDeleted && t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKExpirationReview));

        var audit = await s.Db.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.EntityType == "HKCard" && a.EntityId == cardId.ToString() && a.Action == "HK.ExpirationWarningCreated");
        Assert.Equal(Chernika.Domain.AuditSource.System, audit.Source);
        Assert.Equal(Guid.Empty, audit.UserId);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, card.Status);
    }

    [Fact]
    public async Task ProcessExpiringCards_30Days_CreatesWarningAndReviewTask()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateApprovedCardAsync(s, 30);

        await s.Expiration.ProcessExpiringCardsAsync();

        Assert.Equal(2, await s.Db.Notifications.AsNoTracking().CountAsync(n =>
            n.EntityId == cardId && n.Type == NotificationType.HKExpiring));

        var task = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            !t.IsDeleted && t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKExpirationReview);
        Assert.Equal(WorkTaskStatus.Open, task.Status);
        Assert.Null(task.CreatedByUserId);
        Assert.Contains(task.AssignedToUserId, new[] { _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id });

        var taskCreatedAudit = await s.Db.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.EntityType == "WorkTask" && a.EntityId == task.Id.ToString() && a.Action == "Task.Created");
        Assert.Equal(Chernika.Domain.AuditSource.System, taskCreatedAudit.Source);

        Assert.Equal(1, await s.Db.AuditLogs.AsNoTracking().CountAsync(a =>
            a.EntityType == "HKCard" && a.EntityId == cardId.ToString() && a.Action == "HK.ExpirationWarningCreated"));
    }

    [Fact]
    public async Task ProcessExpiringCards_RerunSameDay_DoesNotDuplicateNotificationsTasksOrAudits()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateApprovedCardAsync(s, 7);

        await s.Expiration.ProcessExpiringCardsAsync();

        Assert.Equal(2, await s.Db.Notifications.AsNoTracking().CountAsync(n =>
            n.EntityId == cardId && n.Type == NotificationType.HKExpiring));
        var task = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            !t.IsDeleted && t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKExpirationReview);
        var taskCreatedAudits = await s.Db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "WorkTask" && a.EntityId == task.Id.ToString() && a.Action == "Task.Created")
            .ToListAsync();
        Assert.Single(taskCreatedAudits);

        await s.Expiration.ProcessExpiringCardsAsync();

        Assert.Equal(2, await s.Db.Notifications.AsNoTracking().CountAsync(n =>
            n.EntityId == cardId && n.Type == NotificationType.HKExpiring));
        Assert.Equal(1, await s.Db.WorkTasks.AsNoTracking().CountAsync(t =>
            !t.IsDeleted && t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKExpirationReview));
        Assert.Equal(1, await s.Db.AuditLogs.AsNoTracking().CountAsync(a =>
            a.EntityType == "HKCard" && a.EntityId == cardId.ToString() && a.Action == "HK.ExpirationWarningCreated"));
        Assert.Equal(1, await s.Db.AuditLogs.AsNoTracking().CountAsync(a =>
            a.EntityType == "WorkTask" && a.EntityId == task.Id.ToString() && a.Action == "Task.Created"));
    }

    [Fact]
    public async Task ProcessExpiringCards_ExpirationDay_SendsExpiredNotificationWithoutArchiving()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateApprovedCardAsync(s, 0);

        await s.Expiration.ProcessExpiringCardsAsync();

        Assert.Equal(2, await s.Db.Notifications.AsNoTracking().CountAsync(n =>
            n.EntityId == cardId && n.Type == NotificationType.HKExpired));
        Assert.Equal(0, await s.Db.Notifications.AsNoTracking().CountAsync(n =>
            n.EntityId == cardId && n.Type == NotificationType.HKExpiring));

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, card.Status);
    }

    [Fact]
    public async Task ProcessExpiringCards_PastExpiration_ArchivesCardAndClosesReviewTask()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateApprovedCardAsync(s, 7);

        await s.Expiration.ProcessExpiringCardsAsync();
        var task = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            !t.IsDeleted && t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKExpirationReview);

        var card = await s.Db.HKCards.SingleAsync(h => h.Id == cardId);
        card.ExpirationDate = DateTime.UtcNow.Date.AddDays(-1);
        await s.Db.SaveChangesAsync();

        await s.Expiration.ProcessExpiringCardsAsync();

        var archived = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Archived, archived.Status);

        var closedTask = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(WorkTaskStatus.Cancelled, closedTask.Status);

        var audit = await s.Db.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.EntityType == "HKCard" && a.EntityId == cardId.ToString() && a.Action == "HK.ExpiredArchived");
        Assert.Equal(Chernika.Domain.AuditSource.System, audit.Source);
        Assert.Equal(Guid.Empty, audit.UserId);

        var statusLog = await s.Db.HKCardStatusLogs.AsNoTracking().SingleAsync(l => l.HKCardId == cardId && l.ToStatus == HKCardStatus.Archived);
        Assert.Equal(HKCardStatus.Approved, statusLog.FromStatus);
        Assert.Equal(Guid.Empty, statusLog.ChangedByUserId);
    }

    private async Task<Guid> CreateApprovedCardAsync(TestScope s, int expirationDays)
    {
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        Assert.True(success, error);
        (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);
        Assert.True(success, error);

        var card = await s.Db.HKCards.SingleAsync(h => h.Id == cardId);
        card.ExpirationDate = DateTime.UtcNow.Date.AddDays(expirationDays);
        await s.Db.SaveChangesAsync();
        return cardId;
    }

    private async Task<Guid> CreateDraftCardAsync(TestScope s, string actorId)
    {
        s.User.CurrentUserId = Guid.Parse(actorId);

        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел сроки" };
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "АУ-" + Guid.NewGuid().ToString("N")[..6], Name = "СЕ сроки" };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            Purpose = "Тест сроков",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new()
                {
                    AssemblyUnitId = au.Id,
                    Quantity = 1,
                    Volume = 1m,
                    UnitOfMeasure = "кг",
                    SortOrder = 1,
                    Materials = new List<HKCardItemMaterial>(),
                },
            },
        };

        var created = await s.HK.CreateAsync(card);
        return created.Id;
    }
}
