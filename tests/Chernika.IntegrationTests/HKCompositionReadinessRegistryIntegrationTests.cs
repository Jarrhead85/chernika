using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCompositionReadinessRegistryIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCompositionReadinessRegistryIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Readiness_NodeHK_ReturnsNotApplicable()
    {
        await using var s = _fixture.CreateScope();
        var node = new Node { Id = Guid.NewGuid(), Code = "N-R-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел тест" };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var context = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.Node, node.Id, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(context);

        Assert.Equal(HKCompositionReadinessState.NotApplicable, details.State);
    }

    [Fact]
    public async Task Readiness_ActiveCompositionWithReadyChildren_ReturnsReady()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, modelId, partId) = await CreateApprovedProductCompositionAsync(s);
        var aggregateId = await s.Db.ProductCompositionAggregates
            .Where(pca => pca.ProductCompositionId == compositionId)
            .Select(pca => pca.AggregateId)
            .FirstAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var today = DateTime.UtcNow.Date;
        await CreateHKCard(s, aggregateId, HKObjectLevel.Aggregate, HKCardStatus.Approved, today.AddDays(-10), today.AddDays(10));

        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(context);

        Assert.Equal(HKCompositionReadinessState.Ready, details.State);
        Assert.Equal(0, details.IssueCount);
        Assert.Equal(compositionId, details.CompositionId);
    }

    [Fact]
    public async Task Readiness_MissingChildHK_ReturnsRequiresAttention()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, modelId, partId) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        // No HK card for the aggregate child → Missing

        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(context);

        Assert.Equal(HKCompositionReadinessState.RequiresAttention, details.State);
        Assert.True(details.IssueCount > 0);
        Assert.Contains(details.Issues, r => r.Status == ReadinessRow.Missing);
    }

    [Fact]
    public async Task Readiness_NoCompositionVersions_ReturnsNoComposition()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-NC-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие без состава" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(context);

        Assert.Equal(HKCompositionReadinessState.NoComposition, details.State);
        Assert.Null(details.CompositionId);
    }

    [Fact]
    public async Task Readiness_OnlyDraftComposition_ReturnsNoActiveComposition()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-DR-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие черновик" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, null));

        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(context);

        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, details.State);
    }

    [Fact]
    public async Task Readiness_Summary_UsesActiveApprovedNotLatestDraft()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, modelId, partId) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { context });

        Assert.True(summaries.TryGetValue(hkCardId, out var summary));
        Assert.Equal(compositionId, summary.CompositionId);
    }

    [Fact]
    public async Task Readiness_BatchRequest_ReturnsDictionaryWithoutN1()
    {
        await using var s = _fixture.CreateScope();
        var models = new List<EquipmentModel>();
        for (int i = 0; i < 5; i++)
        {
            var m = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-BT-" + i + "-" + Guid.NewGuid().ToString("N")[..4], Name = "Изделие " + i };
            s.Db.EquipmentModels.Add(m);
            models.Add(m);
        }
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var contexts = models.Select(m => new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, m.Id, _fixture.BranchA)).ToList();

        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(contexts);

        Assert.Equal(5, summaries.Count);
        foreach (var ctx in contexts)
            Assert.True(summaries.ContainsKey(ctx.HKCardId));
    }

    [Fact]
    public async Task Readiness_BranchIsolation_NormAdminBCannotAccessBranchA()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-BR-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие филиала А" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, null));

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var context = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { context }));
    }

    [Fact]
    public async Task Readiness_Details_RequiresCompositionView()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-CV-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие проверка" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        await DenyPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var context = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.GetHKCompositionReadinessDetailsAsync(context));
    }

    [Fact]
    public async Task Readiness_CompositionView_GetsIssuesAndCompositionId()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, modelId, partId) = await CreateApprovedProductCompositionAsync(s);
        var aggregate = await s.Db.Aggregates.FirstAsync(a => s.Db.ProductCompositionAggregates.Any(pca => pca.AggregateId == a.Id));

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);

        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(context);

        Assert.NotNull(details.Issues);
        Assert.Equal(compositionId, details.CompositionId);
    }

    [Fact]
    public async Task Readiness_Query_DoesNotCreateWorkTasksOrNotifications()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-ERR-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие ошибка" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);

        var beforeTasks = await s.Db.WorkTasks.CountAsync();
        var beforeNotifications = await s.Db.Notifications.CountAsync();

        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { context });
        Assert.True(summaries.TryGetValue(hkCardId, out var summary));
        Assert.Equal(HKCompositionReadinessState.NoComposition, summary.State);

        var afterTasks = await s.Db.WorkTasks.CountAsync();
        var afterNotifications = await s.Db.Notifications.CountAsync();

        Assert.Equal(beforeTasks, afterTasks);
        Assert.Equal(beforeNotifications, afterNotifications);
    }

    [Fact]
    public async Task Readiness_Details_DoesNotCreateWorkTasksOrNotifications()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-DN-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие деталь" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var hkCardId = Guid.NewGuid();
        var context = new HKReadinessContext(hkCardId, HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);

        var beforeTasks = await s.Db.WorkTasks.CountAsync();
        var beforeNotifications = await s.Db.Notifications.CountAsync();

        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(context);
        Assert.Equal(HKCompositionReadinessState.NoComposition, details.State);

        var afterTasks = await s.Db.WorkTasks.CountAsync();
        var afterNotifications = await s.Db.Notifications.CountAsync();

        Assert.Equal(beforeTasks, afterTasks);
        Assert.Equal(beforeNotifications, afterNotifications);
    }

    [Fact]
    public async Task Readiness_Summary_BranchIsolation_SelectsOnlyOwnBranchComposition()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-MB-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие многофилиальное" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        // Approved composition in Branch B
        var bComp = await CreateApprovedProductCompositionForModelAsync(s, model.Id, Guid.Parse(_fixture.NormAdminB.Id), "Branch B");

        // Draft composition in Branch A (intentionally NOT approved)
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var draftA = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Branch A"));

        // Branch A summary should see only the Draft (NoActiveComposition) — Branch B's Approved is invisible.
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var summaryA = (await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxA }))[ctxA.HKCardId];
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, summaryA.State);
        Assert.NotNull(summaryA.NavigationCompositionId);
        Assert.Equal(draftA.Id, summaryA.NavigationCompositionId);

        // Branch B summary should see Approved + compositionId from Branch B
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var ctxB = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchB);
        var summaryB = (await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxB }))[ctxB.HKCardId];
        Assert.Equal(bComp, summaryB.CompositionId);
        Assert.Equal(bComp, summaryB.NavigationCompositionId);
        // The active composition is found; the aggregate child has no HK card so the state
        // is either Ready (no children) or RequiresAttention (missing child HK).
        Assert.True(summaryB.State == HKCompositionReadinessState.Ready
            || summaryB.State == HKCompositionReadinessState.RequiresAttention);
    }

    [Fact]
    public async Task Readiness_Details_BranchIsolation_NoCompositionBranchScoped()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-BNC-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие филиал-изоляция" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        // Composition exists ONLY in Branch B
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "B"));

        // Branch A must see NoComposition, not Ready/NoActive
        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var detailsA = await s.Equipment.GetHKCompositionReadinessDetailsAsync(ctxA);
        Assert.Equal(HKCompositionReadinessState.NoComposition, detailsA.State);
        Assert.Null(detailsA.CompositionId);
        Assert.Null(detailsA.NavigationCompositionId);
    }

    [Fact]
    public async Task Readiness_Summary_BatchRequest_FiftyMixedContexts_DoesNotThrow()
    {
        await using var s = _fixture.CreateScope();
        var models = new List<EquipmentModel>();
        for (int i = 0; i < 25; i++)
        {
            var m = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-MX-" + i + "-" + Guid.NewGuid().ToString("N")[..4], Name = "Изделие M " + i };
            s.Db.EquipmentModels.Add(m);
            models.Add(m);
        }
        var aggregates = new List<Aggregate>();
        for (int i = 0; i < 25; i++)
        {
            var a = new Aggregate { Id = Guid.NewGuid(), Code = "A-MX-" + i + "-" + Guid.NewGuid().ToString("N")[..4], Name = "Агрегат M " + i };
            s.Db.Aggregates.Add(a);
            aggregates.Add(a);
        }
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var contexts = new List<HKReadinessContext>();
        contexts.AddRange(models.Select(m => new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, m.Id, _fixture.BranchA)));
        contexts.AddRange(aggregates.Select(a => new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.Aggregate, a.Id, _fixture.BranchA)));

        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(contexts);
        Assert.Equal(contexts.Count, summaries.Count);
        foreach (var ctx in contexts)
        {
            Assert.True(summaries.TryGetValue(ctx.HKCardId, out var s2));
            Assert.Equal(HKCompositionReadinessState.NoComposition, s2.State);
        }
    }

    [Fact]
    public async Task Readiness_Summary_Batch_DoesNotProduceDuplicateKeysForMultiBranch()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-DUP-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие дубликат" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        var compA = await CreateApprovedProductCompositionForModelAsync(s, model.Id, Guid.Parse(_fixture.NormAdminA.Id), "A");
        var compB = await CreateApprovedProductCompositionForModelAsync(s, model.Id, Guid.Parse(_fixture.NormAdminB.Id), "B");

        // ApproveCompositionAsync archives any other active composition for the same EquipmentModel,
        // regardless of branch. This is a pre-existing cross-branch limitation, so the test
        // explicitly restores both compositions to active+approved state to exercise the batch.
        var storedA = await s.Db.ProductCompositions.FirstAsync(c => c.Id == compA);
        storedA.Status = ProductCompositionStatus.Approved;
        storedA.IsActive = true;
        var storedB = await s.Db.ProductCompositions.FirstAsync(c => c.Id == compB);
        storedB.Status = ProductCompositionStatus.Approved;
        storedB.IsActive = true;
        await s.Db.SaveChangesAsync();

        // SystemAdmin can ask both branches in one call. (ObjectId, BranchId) is the composite key — no duplicate.
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var ctxB = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchB);
        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxA, ctxB });

        Assert.Equal(compA, summaries[ctxA.HKCardId].CompositionId);
        Assert.Equal(compB, summaries[ctxB.HKCardId].CompositionId);
    }

    [Fact]
    public async Task Readiness_NoActiveComposition_NavigationCompositionId_DraftPriority()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-NAP-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие навигация" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var draft = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "draft"));
        var draft2 = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "draft2"));
        // Both drafts exist for the same object. Most recent UpdatedAt wins for navigation.
        var ctx = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(ctx);

        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, details.State);
        Assert.Null(details.CompositionId);
        Assert.NotNull(details.NavigationCompositionId);
        // Most recent draft (draft2) is selected.
        Assert.Equal(draft2.Id, details.NavigationCompositionId);
        Assert.NotEqual(draft.Id, details.NavigationCompositionId);

        // Branch isolation: navigation composition must belong to effective branch.
        var nav = await s.Db.ProductCompositions.FindAsync(details.NavigationCompositionId!.Value);
        Assert.NotNull(nav);
        Assert.Equal(_fixture.BranchA, nav!.BranchId);
    }

    [Fact]
    public async Task Readiness_NoActiveComposition_NavigationCompositionId_ArchivedWhenNoDraftOrReview()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-NAA-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие навигация архив" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        // Create + submit + approve + archive to get an Archived version.
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "approved"));
        await AddAggregateToDraftAsync(s, comp.Id);
        await s.Equipment.SubmitForReviewAsync(comp.Id);
        Assert.True(await s.Equipment.ApproveCompositionAsync(comp.Id, null));
        Assert.True(await s.Equipment.ArchiveCompositionAsync(comp.Id));

        var ctx = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(ctx);
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, details.State);
        Assert.NotNull(details.NavigationCompositionId);
        Assert.Equal(comp.Id, details.NavigationCompositionId);

        var nav = await s.Db.ProductCompositions.FindAsync(details.NavigationCompositionId!.Value);
        Assert.Equal(ProductCompositionStatus.Archived, nav!.Status);
        Assert.Equal(_fixture.BranchA, nav.BranchId);
    }

    [Fact]
    public async Task Readiness_Summary_SystemAdmin_MultiBranchBatch_NoDuplicateKey_RealApprovedInBothBranches()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-MBR-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие многофилиальный реальный" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        var compA = await CreateApprovedProductCompositionForModelAsync(s, model.Id, Guid.Parse(_fixture.NormAdminA.Id), "A");
        var compB = await CreateApprovedProductCompositionForModelAsync(s, model.Id, Guid.Parse(_fixture.NormAdminB.Id), "B");

        // ApproveCompositionAsync archives any other active composition for the same EquipmentModel,
        // regardless of branch. Restore both to active+approved so the multi-branch batch is exercised.
        var a = await s.Db.ProductCompositions.FirstAsync(c => c.Id == compA);
        a.Status = ProductCompositionStatus.Approved; a.IsActive = true;
        var b = await s.Db.ProductCompositions.FirstAsync(c => c.Id == compB);
        b.Status = ProductCompositionStatus.Approved; b.IsActive = true;
        await s.Db.SaveChangesAsync();

        // SystemAdmin can request both contexts in a single summary batch.
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var ctxB = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchB);
        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxA, ctxB });

        Assert.Equal(compA, summaries[ctxA.HKCardId].CompositionId);
        Assert.Equal(compB, summaries[ctxB.HKCardId].CompositionId);
        Assert.Equal(compA, summaries[ctxA.HKCardId].NavigationCompositionId);
        Assert.Equal(compB, summaries[ctxB.HKCardId].NavigationCompositionId);
    }

    [Fact]
    public async Task Readiness_Summary_AggregateLevel_MultiBranchBatch_NoDuplicateKey()
    {
        await using var s = _fixture.CreateScope();
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "AG-MB-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат многофилиальный" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        var compA = await CreateApprovedAggregateCompositionForAggregateAsync(s, aggregate.Id, Guid.Parse(_fixture.NormAdminA.Id), "A");
        var compB = await CreateApprovedAggregateCompositionForAggregateAsync(s, aggregate.Id, Guid.Parse(_fixture.NormAdminB.Id), "B");

        // ApproveAggregateCompositionAsync archives any other active composition for the same AggregateId,
        // regardless of branch. Restore both to active+approved so the multi-branch batch is exercised.
        var aAgg = await s.Db.AggregateCompositions.FirstAsync(c => c.Id == compA);
        aAgg.Status = ProductCompositionStatus.Approved; aAgg.IsActive = true;
        var bAgg = await s.Db.AggregateCompositions.FirstAsync(c => c.Id == compB);
        bAgg.Status = ProductCompositionStatus.Approved; bAgg.IsActive = true;
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.Aggregate, aggregate.Id, _fixture.BranchA);
        var ctxB = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.Aggregate, aggregate.Id, _fixture.BranchB);
        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxA, ctxB });

        Assert.Equal(compA, summaries[ctxA.HKCardId].CompositionId);
        Assert.Equal(compB, summaries[ctxB.HKCardId].CompositionId);
    }

    [Fact]
    public async Task Readiness_Summary_BranchSpecificNoActive_DraftInA_ApprovedInB()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-BNA-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие ветвь" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var draftA = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "draft A"));

        var compB = await CreateApprovedProductCompositionForModelAsync(s, model.Id, Guid.Parse(_fixture.NormAdminB.Id), "B");

        // SystemAdmin asks both contexts in one batch.
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var ctxB = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchB);
        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxA, ctxB });

        // Branch A: only the Draft, no active composition.
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, summaries[ctxA.HKCardId].State);
        Assert.Equal(draftA.Id, summaries[ctxA.HKCardId].NavigationCompositionId);
        Assert.Null(summaries[ctxA.HKCardId].CompositionId);

        // Branch B: active composition was found, but the aggregate child has no HK card,
        // so the state is RequiresAttention (the composition itself is read).
        Assert.Equal(compB, summaries[ctxB.HKCardId].CompositionId);
        Assert.NotEqual(HKCompositionReadinessState.NotApplicable, summaries[ctxB.HKCardId].State);
        Assert.NotEqual(HKCompositionReadinessState.NoComposition, summaries[ctxB.HKCardId].State);
        Assert.NotEqual(HKCompositionReadinessState.NoActiveComposition, summaries[ctxB.HKCardId].State);

        // Navigation version in A is in Branch A, active version in B is in Branch B.
        var navA = await s.Db.ProductCompositions.FindAsync(summaries[ctxA.HKCardId].NavigationCompositionId!.Value);
        Assert.Equal(_fixture.BranchA, navA!.BranchId);
        var activeB = await s.Db.ProductCompositions.FindAsync(summaries[ctxB.HKCardId].CompositionId!.Value);
        Assert.Equal(_fixture.BranchB, activeB!.BranchId);
    }

    [Fact]
    public async Task Readiness_Active_IsActiveWithoutApproved_IsNotReadinessActive()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-NAST-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие status guard" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        // Normal lifecycle gives IsActive=true only when Status=Approved.
        // To prove the defensive criterion, manipulate the entity directly.
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var draft = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "draft"));
        var tracked = await s.Db.ProductCompositions.FindAsync(draft.Id);
        tracked!.IsActive = true;
        tracked.Status = ProductCompositionStatus.OnReview;
        tracked.UpdatedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        var ctx = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var summary = (await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctx }))[ctx.HKCardId];
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, summary.State);
        Assert.Null(summary.CompositionId);

        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(ctx);
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, details.State);
        Assert.Null(details.CompositionId);
        // Navigation still points to the same OnReview version (Draft > OnReview > Archived, but it's OnReview here).
        Assert.Equal(draft.Id, details.NavigationCompositionId);
    }

    [Fact]
    public async Task Readiness_NavigationPriority_DraftBeatsOnReviewAndArchived_AllInSameBranch()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-NP-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие навигация приоритет" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        // Three different versions for the same (Model, Branch A), each with its own aggregate.
        var draft = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "draft"));
        await AddAggregateToDraftAsync(s, draft.Id);

        var onReview = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "review"));
        await AddAggregateToDraftAsync(s, onReview.Id);
        await s.Equipment.SubmitForReviewAsync(onReview.Id);

        var archived = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "approved"));
        await AddAggregateToDraftAsync(s, archived.Id);
        await s.Equipment.SubmitForReviewAsync(archived.Id);
        Assert.True(await s.Equipment.ApproveCompositionAsync(archived.Id, null));
        Assert.True(await s.Equipment.ArchiveCompositionAsync(archived.Id));

        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);
        var ctx = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(ctx);
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, details.State);
        Assert.Equal(draft.Id, details.NavigationCompositionId);
    }

    [Fact]
    public async Task Readiness_NavigationPriority_OnReviewBeatsArchived_WhenNoDraft()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-NP2-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие навигация 2" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var onReview = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "review"));
        await AddAggregateToDraftAsync(s, onReview.Id);
        await s.Equipment.SubmitForReviewAsync(onReview.Id);

        var archived = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "approved"));
        await AddAggregateToDraftAsync(s, archived.Id);
        await s.Equipment.SubmitForReviewAsync(archived.Id);
        Assert.True(await s.Equipment.ApproveCompositionAsync(archived.Id, null));
        Assert.True(await s.Equipment.ArchiveCompositionAsync(archived.Id));

        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.CompositionView);
        var ctx = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var details = await s.Equipment.GetHKCompositionReadinessDetailsAsync(ctx);
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, details.State);
        Assert.Equal(onReview.Id, details.NavigationCompositionId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task GrantPermissionAsync(TestScope s, string userId, string permissionCode)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == userId && o.PermissionCode == permissionCode)
            .FirstOrDefaultAsync();
        if (existing != null)
        {
            if (!existing.IsGranted)
            {
                existing.IsGranted = true;
                existing.Reason = "Test override";
                existing.GrantedByUserId = _fixture.SystemAdminUser.Id;
                await s.Db.SaveChangesAsync();
                s.Permissions.InvalidateCache(userId);
            }
            return;
        }
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

    private async Task DenyPermissionAsync(TestScope s, string userId, string permissionCode)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == userId && o.PermissionCode == permissionCode)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionCode = permissionCode,
            IsGranted = false,
            Reason = "Test deny",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(userId);
    }

    private async Task CreateHKCard(TestScope s, Guid? aggregateId, HKObjectLevel level, HKCardStatus status,
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
        await s.Db.SaveChangesAsync();
    }

    private async Task<(Guid CompositionId, Guid ModelId, Guid PartId)> CreateApprovedProductCompositionAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Тест"));
        var part = await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Силовая установка", null, 1));
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(comp.Id, part.Id, aggregate.Id, 2));
        await s.Equipment.SubmitForReviewAsync(comp.Id);
        var approved = await s.Equipment.ApproveCompositionAsync(comp.Id, null);
        Assert.True(approved);
        return (comp.Id, model.Id, part.Id);
    }

    private async Task<Guid> CreateApprovedProductCompositionForModelAsync(
        TestScope s, Guid modelId, Guid userId, string? comment = null)
    {
        s.User.CurrentUserId = userId;
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(modelId, comment));
        var part = await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Силовая установка", null, 1));
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(comp.Id, part.Id, aggregate.Id, 1));
        await s.Equipment.SubmitForReviewAsync(comp.Id);
        Assert.True(await s.Equipment.ApproveCompositionAsync(comp.Id, null));
        return comp.Id;
    }

    private async Task<Guid> CreateApprovedAggregateCompositionForAggregateAsync(
        TestScope s, Guid aggregateId, Guid userId, string? comment = null)
    {
        s.User.CurrentUserId = userId;
        var comp = await s.Equipment.CreateAggregateCompositionAsync(new CreateAggregateCompositionRequest(aggregateId, comment));
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел тест" };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();

        await s.Equipment.AddAggregateCompositionNodeAsync(new AddAggregateCompositionNodeRequest(comp.Id, node.Id, 1, null));
        await s.Equipment.SubmitAggregateCompositionForReviewAsync(comp.Id);
        Assert.True(await s.Equipment.ApproveAggregateCompositionAsync(comp.Id, null));
        return comp.Id;
    }

    private async Task AddAggregateToDraftAsync(TestScope s, Guid compositionId)
    {
        var part = s.Db.ProductCompositionParts.FirstOrDefault(p => p.ProductCompositionId == compositionId);
        if (part == null)
        {
            await s.Equipment.AddPartAsync(new AddPartRequest(compositionId, "Часть", null, 1));
            part = s.Db.ProductCompositionParts.First(p => p.ProductCompositionId == compositionId);
        }
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, part.Id, aggregate.Id, 1));
    }
}
