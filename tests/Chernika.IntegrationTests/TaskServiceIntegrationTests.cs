using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class TaskServiceIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public TaskServiceIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateAsync_CreatesTaskWithAuditAndNotification()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var entityId = Guid.NewGuid();
        var due = DateTime.UtcNow.AddDays(3);

        var dto = await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Подготовить отчёт",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.High,
            Description: "Проверить содержание",
            AssignedToUserId: _fixture.OperatorA.Id,
            BranchId: _fixture.BranchA,
            EntityType: "HKCard",
            EntityId: entityId,
            EntityCodeSnapshot: "ХК-001",
            EntityTitleSnapshot: "v2",
            DueDateUtc: due));

        Assert.Equal(WorkTaskStatus.Open, dto.Status);
        Assert.Equal(_fixture.OperatorA.Id, dto.AssignedToUserId);
        Assert.Equal(_fixture.BranchA, dto.BranchId);

        var task = await s.Db.WorkTasks.AsNoTracking().FirstAsync(t => t.Id == dto.Id);
        Assert.Equal(_fixture.SystemAdminUser.Id, task.CreatedByUserId);
        Assert.Equal(WorkTaskType.HKReview, task.Type);
        Assert.False(task.IsDeleted);

        var audits = await s.Db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "WorkTask" && a.EntityId == dto.Id.ToString())
            .ToListAsync();
        var createdAudit = Assert.Single(audits, a => a.Action == "Task.Created");
        Assert.Equal(Guid.Parse(_fixture.SystemAdminUser.Id), createdAudit.UserId);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.WorkTaskId == dto.Id)
            .ToListAsync();
        var assignedNotification = Assert.Single(notifications, n => n.Type == NotificationType.TaskAssigned);
        Assert.Equal(_fixture.OperatorA.Id, assignedNotification.UserId);
        Assert.Equal("task-assigned:" + dto.Id + ":" + _fixture.OperatorA.Id, assignedNotification.DeduplicationKey);
    }

    [Fact]
    public async Task CreateAsync_WithoutTaskAssignPermission_Throws()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Задача без прав",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.OperatorA.Id,
            BranchId: _fixture.BranchA)));
    }

    [Fact]
    public async Task CreateAsync_WithoutAssignee_Throws()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Задача без исполнителя",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal)));
    }

    [Fact]
    public async Task CreateAsync_WithForeignBranch_ThrowsForNonAdmin()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.HeadA.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Чужая ветка",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.OperatorA.Id,
            BranchId: _fixture.BranchB)));
    }

    [Fact]
    public async Task AssignAsync_ReassignsAndNotifiesNewAssignee()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var dto = await s.Tasks.AssignAsync(new AssignWorkTaskCommand(
            taskId, _fixture.NormAdminA.Id, null, "Передаю на проверку"));

        Assert.Equal(_fixture.NormAdminA.Id, dto.AssignedToUserId);

        var assignedAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "WorkTask" && a.EntityId == taskId.ToString() && a.Action == "Task.Assigned");
        Assert.True(assignedAudit);

        var notification = await s.Db.Notifications.AsNoTracking()
            .FirstOrDefaultAsync(n => n.WorkTaskId == taskId && n.UserId == _fixture.NormAdminA.Id);
        Assert.NotNull(notification);
        Assert.Equal(NotificationType.TaskAssigned, notification!.Type);
    }

    [Fact]
    public async Task StartAsync_TransitionsToInProgress_ByAssignee()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var dto = await s.Tasks.StartAsync(taskId);

        Assert.Equal(WorkTaskStatus.InProgress, dto.Status);
        Assert.NotNull(dto.StartedAtUtc);

        var startedAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "WorkTask" && a.EntityId == taskId.ToString() && a.Action == "Task.Started");
        Assert.True(startedAudit);
    }

    [Fact]
    public async Task StartAsync_ByNonAssignee_Throws()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Tasks.StartAsync(taskId));
    }

    [Fact]
    public async Task CompleteAsync_ByAssignee_CompletesAndNotifiesCreator()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id, createdBy: _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var dto = await s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(taskId, "Готово, приложен акт"));

        Assert.Equal(WorkTaskStatus.Completed, dto.Status);
        Assert.Equal(_fixture.OperatorA.Id, dto.CompletedByUserId);
        Assert.Equal("Готово, приложен акт", dto.CompletionComment);

        var completedAudit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "WorkTask" && a.EntityId == taskId.ToString() && a.Action == "Task.Completed");
        Assert.Equal(Guid.Parse(_fixture.OperatorA.Id), completedAudit.UserId);

        var notification = await s.Db.Notifications.AsNoTracking()
            .FirstOrDefaultAsync(n => n.WorkTaskId == taskId && n.UserId == _fixture.NormAdminA.Id);
        Assert.NotNull(notification);
        Assert.Equal(NotificationType.TaskCompleted, notification!.Type);
    }

    [Fact]
    public async Task CompleteAsync_ByNonAssignee_Throws()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(taskId)));
    }

    [Fact]
    public async Task CompleteAsync_ByGuest_WithoutPermission_Throws()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.GuestA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(taskId)));
    }

    [Fact]
    public async Task CompleteAsync_OnCompletedTask_Throws_AndDoesNotWriteAudit()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(taskId, "Первый раз"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(taskId, "Второй раз")));

        var completedAudits = await s.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityType == "WorkTask" && a.EntityId == taskId.ToString() && a.Action == "Task.Completed");
        Assert.Equal(1, completedAudits);
    }

    [Fact]
    public async Task CompleteAsync_OnCancelledTask_Throws_AndDoesNotWriteAudit()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await s.Tasks.CancelAsync(new CancelWorkTaskCommand(taskId, "Неактуально"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(taskId)));

        var cancelledAudits = await s.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityType == "WorkTask" && a.EntityId == taskId.ToString() && a.Action == "Task.Cancelled");
        Assert.Equal(1, cancelledAudits);
        var completedAudits = await s.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityType == "WorkTask" && a.EntityId == taskId.ToString() && a.Action == "Task.Completed");
        Assert.Equal(0, completedAudits);
    }

    [Fact]
    public async Task CancelAsync_BySystemAdmin_Works()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var dto = await s.Tasks.CancelAsync(new CancelWorkTaskCommand(taskId, "Отменена"));

        Assert.Equal(WorkTaskStatus.Cancelled, dto.Status);
    }

    [Fact]
    public async Task GetMyTasksAsync_ReturnsOwnAndRoleTasks_ByBranch()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var headATaskId = (await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Задача начальнику А",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.HeadA.Id,
            BranchId: _fixture.BranchA))).Id;

        var headBInA = (await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Задача начальнику А из Б",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.NormAdminB.Id,
            BranchId: _fixture.BranchA))).Id;

        s.User.CurrentUserId = Guid.Parse(_fixture.HeadA.Id);
        var paged = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery { PageSize = 50 });

        Assert.Contains(paged.Items, t => t.Id == headATaskId);
        Assert.DoesNotContain(paged.Items, t => t.Id == headBInA);
        Assert.All(paged.Items, t => Assert.Equal(_fixture.BranchA, t.BranchId));
    }

    [Fact]
    public async Task GetMyTasksAsync_SystemAdmin_SeesAllBranches()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var taskA = (await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Задача в филиале А",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.OperatorA.Id,
            BranchId: _fixture.BranchA))).Id;

        var taskB = (await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Задача в филиале Б",
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal,
            AssignedToUserId: _fixture.NormAdminB.Id,
            BranchId: _fixture.BranchB))).Id;

        var paged = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery { PageSize = 50 });

        Assert.Contains(paged.Items, t => t.Id == taskA);
        Assert.Contains(paged.Items, t => t.Id == taskB);
    }

    [Fact]
    public async Task GetMyTasksAsync_Paginates()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var tag = "Пагинация-" + Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < 5; i++)
        {
            await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
                Title: tag + " Задача №" + i,
                Type: WorkTaskType.HKReview,
                Priority: WorkTaskPriority.Normal,
                AssignedToUserId: _fixture.OperatorA.Id,
                BranchId: _fixture.BranchA));
        }

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var page1 = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery { Page = 1, PageSize = 2, BranchId = _fixture.BranchA, Text = tag });
        var page2 = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery { Page = 2, PageSize = 2, BranchId = _fixture.BranchA, Text = tag });

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.Empty(page1.Items.IntersectBy(page2.Items.Select(t => t.Id), t => t.Id));
    }

    [Fact]
    public async Task GetMyTasksAsync_SortsByPriorityAndCreated()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var tag = "Сорт-" + Guid.NewGuid().ToString("N")[..8];

        await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: tag + " Низкий", Type: WorkTaskType.HKReview, Priority: WorkTaskPriority.Low,
            AssignedToUserId: _fixture.OperatorA.Id, BranchId: _fixture.BranchA));
        await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: tag + " Критический", Type: WorkTaskType.HKReview, Priority: WorkTaskPriority.Critical,
            AssignedToUserId: _fixture.OperatorA.Id, BranchId: _fixture.BranchA));

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        var byPriority = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery
        {
            PageSize = 10,
            BranchId = _fixture.BranchA,
            Text = tag,
            SortBy = "priority",
        });
        Assert.Equal(new[] { WorkTaskPriority.Low, WorkTaskPriority.Critical }, byPriority.Items.Select(t => t.Priority));

        var byPriorityDesc = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery
        {
            PageSize = 10,
            BranchId = _fixture.BranchA,
            Text = tag,
            SortBy = "priority",
            SortDescending = true,
        });
        Assert.Equal(new[] { WorkTaskPriority.Critical, WorkTaskPriority.Low }, byPriorityDesc.Items.Select(t => t.Priority));
    }

    [Fact]
    public async Task SoftDeletedTask_IsExcludedFromQueries()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id);

        var task = await s.Db.WorkTasks.FirstAsync(t => t.Id == taskId);
        task.IsDeleted = true;
        task.UpdatedAtUtc = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var paged = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery { PageSize = 50 });

        Assert.DoesNotContain(paged.Items, t => t.Id == taskId);

        var liveRows = await s.Db.WorkTasks.CountAsync(t => t.Id == taskId && !t.IsDeleted);
        Assert.Equal(0, liveRows);
    }

    [Fact]
    public async Task ProcessOverdueTasks_MarksOverdueAndAuditsAsSystem()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id, dueDate: DateTime.UtcNow.AddDays(-1));

        await s.Tasks.ProcessOverdueTasksAsync();

        var task = await s.Db.WorkTasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Equal(WorkTaskStatus.Overdue, task.Status);

        var overdueAudit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "WorkTask" && a.EntityId == taskId.ToString() && a.Action == "Task.Overdue");
        Assert.Equal(Guid.Empty, overdueAudit.UserId);
    }

    [Fact]
    public async Task ProcessOverdueTasks_SkipsCompletedTasks()
    {
        await using var s = _fixture.CreateScope();
        var taskId = await CreateTaskAsync(s, _fixture.OperatorA.Id, dueDate: DateTime.UtcNow.AddDays(-1));

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await s.Tasks.CompleteAsync(new CompleteWorkTaskCommand(taskId, "Закрыли вовремя"));

        await s.Tasks.ProcessOverdueTasksAsync();

        var task = await s.Db.WorkTasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
    }

    private async Task<Guid> CreateTaskAsync(
        TestScope s,
        string assignedToUserId,
        string? createdBy = null,
        DateTime? dueDate = null)
    {
        if (createdBy != null)
            s.User.CurrentUserId = Guid.Parse(createdBy);
        else
            s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var dto = await s.Tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: "Тестовая задача " + Guid.NewGuid().ToString("N")[..8],
            Type: WorkTaskType.HKReview,
            Priority: WorkTaskPriority.Normal,
            AssignedToUserId: assignedToUserId,
            BranchId: _fixture.BranchA,
            DueDateUtc: dueDate));
        return dto.Id;
    }
}
