using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardWorkflowIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardWorkflowIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChangeStatus_OnReview_CreatesHKReviewTask_WithRealAuthor()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        Assert.True(success, error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.OnReview, card.Status);

        var task = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKReview);
        Assert.Equal(WorkTaskStatus.Open, task.Status);
        Assert.Equal(_fixture.NormAdminA.Id, task.CreatedByUserId);
        Assert.Equal(_fixture.BranchA, task.BranchId);
        Assert.Equal(card.Code, task.EntityCodeSnapshot);
        Assert.Equal("v" + card.Version, task.EntityTitleSnapshot);
        Assert.NotNull(task.DueDateUtc);
        Assert.InRange(task.DueDateUtc!.Value, DateTime.UtcNow.AddDays(6.5), DateTime.UtcNow.AddDays(7.5));

        var branchAdmins = new[] { _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id };
        Assert.Contains(task.AssignedToUserId, branchAdmins);

        var createdAudit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "WorkTask" && a.EntityId == task.Id.ToString() && a.Action == "Task.Created");
        Assert.Equal(Guid.Parse(_fixture.NormAdminA.Id), createdAudit.UserId);

        var notification = await s.Db.Notifications.AsNoTracking()
            .SingleAsync(n => n.WorkTaskId == task.Id);
        Assert.Equal(NotificationType.TaskAssigned, notification.Type);
        Assert.Equal("task-assigned:" + task.Id + ":" + task.AssignedToUserId, notification.DeduplicationKey);
    }

    [Fact]
    public async Task ChangeStatus_RevisionRequired_CancelsReviewAndCreatesRevisionForAuthor()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.RevisionRequired);
        Assert.True(success, error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.RevisionRequired, card.Status);

        var reviewTask = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKReview);
        Assert.Equal(WorkTaskStatus.Cancelled, reviewTask.Status);

        var cancelledAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "WorkTask" && a.EntityId == reviewTask.Id.ToString() && a.Action == "Task.Cancelled");
        Assert.True(cancelledAudit);

        var revision = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKRevision);
        Assert.Equal(WorkTaskStatus.Open, revision.Status);
        Assert.Equal(card.AuthorId!.Value.ToString(), revision.AssignedToUserId);
        Assert.Equal(_fixture.NormAdminA.Id, revision.CreatedByUserId);
        Assert.NotNull(revision.DueDateUtc);
        Assert.InRange(revision.DueDateUtc!.Value, DateTime.UtcNow.AddDays(6.5), DateTime.UtcNow.AddDays(7.5));
    }

    [Fact]
    public async Task ChangeStatus_Resubmit_CompletesRevisionTaskForAuthor()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.RevisionRequired);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        Assert.True(success, error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.OnReview, card.Status);

        var revision = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKRevision);
        Assert.Equal(WorkTaskStatus.Completed, revision.Status);
        Assert.Equal("ХК повторно отправлена на проверку", revision.CompletionComment);
        Assert.Equal(_fixture.NormAdminA.Id, revision.CompletedByUserId);
        Assert.NotNull(revision.CompletedAtUtc);

        var completedAudit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "WorkTask" && a.EntityId == revision.Id.ToString() && a.Action == "Task.Completed");
        Assert.Equal(Guid.Parse(_fixture.NormAdminA.Id), completedAudit.UserId);
        Assert.Equal("ХК повторно отправлена на проверку", completedAudit.Details);

        var review = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKReview
            && t.Status == WorkTaskStatus.Open);
        Assert.Equal(WorkTaskStatus.Open, review.Status);
    }

    [Fact]
    public async Task RepeatedRevisionRequired_AfterResubmit_CreatesFreshRevision()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.RevisionRequired);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.RevisionRequired);
        Assert.True(success, error);

        var revisions = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKRevision)
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(WorkTaskStatus.Completed, revisions[0].Status);
        Assert.Equal("ХК повторно отправлена на проверку", revisions[0].CompletionComment);
        Assert.Equal(WorkTaskStatus.Open, revisions[1].Status);

        var reviewTasks = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKReview)
            .ToListAsync();
        Assert.Equal(2, reviewTasks.Count);
        Assert.All(reviewTasks, t => Assert.Equal(WorkTaskStatus.Cancelled, t.Status));
    }

    [Fact]
    public async Task ChangeStatus_Approved_ArchivesPreviousApprovedVersion()
    {
        await using var s = _fixture.CreateScope();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);
        var v1 = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, v1.Status);

        var cardId2 = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var v2 = await s.Db.HKCards.FirstAsync(h => h.Id == cardId2);
        v2.Code = v1.Code;
        v2.Version = "vTEST2";
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId2, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId2, HKCardStatus.Approved);
        Assert.True(success, error);

        var v1After = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Archived, v1After.Status);

        var archiveLog = await s.Db.HKCardStatusLogs.AsNoTracking()
            .AnyAsync(l => l.HKCardId == cardId && l.ToStatus == HKCardStatus.Archived);
        Assert.True(archiveLog);

        var archiveAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "HKCard" && a.EntityId == cardId.ToString()
                && a.Action == $"Status:{HKCardStatus.Archived}");
        Assert.True(archiveAudit);
    }

    [Fact]
    public async Task ChangeStatus_Approved_CompletesReviewAndCreatesRecalculationTasks()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        var model = new EquipmentModel
        {
            Id = Guid.NewGuid(),
            Index = "И-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Изделие тест",
        };
        var pc = new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = model.Id,
            Version = "1",
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
        };
        var instance = new EquipmentInstance
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = model.Id,
            SerialNumber = "SN-" + Guid.NewGuid().ToString("N")[..6],
            Index = "И-1",
            Name = "Экземпляр тест",
        };
        s.Db.EquipmentModels.Add(model);
        s.Db.ProductCompositions.Add(pc);
        s.Db.EquipmentInstances.Add(instance);
        await s.Db.SaveChangesAsync();

        s.Db.IndividualCards.Add(new IndividualCard
        {
            Id = Guid.NewGuid(),
            EquipmentInstanceId = instance.Id,
            HKCardId = card.Id,
            NodeId = card.NodeId!.Value,
            ProductCompositionId = pc.Id,
            Version = "1",
            TotalNorm = 1,
        });
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);
        Assert.True(success, error);

        var reviewTask = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKReview);
        Assert.Equal(WorkTaskStatus.Completed, reviewTask.Status);
        Assert.Equal(_fixture.NormAdminA.Id, reviewTask.CompletedByUserId);
        Assert.Equal("ХК утверждена", reviewTask.CompletionComment);
        Assert.NotNull(reviewTask.CompletedAtUtc);

        var completedAudit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "WorkTask" && a.EntityId == reviewTask.Id.ToString() && a.Action == "Task.Completed");
        Assert.Equal(Guid.Parse(_fixture.NormAdminA.Id), completedAudit.UserId);

        var recalculation = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "EquipmentInstance" && t.EntityId == instance.Id && t.Type == WorkTaskType.HKReview);
        Assert.Equal("Пересчёт инд. карт — экземпляр", recalculation.Title);
        Assert.Equal(WorkTaskStatus.Open, recalculation.Status);
        Assert.Equal(_fixture.OperatorA.Id, recalculation.AssignedToUserId);
        Assert.Equal(_fixture.NormAdminA.Id, recalculation.CreatedByUserId);
        Assert.NotNull(recalculation.DueDateUtc);
        Assert.InRange(recalculation.DueDateUtc!.Value, DateTime.UtcNow.AddDays(13), DateTime.UtcNow.AddDays(15));
    }

    [Fact]
    public async Task GetOpenTaskCountAsync_CountsOnlyActiveAssignedTasks()
    {
        await using var s = _fixture.CreateScope();

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var beforeOperator = await s.Tasks.GetOpenTaskCountAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var beforeNormAdmin = await s.Tasks.GetOpenTaskCountAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var opId1 = (await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Открытая 1", Type: WorkTaskType.HKReview, Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.OperatorA.Id, BranchId: _fixture.BranchA))).Id;
        var opId2 = (await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Открытая 2", Type: WorkTaskType.HKReview, Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.OperatorA.Id, BranchId: _fixture.BranchA))).Id;
        await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Чужая", Type: WorkTaskType.HKReview, Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.NormAdminA.Id, BranchId: _fixture.BranchA));

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        Assert.Equal(beforeOperator + 2, await s.Tasks.GetOpenTaskCountAsync());

        await s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(opId1, "Готово"));
        Assert.Equal(beforeOperator + 1, await s.Tasks.GetOpenTaskCountAsync());

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        Assert.Equal(beforeNormAdmin + 1, await s.Tasks.GetOpenTaskCountAsync());
    }

    [Fact]
    public async Task GetStatusCountsAsync_MatchesDirectGrouping()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Счётчик", Type: WorkTaskType.HKReview, Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.OperatorA.Id, BranchId: _fixture.BranchA));

        var counts = await s.Tasks.GetStatusCountsAsync();

        var expected = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => !t.IsDeleted)
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count);

        Assert.Equal(expected, counts);
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
            Purpose = "Тест workflow",
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
