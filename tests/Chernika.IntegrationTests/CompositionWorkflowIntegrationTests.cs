using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CompositionWorkflowIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CompositionWorkflowIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Submit_CreatesOneGroup_WithTaskPerNormAdmin()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateAsync(s, compositionId, partId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.SubmitForReviewAsync(compositionId);

        var entity = "ProductComposition";
        var group = await s.Db.WorkTaskGroups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.EntityType == entity && g.EntityId == compositionId
                && g.TaskType == nameof(WorkTaskType.CompositionReview) && g.CompletedAt == null);
        Assert.NotNull(group);

        var tasks = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => t.WorkTaskGroupId == group.Id && !t.IsDeleted)
            .ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(group.Id, t.WorkTaskGroupId));
        Assert.Contains(tasks, t => t.AssignedToUserId == _fixture.NormAdminA.Id);
        Assert.Contains(tasks, t => t.AssignedToUserId == _fixture.NormAdminA2.Id);
    }

    [Fact]
    public async Task CompleteGroupByOneNormAdmin_ClosesAllTasks()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateAsync(s, compositionId, partId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.SubmitForReviewAsync(compositionId);

        var task = await s.Db.WorkTasks.AsNoTracking()
            .FirstAsync(t => t.EntityType == "ProductComposition" && t.EntityId == compositionId
                && t.AssignedToUserId == _fixture.NormAdminA.Id);

        var result = await s.Tasks.CompleteGroupAsync(task.Id, _fixture.NormAdminA.Id, null);
        Assert.False(result.AlreadyCompleted);

        var group = await s.Db.WorkTaskGroups.AsNoTracking()
            .FirstAsync(g => g.Id == task.WorkTaskGroupId);
        Assert.NotNull(group.CompletedAt);
        Assert.Equal(_fixture.NormAdminA.Id, group.CompletedByUserId);

        var tasks = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => t.WorkTaskGroupId == group.Id && !t.IsDeleted)
            .ToListAsync();
        Assert.All(tasks, t => Assert.Equal(WorkTaskStatus.Completed, t.Status));
        Assert.All(tasks, t => Assert.Equal(_fixture.NormAdminA.Id, t.CompletedByUserId));
    }

    [Fact]
    public async Task CompleteGroupBySecondNormAdmin_IsIdempotent()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateAsync(s, compositionId, partId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.SubmitForReviewAsync(compositionId);

        var taskA = await s.Db.WorkTasks.AsNoTracking()
            .FirstAsync(t => t.EntityType == "ProductComposition" && t.EntityId == compositionId
                && t.AssignedToUserId == _fixture.NormAdminA.Id);
        var taskA2 = await s.Db.WorkTasks.AsNoTracking()
            .FirstAsync(t => t.EntityType == "ProductComposition" && t.EntityId == compositionId
                && t.AssignedToUserId == _fixture.NormAdminA2.Id);

        var first = await s.Tasks.CompleteGroupAsync(taskA.Id, _fixture.NormAdminA.Id, null);
        Assert.False(first.AlreadyCompleted);

        var second = await s.Tasks.CompleteGroupAsync(taskA2.Id, _fixture.NormAdminA2.Id, null);
        Assert.True(second.AlreadyCompleted);
        Assert.Contains("уже выполнена", second.Message);

        var audits = await s.Db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "WorkTaskGroup" && a.Action == "WorkTaskGroup.Completed"
                && a.EntityId == taskA.WorkTaskGroupId.ToString())
            .ToListAsync();
        Assert.Single(audits);
    }

    [Fact]
    public async Task RepeatedSubmit_DoesNotDuplicateReviewGroup()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateAsync(s, compositionId, partId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.SubmitForReviewAsync(compositionId);
        await s.Equipment.ReturnToDraftAsync(compositionId, "Доработка");
        await s.Equipment.SubmitForReviewAsync(compositionId);

        var groups = await s.Db.WorkTaskGroups.AsNoTracking()
            .Where(g => g.EntityType == "ProductComposition" && g.EntityId == compositionId
                && g.TaskType == nameof(WorkTaskType.CompositionReview) && g.CompletedAt == null)
            .ToListAsync();
        Assert.Single(groups);
    }

    [Fact]
    public async Task NoNormAdmin_CreatesSystemAdminFallbackTaskAndNotification()
    {
        await using var s = _fixture.CreateScope();

        var branchC = Guid.NewGuid();
        s.Db.Branches.Add(new Branch { Id = branchC, Name = "Филиал C", Code = "C" });

        var normAdminC = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "normadmin_c",
            FullName = "Тест normadmin_c",
            BranchId = branchC,
            IsActive = true
        };
        await s.Users.CreateAsync(normAdminC);
        await s.Users.AddToRoleAsync(normAdminC, nameof(UserRole.NormAdmin));

        s.User.CurrentUserId = Guid.Parse(normAdminC.Id);
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие C" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Тест"));
        var part = await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Часть", null, 1));
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат C" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(comp.Id, part.Id, aggregate.Id, 1));

        await s.Users.RemoveFromRoleAsync(normAdminC, nameof(UserRole.NormAdmin));

        await s.Equipment.SubmitForReviewAsync(comp.Id);

        var fallback = await s.Db.WorkTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.EntityType == "ProductComposition" && t.EntityId == comp.Id
                && t.Type == WorkTaskType.CompositionReview && t.AssignedRole == nameof(UserRole.SystemAdmin));
        Assert.NotNull(fallback);

        var notification = await s.Db.Notifications.AsNoTracking()
            .SingleOrDefaultAsync(n => n.UserId == _fixture.SystemAdminUser.Id
                && n.Type == NotificationType.System
                && n.EntityType == "ProductComposition" && n.EntityId == comp.Id);
        Assert.NotNull(notification);
    }

    [Fact]
    public async Task Submit_CreatesCompositionReviewRequestedNotification()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateAsync(s, compositionId, partId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.SubmitForReviewAsync(compositionId);

        Assert.True(await s.Db.Notifications.AnyAsync(n => n.UserId == _fixture.NormAdminA.Id
            && n.Type == NotificationType.CompositionReviewRequested
            && n.EntityType == "ProductComposition" && n.EntityId == compositionId));
        Assert.True(await s.Db.Notifications.AnyAsync(n => n.UserId == _fixture.NormAdminA2.Id
            && n.Type == NotificationType.CompositionReviewRequested
            && n.EntityType == "ProductComposition" && n.EntityId == compositionId));
    }

    [Fact]
    public async Task Return_CreatesCompositionReturnedToDraftNotificationAndAuthorTask()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateAsync(s, compositionId, partId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.SubmitForReviewAsync(compositionId);
        await s.Equipment.ReturnToDraftAsync(compositionId, "Нужна доработка");

        var authorTask = await s.Db.WorkTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.EntityType == "ProductComposition" && t.EntityId == compositionId
                && t.Type == WorkTaskType.CompositionReview
                && t.AssignedToUserId == _fixture.NormAdminA.Id
                && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue));
        Assert.NotNull(authorTask);

        Assert.True(await s.Db.Notifications.AnyAsync(n => n.UserId == _fixture.NormAdminA.Id
            && n.Type == NotificationType.CompositionReturnedToDraft
            && n.EntityType == "ProductComposition" && n.EntityId == compositionId));
    }

    [Fact]
    public async Task Approve_CreatesCompositionApprovedNotification()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateAsync(s, compositionId, partId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.SubmitForReviewAsync(compositionId);
        await s.Equipment.ApproveCompositionAsync(compositionId, null);

        Assert.True(await s.Db.Notifications.AnyAsync(n => n.UserId == _fixture.NormAdminA.Id
            && n.Type == NotificationType.CompositionApproved
            && n.EntityType == "ProductComposition" && n.EntityId == compositionId));
    }

    [Fact]
    public async Task ReadinessProblem_CreatesReadinessGroupAndNotifiesHKAuthorOnce()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Guid.NewGuid().ToString("N")[..6],
            Version = "v1",
            ObjectLevel = HKObjectLevel.Aggregate,
            AggregateId = aggregate.Id,
            Status = HKCardStatus.Archived,
            EffectiveDate = DateTime.UtcNow.AddDays(-10),
            ExpirationDate = DateTime.UtcNow.AddDays(10),
            BranchId = _fixture.BranchA,
            AuthorId = Guid.Parse(_fixture.NormAdminA2.Id)
        };
        s.Db.HKCards.Add(hk);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        await s.Equipment.SubmitForReviewAsync(compositionId);
        await s.Equipment.ApproveCompositionAsync(compositionId, null);

        var group = await s.Db.WorkTaskGroups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.EntityType == "Aggregate" && g.EntityId == aggregate.Id
                && g.TaskType == nameof(WorkTaskType.CompositionReadiness) && g.CompletedAt == null);
        Assert.NotNull(group);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.UserId == _fixture.NormAdminA2.Id
                && n.Type == NotificationType.CompositionReadinessIssue
                && n.EntityType == "Aggregate" && n.EntityId == aggregate.Id)
            .ToListAsync();
        Assert.Single(notifications);
    }

    private async Task<Aggregate> AddAggregateAsync(TestScope s, Guid compositionId, Guid partId)
    {
        var aggregate = new Aggregate
        {
            Id = Guid.NewGuid(),
            Code = "A-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Агрегат тест"
        };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        return aggregate;
    }

    private async Task<(Guid CompositionId, Guid ModelId, Guid PartId)> CreateProductCompositionDraftAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Тест"));
        var part = await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Силовая установка", null, 1));
        return (comp.Id, model.Id, part.Id);
    }
}
