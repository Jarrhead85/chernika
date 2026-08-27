using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CompositionA2IntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CompositionA2IntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Operator_WithoutEditDraft_CannotAddAggregate()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);

        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1)));
    }

    [Fact]
    public async Task Operator_WithPointAggregateEditDraft_CanEditAggregateOnlyOwnBranch()
    {
        await using var s = _fixture.CreateScope();
        await GrantPermissionAsync(s, _fixture.OperatorA.Id, PermissionCodes.CompositionAggregateEditDraft);

        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел тест" };
        s.Db.Aggregates.Add(aggregate);
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();

        // BranchA composition — OperatorA is from BranchA, can edit
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var compA = await s.Equipment.CreateAggregateCompositionAsync(new CreateAggregateCompositionRequest(aggregate.Id, null));
        Assert.Equal(_fixture.BranchA, compA.BranchId);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var addedA = await s.Equipment.AddAggregateCompositionNodeAsync(new AddAggregateCompositionNodeRequest(compA.Id, node.Id, 1, null));
        Assert.NotNull(addedA);

        // BranchB composition — OperatorA cannot edit due to branch
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var compB = await s.Equipment.CreateAggregateCompositionAsync(new CreateAggregateCompositionRequest(aggregate.Id, null));
        Assert.Equal(_fixture.BranchB, compB.BranchId);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.AddAggregateCompositionNodeAsync(new AddAggregateCompositionNodeRequest(compB.Id, node.Id, 1, null)));

        // Product composition in same branch — OperatorA still cannot edit (wrong level)
        var (productId, _, partId) = await CreateProductCompositionDraftAsync(s);
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var ag = new Aggregate { Id = Guid.NewGuid(), Code = "A2-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат 2" };
        s.Db.Aggregates.Add(ag);
        await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(productId, partId, ag.Id, 1)));
    }

    [Fact]
    public async Task DraftRows_CannotChange_AfterOnReview()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var pca = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 2));
        await s.Equipment.SubmitForReviewAsync(compositionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.AddPartAsync(new AddPartRequest(compositionId, "Новая часть", null, 2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.UpdateAggregateQuantityAsync(new UpdateProductCompositionAggregateRequest(pca.Id, 5, 0, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.RemoveAggregateAsync(pca.Id));
    }

    [Fact]
    public async Task AddAggregate_DuplicateRejected()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 2)));
    }

    [Fact]
    public async Task AddAggregate_QuantityZeroRejected()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 0)));
    }

    [Fact]
    public async Task Submit_EmptyCompositionRejected()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, null));
        // part exists but no aggregates → composition is empty
        await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Пустая часть", null, 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.SubmitForReviewAsync(comp.Id));
    }

    [Fact]
    public async Task Submit_CreatesGroupTask_ForAllNormAdminsInBranch()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        await s.Equipment.SubmitForReviewAsync(compositionId);

        var group = await s.Db.WorkTaskGroups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.EntityType == "ProductComposition" && g.EntityId == compositionId
                && g.TaskType == nameof(WorkTaskType.CompositionReview) && g.CompletedAt == null);
        Assert.NotNull(group);
        Assert.Equal(_fixture.BranchA, group.BranchId);

        var tasks = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => t.WorkTaskGroupId == group.Id && !t.IsDeleted)
            .ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, t => t.AssignedToUserId == _fixture.NormAdminA.Id);
        Assert.Contains(tasks, t => t.AssignedToUserId == _fixture.NormAdminA2.Id);
        Assert.DoesNotContain(tasks, t => t.AssignedToUserId == _fixture.NormAdminB.Id);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.CompositionReviewRequested
                && n.EntityType == "ProductComposition" && n.EntityId == compositionId)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task Return_ClosesGroupTask_And_CreatesAuthorTask()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        await s.Equipment.SubmitForReviewAsync(compositionId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await s.Equipment.ReturnToDraftAsync(compositionId, "Доработать");

        var group = await s.Db.WorkTaskGroups.AsNoTracking()
            .SingleAsync(g => g.EntityType == "ProductComposition" && g.EntityId == compositionId);
        Assert.NotNull(group.CompletedAt);
        Assert.Equal(_fixture.NormAdminA2.Id, group.CompletedByUserId);

        var groupTasks = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => t.WorkTaskGroupId == group.Id)
            .ToListAsync();
        Assert.All(groupTasks, t => Assert.Equal(WorkTaskStatus.Completed, t.Status));

        var authorTask = await s.Db.WorkTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.EntityType == "ProductComposition" && t.EntityId == compositionId
                && t.AssignedToUserId == _fixture.NormAdminA.Id && t.Type == WorkTaskType.CompositionReview
                && t.Status == WorkTaskStatus.Open && t.WorkTaskGroupId == null);
        Assert.NotNull(authorTask);
        Assert.Equal("Доработать состав", authorTask.Title);
    }

    [Fact]
    public async Task Approve_ArchivesPredecessor_And_ClosesGroup()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, _, _) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newId, error) = await s.Equipment.CreateProductCompositionVersionAsync(sourceId);
        Assert.True(success, error);
        Assert.NotNull(newId);

        await s.Equipment.SubmitForReviewAsync(newId.Value);
        var approved = await s.Equipment.ApproveCompositionAsync(newId.Value, null);
        Assert.True(approved);

        var source = await s.Db.ProductCompositions.AsNoTracking().SingleAsync(c => c.Id == sourceId);
        Assert.Equal(ProductCompositionStatus.Archived, source.Status);
        Assert.False(source.IsActive);

        var fresh = await s.Db.ProductCompositions.AsNoTracking().SingleAsync(c => c.Id == newId.Value);
        Assert.Equal(ProductCompositionStatus.Approved, fresh.Status);
        Assert.True(fresh.IsActive);

        var group = await s.Db.WorkTaskGroups.AsNoTracking()
            .SingleAsync(g => g.EntityType == "ProductComposition" && g.EntityId == newId.Value);
        Assert.NotNull(group.CompletedAt);
    }

    [Fact]
    public async Task Approve_InvalidPredecessor_Rollbacks()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, _, _) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newId, error) = await s.Equipment.CreateProductCompositionVersionAsync(sourceId);
        Assert.True(success, error);
        Assert.NotNull(newId);

        // Сделать предшественника недействительным
        var source = await s.Db.ProductCompositions.SingleAsync(c => c.Id == sourceId);
        source.Status = ProductCompositionStatus.Draft;
        source.IsActive = false;
        await s.Db.SaveChangesAsync();

        await s.Equipment.SubmitForReviewAsync(newId.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.ApproveCompositionAsync(newId.Value, null));

        var fresh = await s.Db.ProductCompositions.AsNoTracking().SingleAsync(c => c.Id == newId.Value);
        Assert.Equal(ProductCompositionStatus.OnReview, fresh.Status);
    }

    [Fact]
    public async Task Readiness_AggregateStates_Correct()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));

        var today = DateTime.UtcNow.Date;

        // Missing
        var rows = await s.Equipment.EvaluateReadinessAsync(1, compositionId);
        var row = Assert.Single(rows);
        Assert.Equal(ReadinessRow.Missing, row.Status);

        // Ready
        var readyHk = CreateHKCard(s, aggregate.Id, HKObjectLevel.Aggregate, HKCardStatus.Approved, today.AddDays(-10), today.AddDays(10));
        await s.Db.SaveChangesAsync();
        row = (await s.Equipment.EvaluateReadinessAsync(1, compositionId)).Single();
        Assert.Equal(ReadinessRow.Ready, row.Status);
        Assert.Equal(readyHk.Id, row.HkCardId);

        // Expired
        s.Db.HKCards.Remove(readyHk);
        s.Db.HKCards.Add(CreateHKCard(s, aggregate.Id, HKObjectLevel.Aggregate, HKCardStatus.Approved, today.AddDays(-10), today.AddDays(-1)));
        await s.Db.SaveChangesAsync();
        row = (await s.Equipment.EvaluateReadinessAsync(1, compositionId)).Single();
        Assert.Equal(ReadinessRow.Expired, row.Status);

        // FutureEffective
        s.Db.HKCards.RemoveRange(s.Db.HKCards.Where(h => h.AggregateId == aggregate.Id));
        s.Db.HKCards.Add(CreateHKCard(s, aggregate.Id, HKObjectLevel.Aggregate, HKCardStatus.Approved, today.AddDays(1), null));
        await s.Db.SaveChangesAsync();
        row = (await s.Equipment.EvaluateReadinessAsync(1, compositionId)).Single();
        Assert.Equal(ReadinessRow.FutureEffective, row.Status);

        // ArchivedOrClosed
        s.Db.HKCards.RemoveRange(s.Db.HKCards.Where(h => h.AggregateId == aggregate.Id));
        s.Db.HKCards.Add(CreateHKCard(s, aggregate.Id, HKObjectLevel.Aggregate, HKCardStatus.Archived, today.AddDays(-10), today.AddDays(10)));
        await s.Db.SaveChangesAsync();
        row = (await s.Equipment.EvaluateReadinessAsync(1, compositionId)).Single();
        Assert.Equal(ReadinessRow.ArchivedOrClosed, row.Status);
    }

    [Fact]
    public async Task Approve_WithReadinessProblem_CreatesReadinessTask()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        await s.Equipment.SubmitForReviewAsync(compositionId);

        var approved = await s.Equipment.ApproveCompositionAsync(compositionId, null);
        Assert.True(approved);

        var group = await s.Db.WorkTaskGroups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.EntityType == "Aggregate" && g.EntityId == aggregate.Id
                && g.TaskType == nameof(WorkTaskType.CompositionReadiness) && g.CompletedAt == null);
        Assert.NotNull(group);
    }

    [Fact]
    public async Task Lifecycle_AuditActions_Registered()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        await s.Equipment.SubmitForReviewAsync(compositionId);

        Assert.True(await s.Db.AuditLogs.AnyAsync(a => a.EntityType == "ProductComposition" && a.EntityId == compositionId.ToString()
            && a.Action == "ProductComposition.Submitted"));

        await s.Equipment.ReturnToDraftAsync(compositionId, "Нужна доработка");
        Assert.True(await s.Db.AuditLogs.AnyAsync(a => a.EntityType == "ProductComposition" && a.EntityId == compositionId.ToString()
            && a.Action == "ProductComposition.ReturnedToDraft"));

        await s.Equipment.SubmitForReviewAsync(compositionId);
        await s.Equipment.ApproveCompositionAsync(compositionId, null);
        Assert.True(await s.Db.AuditLogs.AnyAsync(a => a.EntityType == "ProductComposition" && a.EntityId == compositionId.ToString()
            && a.Action == "ProductComposition.Approved"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task GrantPermissionAsync(TestScope s, string userId, string permissionCode)
    {
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionCode = permissionCode,
            IsGranted = true,
            Reason = "Test override",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(userId);
    }

    private HKCard CreateHKCard(TestScope s, Guid? aggregateId, HKObjectLevel level, HKCardStatus status,
        DateTime? effectiveDate, DateTime? expirationDate)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Guid.NewGuid().ToString("N")[..6],
            Version = "v" + DateTime.UtcNow.ToString("MMyy"),
            ObjectLevel = level,
            AggregateId = aggregateId,
            Status = status,
            EffectiveDate = effectiveDate,
            ExpirationDate = expirationDate,
            BranchId = _fixture.BranchA
        };
        s.Db.HKCards.Add(hk);
        return hk;
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

    private async Task<(Guid SourceId, Guid ModelId, Guid PartId)> CreateApprovedProductCompositionAsync(TestScope s)
    {
        var (compositionId, modelId, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 2));
        await s.Equipment.SubmitForReviewAsync(compositionId);
        var approved = await s.Equipment.ApproveCompositionAsync(compositionId, null);
        Assert.True(approved);
        return (compositionId, modelId, partId);
    }
}
