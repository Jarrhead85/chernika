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
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var bComp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Branch B"));
        await s.Equipment.SubmitForReviewAsync(bComp.Id);
        Assert.True(await s.Equipment.ApproveCompositionAsync(bComp.Id, null));

        // Draft composition in Branch A
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Branch A"));

        // Branch A summary should see only the Draft (NoActiveComposition) — Branch B's Approved is invisible.
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var summaryA = (await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxA }))[ctxA.HKCardId];
        Assert.Equal(HKCompositionReadinessState.NoActiveComposition, summaryA.State);
        Assert.NotNull(summaryA.NavigationCompositionId);
        Assert.Equal(summaryA.NavigationCompositionId, s.Db.ProductCompositions.Single(c => c.BranchId == _fixture.BranchA && c.EquipmentModelId == model.Id && c.Status == ProductCompositionStatus.Draft).Id);

        // Branch B summary should see Approved + compositionId from Branch B
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var ctxB = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchB);
        var summaryB = (await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxB }))[ctxB.HKCardId];
        Assert.Equal(HKCompositionReadinessState.Ready, summaryB.State);
        Assert.Equal(bComp.Id, summaryB.CompositionId);
        Assert.Equal(bComp.Id, summaryB.NavigationCompositionId);
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

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var compA = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "A"));
        await s.Equipment.SubmitForReviewAsync(compA.Id);
        Assert.True(await s.Equipment.ApproveCompositionAsync(compA.Id, null));

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var compB = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "B"));
        await s.Equipment.SubmitForReviewAsync(compB.Id);
        Assert.True(await s.Equipment.ApproveCompositionAsync(compB.Id, null));

        // SystemAdmin can ask both branches in one call. (ObjectId, BranchId) is the composite key — no duplicate.
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var ctxA = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchA);
        var ctxB = new HKReadinessContext(Guid.NewGuid(), HKObjectLevel.EquipmentModel, model.Id, _fixture.BranchB);
        var summaries = await s.Equipment.GetHKCompositionReadinessSummariesAsync(new[] { ctxA, ctxB });

        Assert.Equal(compA.Id, summaries[ctxA.HKCardId].CompositionId);
        Assert.Equal(compB.Id, summaries[ctxB.HKCardId].CompositionId);
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
}
