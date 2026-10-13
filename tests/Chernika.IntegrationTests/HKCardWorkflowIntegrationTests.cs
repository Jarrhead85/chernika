using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardWorkflowIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardWorkflowIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SubmitDetails_CreatesAddressedReviewTask_WithRealAuthor()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, version);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.OnReview, card.Status);

        // Задача адресная: назначенный согласующий, а не роль.
        var task = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKDetailsReview);
        Assert.Equal(WorkTaskStatus.Open, task.Status);
        Assert.Equal(_fixture.NormAdminA2.Id, task.AssignedToUserId);
        Assert.Null(task.AssignedRole);
        Assert.Equal(_fixture.NormAdminA.Id, task.CreatedByUserId);
        Assert.Equal(_fixture.BranchA, task.BranchId);
        Assert.Equal(card.Code, task.EntityCodeSnapshot);
        Assert.Contains(card.Code, task.Title);

        var createdAudit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "WorkTask" && a.EntityId == task.Id.ToString() && a.Action == "Task.Created");
        Assert.Equal(Guid.Parse(_fixture.NormAdminA.Id), createdAudit.UserId);

        var notification = await s.Db.Notifications.AsNoTracking()
            .SingleAsync(n => n.WorkTaskId == task.Id);
        Assert.Equal(NotificationType.TaskAssigned, notification.Type);

        var submitAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "HKCard" && a.EntityId == cardId.ToString()
                && a.Action == "DetailsSubmitted");
        Assert.True(submitAudit);
    }

    [Fact]
    public async Task SubmitDetails_InactiveApprover_IsRefusedAndCardStaysDraft()
    {
        await using var s = _fixture.CreateScope();

        var branchC = Guid.NewGuid();
        s.Db.Branches.Add(new Branch { Id = branchC, Name = "Филиал В", Code = "C" });
        await s.Db.SaveChangesAsync();

        var approver = await CreateUserAsync(
            s, "normadmin_in" + HKApprovalScenario.Suffix(), nameof(UserRole.NormAdmin), branchC);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        var user = await s.Db.Users.SingleAsync(u => u.Id == approver.Id);
        user.IsActive = false;
        await s.Db.SaveChangesAsync();

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Approvals.SubmitDetailsAsync(cardId, approver.Id, version));
        Assert.Contains("отключён", ex.Message, StringComparison.OrdinalIgnoreCase);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Draft, card.Status);

        var stage = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.Draft, stage.State);
        Assert.Null(stage.AssignedApproverUserId);
    }

    [Fact]
    public async Task ReturnDetails_CompletesReviewAndCreatesRevisionForAuthor()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.ReturnDetailsAsync(s, cardId);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.RevisionRequired, card.Status);

        var reviewTask = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKDetailsReview);
        Assert.Equal(WorkTaskStatus.Completed, reviewTask.Status);
        Assert.Equal(_fixture.NormAdminA2.Id, reviewTask.CompletedByUserId);

        var revision = await s.Db.WorkTasks.AsNoTracking().SingleAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId && t.Type == WorkTaskType.HKRevision);
        Assert.Equal(WorkTaskStatus.Open, revision.Status);
        // Задача адресована первоначальному автору, а создана тем, кто вернул.
        Assert.Equal(card.AuthorId!.Value.ToString(), revision.AssignedToUserId);
        Assert.Equal(_fixture.NormAdminA2.Id, revision.CreatedByUserId);

        // Данные карты при возврате сохраняются.
        Assert.Equal(1, await s.Db.HKCardItems.AsNoTracking().CountAsync(i => i.HKCardId == cardId));
    }

    [Fact]
    public async Task Resubmit_ClosesRevisionTask_AndKeepsSingleOpenReview()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.ReturnDetailsAsync(s, cardId);

        // Повторная отправка: автор возвращает карту в работу.
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.OnReview, card.Status);

        // Повторная отправка не плодит открытые задачи рассмотрения.
        var openReviews = await s.Db.WorkTasks.AsNoTracking()
            .CountAsync(t => t.EntityType == "HKCard" && t.EntityId == cardId
                             && t.Type == WorkTaskType.HKDetailsReview
                             && (t.Status == WorkTaskStatus.Open
                                 || t.Status == WorkTaskStatus.InProgress
                                 || t.Status == WorkTaskStatus.Overdue));
        Assert.Equal(1, openReviews);
    }

    [Fact]
    public async Task RepeatedReturn_AfterResubmit_KeepsStageAndTaskHistoryConsistent()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.ReturnDetailsAsync(s, cardId);

        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.ReturnDetailsAsync(s, cardId);

        var stage = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.RevisionRequired, stage.State);

        // Назначенный согласующий сохраняется при повторных возвратах.
        Assert.Equal(_fixture.NormAdminA2.Id, stage.AssignedApproverUserId);

        // Открытая задача доработки автору не дублируется.
        var openRevisions = await s.Db.WorkTasks.AsNoTracking()
            .CountAsync(t => !t.IsDeleted && t.EntityType == "HKCard" && t.EntityId == cardId
                             && t.Type == WorkTaskType.HKRevision
                             && (t.Status == WorkTaskStatus.Open
                                 || t.Status == WorkTaskStatus.InProgress
                                 || t.Status == WorkTaskStatus.Overdue));
        Assert.Equal(1, openRevisions);

        // Данные не теряются ни при одном из возвратов.
        Assert.Equal(1, await s.Db.HKCardItems.AsNoTracking().CountAsync(i => i.HKCardId == cardId));
    }

    [Fact]
    public async Task FinalApproval_Closed_AndNoIkRecalculationTasks()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await HKApprovalScenario.CreateCardWithApprovedDetailsAsync(
            s, _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id);

        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        // Окончательное принятие закрыто до появления хранилища принятой структуры.
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Approvals.ApproveCompositionAsync(cardId, null, version));
        Assert.Contains("хранилище", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Общий статус Approved не выставляется.
        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.NotEqual(HKCardStatus.Approved, card.Status);

        // Задачи пересчёта ИК согласованием не создаются.
        // Проверка ограничена ЭКЗЕМПЛЯРОМ этой карты: общая тестовая БД живёт
        // между тестами, и поиск по всем экземплярам ловил бы чужие задачи.
        var model = new EquipmentModel
        {
            Id = Guid.NewGuid(),
            Index = "И-" + HKApprovalScenario.Suffix(),
            Name = "Изделие тест"
        };
        var instance = new EquipmentInstance
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = model.Id,
            SerialNumber = "SN-" + HKApprovalScenario.Suffix(),
            Index = "И-1",
            Name = "Экземпляр тест"
        };
        s.Db.EquipmentModels.Add(model);
        s.Db.EquipmentInstances.Add(instance);
        await s.Db.SaveChangesAsync();

        s.Db.IndividualCards.Add(new IndividualCard
        {
            Id = Guid.NewGuid(),
            ObjectLevel = IndividualCardObjectLevel.EquipmentInstance,
            Status = IndividualCardStatus.Draft,
            RevisionNumber = 1,
            Code = "ИК-ЭКЗ-" + instance.SerialNumber + "-" + DateTime.UtcNow.Year
                   + "-" + HKApprovalScenario.Suffix(),
            EquipmentInstanceId = instance.Id,
            HKCardId = card.Id,
            BranchId = card.BranchId,
            CreatedByUserId = _fixture.SystemAdminUser.Id,
            Version = "1",
            TotalNorm = 1
        });
        await s.Db.SaveChangesAsync();

        Assert.False(await s.Db.WorkTasks.AsNoTracking().AnyAsync(x => !x.IsDeleted
            && x.EntityType == "EquipmentInstance" && x.EntityId == instance.Id));
        Assert.False(await s.Db.WorkTasks.AsNoTracking().AnyAsync(x => !x.IsDeleted
            && x.EntityType == "HKCard" && x.EntityId == card.Id
            && (x.Type == WorkTaskType.HKReview || x.Type == WorkTaskType.CompositionReadiness)));
    }

    [Fact]
    public async Task ApprovedDetails_FixedDecisionDate_AndCompositionBecomesSendable()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(s, cardId);

        var details = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.Approved, details.State);
        var approvedAt = details.ApprovedAtUtc;
        Assert.NotNull(approvedAt);

        // Дата принятия не равна срокам действия документа и не заменяет их.
        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Null(card.EffectiveDate);
        Assert.Null(card.ExpirationDate);

        // Возврат и повторное принятие не обнуляют дату первого решения.
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);
        await HKApprovalScenario.ReturnCompositionAsync(s, cardId);

        var detailsAfter = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(approvedAt, detailsAfter.ApprovedAtUtc);

        // После принятия реквизитов отправка состава допустима.
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);
        var composition = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition);
        Assert.Equal(HKApprovalStageState.OnReview, composition.State);
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

}
