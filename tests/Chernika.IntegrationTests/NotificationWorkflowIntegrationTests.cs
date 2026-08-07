using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class NotificationWorkflowIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public NotificationWorkflowIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChangeStatus_RevisionRequired_NotifiesAuthor_WithHKReturnedForRevision()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.RevisionRequired, comment: "Уточнить нормы расхода.");
        Assert.True(success, error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);

        var notification = await s.Db.Notifications.AsNoTracking()
            .SingleAsync(n => n.UserId == _fixture.NormAdminA.Id
                && n.EntityType == "HKCard" && n.EntityId == cardId
                && n.Type == NotificationType.HKReturnedForRevision);

        Assert.Equal($"ХК {card.Code} возвращена на доработку", notification.Title);
        Assert.Equal("Уточнить нормы расхода.", notification.Message);
        Assert.Equal($"/хк/{cardId}", notification.NavigationUrl);
        Assert.False(notification.IsRead);
        Assert.Equal(_fixture.BranchA, notification.BranchId);
    }

    [Fact]
    public async Task ChangeStatus_Approved_NotifiesAuthor_WithHKApproved()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);
        Assert.True(success, error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);

        var notification = await s.Db.Notifications.AsNoTracking()
            .SingleAsync(n => n.UserId == _fixture.NormAdminA.Id
                && n.EntityType == "HKCard" && n.EntityId == cardId
                && n.Type == NotificationType.HKApproved);

        Assert.Equal($"ХК {card.Code} утверждена", notification.Title);
        Assert.Equal($"/хк/{cardId}", notification.NavigationUrl);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public async Task CreateProposalAsync_AssignsReviewTaskAndNotifiesSingleBranchNormAdmin()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var proposal = await s.HK.CreateProposalAsync(
            cardId, ProposalTargetType.Node,
            "N-NEW-1", "Новый узел", "Описание", gost: null, type: null);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.ReferenceProposalPending
                && n.EntityType == "ReferenceProposal" && n.EntityId == proposal.Id)
            .ToListAsync();

        var notification = Assert.Single(notifications);
        Assert.Equal(_fixture.NormAdminA2.Id, notification.UserId);
        Assert.Equal("Новое предложение справочника: Новый узел", notification.Title);
        Assert.Equal($"/хк/{cardId}", notification.NavigationUrl);
        Assert.Equal($"ref-proposal:{proposal.Id}:{notification.UserId}", notification.DeduplicationKey);
        Assert.Equal(_fixture.BranchA, notification.BranchId);
        Assert.False(notification.IsRead);

        var task = await s.Db.WorkTasks.AsNoTracking()
            .SingleAsync(t => t.EntityType == "ReferenceProposal" && t.EntityId == proposal.Id
                && t.Type == WorkTaskType.ReferenceProposalReview);
        Assert.Equal(_fixture.NormAdminA2.Id, task.AssignedToUserId);
        Assert.Equal(_fixture.BranchA, task.BranchId);
        Assert.Equal(WorkTaskStatus.Open, task.Status);
        Assert.Equal(task.Id, notification.WorkTaskId);

        var createdAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "ReferenceProposal" && a.EntityId == proposal.Id.ToString() && a.Action == "Created");
        Assert.True(createdAudit);

        var notificationAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "Notification" && a.EntityId == notification.Id.ToString() && a.Action == "Notification.Created");
        Assert.True(notificationAudit);

        var taskAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "WorkTask" && a.EntityId == task.Id.ToString() && a.Action == "Task.Created");
        Assert.True(taskAudit);
    }

    [Fact]
    public async Task CreateProposalAsync_DoesNotNotifyNormAdminOfAnotherBranch()
    {
        await using var s = _fixture.CreateScope();

        var branchC = Guid.NewGuid();
        var branchD = Guid.NewGuid();
        s.Db.Branches.AddRange(
            new Branch { Id = branchC, Name = "Филиал В", Code = "C" },
            new Branch { Id = branchD, Name = "Филиал Г", Code = "D" });
        await s.Db.SaveChangesAsync();

        var author = await CreateUserAsync(s, "normadmin_c", nameof(UserRole.NormAdmin), branchC);
        var otherBranchNormAdmin = await CreateUserAsync(s, "normadmin_d", nameof(UserRole.NormAdmin), branchD);

        var cardId = await CreateDraftCardAsync(s, author.Id);

        s.User.CurrentUserId = Guid.Parse(author.Id);
        var proposal = await s.HK.CreateProposalAsync(
            cardId, ProposalTargetType.Node,
            "N-NO-2", "Узел без проверяющего в филиале", description: null, gost: null, type: null);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.ReferenceProposalPending
                && n.EntityType == "ReferenceProposal" && n.EntityId == proposal.Id)
            .ToListAsync();

        Assert.Empty(notifications);
        Assert.DoesNotContain(notifications, n => n.UserId == otherBranchNormAdmin.Id);

        var warning = await s.Db.AuditLogs.AsNoTracking()
            .SingleOrDefaultAsync(a => a.EntityType == "ReferenceProposal" && a.EntityId == proposal.Id.ToString()
                && a.Action == "ReferenceProposal.NoNormAdmin");
        Assert.NotNull(warning);

        var adminTask = await s.Db.WorkTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.EntityType == "ReferenceProposal" && t.EntityId == proposal.Id
                && t.Type == WorkTaskType.UserAdministration);
        Assert.NotNull(adminTask);
        Assert.Equal(_fixture.SystemAdminUser.Id, adminTask!.AssignedToUserId);
        Assert.Equal(branchC, adminTask.BranchId);
    }

    [Fact]
    public async Task CreateProposalAsync_SingleReviewer_NotifiesExactlyOnce()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var proposal = await s.HK.CreateProposalAsync(
            cardId, ProposalTargetType.Node,
            "N-NEW-3", "Новый узел 3", description: null, gost: null, type: null);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.ReferenceProposalPending
                && n.EntityType == "ReferenceProposal" && n.EntityId == proposal.Id)
            .ToListAsync();

        var notification = Assert.Single(notifications);
        Assert.Equal($"ref-proposal:{proposal.Id}:{notification.UserId}", notification.DeduplicationKey);
        Assert.Equal(_fixture.NormAdminA2.Id, notification.UserId);
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        TestScope s, string login, string role, Guid branchId)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = true,
        };

        var result = await s.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Создание пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        var roleResult = await s.Users.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
            throw new InvalidOperationException(
                "Назначение роли не удалось: " + string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        return user;
    }

    private async Task<Guid> CreateDraftCardAsync(TestScope s, string actorId)
    {
        s.User.CurrentUserId = Guid.Parse(actorId);

        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел тест" };
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "АУ-" + Guid.NewGuid().ToString("N")[..6], Name = "СЕ тест" };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            Purpose = "Тест уведомлений",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new()
                {
                    AssemblyUnitId = au.Id,
                    Quantity = 2,
                    Volume = 1.5m,
                    UnitOfMeasure = "кг",
                    SortOrder = 1,
                    Materials = new List<HKCardItemMaterial>(),
                },
            },
        };

        var created = await s.HK.CreateAsync(card);
        Assert.Equal(HKCardStatus.Draft, created.Status);
        return created.Id;
    }
}
