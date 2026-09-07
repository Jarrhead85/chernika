using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardDraftIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardDraftIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task GrantAsync(TestScope s, ApplicationUser user, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = code,
            IsGranted = true, Reason = "Test", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private async Task DenyAsync(TestScope s, ApplicationUser user, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = code,
            IsGranted = false, Reason = "Test deny", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// Fresh per-test user: scenarios that modify permission overrides must not
    /// leak state onto shared fixture users (the test database is shared).
    /// </summary>
    private static async Task<ApplicationUser> CreateUserAsync(
        TestScope s, string role, Guid branchId)
    {
        var login = role.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..8];
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
                "Создание тестового пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);
        return user;
    }

    private static DateTime TrimMs(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Millisecond, DateTimeKind.Utc);

    private async Task<Guid> CreateNodeAsync(TestScope s, string? code = null)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = code ?? "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    private async Task<Guid> CreateAggregateAsync(TestScope s)
    {
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Suffix(), Name = "Агрегат " + Suffix(), IsDeleted = false };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        return aggregate.Id;
    }

    private async Task<(Guid ModelId, Guid InstanceId)> CreateEquipmentAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Suffix(), Name = "Изделие " + Suffix(), IsDeleted = false };
        s.Db.EquipmentModels.Add(model);
        var instance = new EquipmentInstance { Id = Guid.NewGuid(), SerialNumber = "SN-" + Suffix(), Index = model.Index, Name = "Экземпляр", EquipmentModelId = model.Id, IsDeleted = false };
        s.Db.EquipmentInstances.Add(instance);
        await s.Db.SaveChangesAsync();
        return (model.Id, instance.Id);
    }

    private async Task<Guid> CreateComplexAsync(TestScope s)
    {
        var complex = new Complex { Id = Guid.NewGuid(), Code = "C-" + Suffix(), Name = "Комплекс " + Suffix(), IsDeleted = false };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();
        return complex.Id;
    }

    private async Task<HKCard> CreateHKAsync(
        TestScope s, IndividualCardObjectLevel level, Guid objectId, Guid branchId,
        HKCardStatus status = HKCardStatus.Approved, string? code = null, string? version = null)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = code ?? ("HK-" + level.ToString()[..3] + "-" + Suffix()),
            Version = version ?? ("v" + Suffix()[..4]),
            Status = status,
            ObjectLevel = level switch
            {
                IndividualCardObjectLevel.Complex => HKObjectLevel.Complex,
                IndividualCardObjectLevel.EquipmentModel => HKObjectLevel.EquipmentModel,
                IndividualCardObjectLevel.Aggregate => HKObjectLevel.Aggregate,
                IndividualCardObjectLevel.Node => HKObjectLevel.Node,
                _ => throw new ArgumentOutOfRangeException(nameof(level)),
            },
            BranchId = branchId,
            ApprovedDate = status == HKCardStatus.Approved ? DateTime.UtcNow : null,
        };
        switch (level)
        {
            case IndividualCardObjectLevel.Complex: hk.ComplexId = objectId; break;
            case IndividualCardObjectLevel.EquipmentModel: hk.EquipmentModelId = objectId; break;
            case IndividualCardObjectLevel.Aggregate: hk.AggregateId = objectId; break;
            case IndividualCardObjectLevel.Node: hk.NodeId = objectId; break;
        }
        s.Db.HKCards.Add(hk);
        await s.Db.SaveChangesAsync();
        return hk;
    }

    private async Task AddComponentAsync(TestScope s, HKCard parent, HKCard child, int sortOrder = 1)
    {
        s.Db.HKCardComponents.Add(new HKCardComponent
        {
            Id = Guid.NewGuid(),
            ParentHKCardId = parent.Id,
            ChildHKCardId = child.Id,
            SortOrder = sortOrder,
            AddedAt = DateTime.UtcNow,
            AddedByUserId = _fixture.SystemAdminUser.Id,
            ChildCode = child.Code,
            ChildVersion = child.Version,
            ChildApprovedAt = child.ApprovedDate,
        });
        await s.Db.SaveChangesAsync();
    }

    private async Task<Guid> CreateProductCompositionAsync(TestScope s, Guid modelId, params (Guid AggregateId, int Quantity)[] aggregates)
    {
        var pc = new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = modelId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.ProductCompositions.Add(pc);
        for (var i = 0; i < aggregates.Length; i++)
        {
            s.Db.ProductCompositionAggregates.Add(new ProductCompositionAggregate
            {
                Id = Guid.NewGuid(),
                ProductCompositionId = pc.Id,
                AggregateId = aggregates[i].AggregateId,
                Quantity = aggregates[i].Quantity,
                SortOrder = i + 1,
            });
        }
        await s.Db.SaveChangesAsync();
        return pc.Id;
    }

    private async Task<Guid> CreateAggregateCompositionAsync(TestScope s, Guid aggregateId, params (Guid NodeId, int Quantity)[] nodes)
    {
        var ac = new AggregateComposition
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.AggregateCompositions.Add(ac);
        for (var i = 0; i < nodes.Length; i++)
        {
            s.Db.AggregateCompositionNodes.Add(new AggregateCompositionNode
            {
                Id = Guid.NewGuid(),
                AggregateCompositionId = ac.Id,
                NodeId = nodes[i].NodeId,
                Quantity = nodes[i].Quantity,
                SortOrder = i + 1,
            });
        }
        await s.Db.SaveChangesAsync();
        return ac.Id;
    }

    private async Task<Guid> CreateComplexCompositionAsync(TestScope s, Guid complexId, params (Guid EquipmentModelId, int Quantity)[] items)
    {
        var cc = new ComplexComposition
        {
            Id = Guid.NewGuid(),
            ComplexId = complexId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.ComplexCompositions.Add(cc);
        for (var i = 0; i < items.Length; i++)
        {
            s.Db.ComplexCompositionItems.Add(new ComplexCompositionItem
            {
                Id = Guid.NewGuid(),
                ComplexCompositionId = cc.Id,
                EquipmentModelId = items[i].EquipmentModelId,
                Quantity = items[i].Quantity,
                SortOrder = i + 1,
            });
        }
        await s.Db.SaveChangesAsync();
        return cc.Id;
    }

    private async Task<int> CountAuditsAsync(TestScope s, Guid entityId, string action) =>
        await s.Db.AuditLogs.CountAsync(a =>
            a.EntityType == "IndividualCard" && a.EntityId == entityId.ToString() && a.Action == action);

    // ── Corrective D2: A1 auto-selected root object display ───────────────

    [Fact]
    public async Task AutoSelectedRoot_Complex_HKSourceHasObjectDisplay()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var complexId = await CreateComplexAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);

        var preflight = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.Complex, complexId));

        var source = Assert.Single(preflight.HKSources);
        Assert.Equal(root.Id, source.HKCardId);
        Assert.False(string.IsNullOrWhiteSpace(source.ObjectCode));
        Assert.False(string.IsNullOrWhiteSpace(source.ObjectName));
        var complex = await s.Db.Complexes.AsNoTracking().FirstAsync(c => c.Id == complexId);
        Assert.Equal(complex.Code, source.ObjectCode);
        Assert.Equal(complex.Name, source.ObjectName);
    }

    [Fact]
    public async Task AutoSelectedRoot_Izdelie_HKSourceHasObjectDisplay()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);

        var preflight = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        var source = Assert.Single(preflight.HKSources);
        Assert.False(string.IsNullOrWhiteSpace(source.ObjectCode));
        var model = await s.Db.EquipmentModels.AsNoTracking().FirstAsync(m => m.Id == modelId);
        Assert.Equal(model.Index, source.ObjectCode);
        Assert.Equal(model.Name, source.ObjectName);
    }

    [Fact]
    public async Task AutoSelectedRoot_Aggregate_HKSourceHasObjectDisplay()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);

        var preflight = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.Aggregate, aggregateId));

        var source = Assert.Single(preflight.HKSources);
        Assert.False(string.IsNullOrWhiteSpace(source.ObjectCode));
        Assert.False(string.IsNullOrWhiteSpace(source.ObjectName));
    }

    [Fact]
    public async Task AutoSelectedRoot_Node_HKSourceHasObjectDisplay()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var preflight = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.Node, nodeId));

        var source = Assert.Single(preflight.HKSources);
        Assert.False(string.IsNullOrWhiteSpace(source.ObjectCode));
        Assert.False(string.IsNullOrWhiteSpace(source.ObjectName));
    }

    // ── Corrective D2: A2 SystemConfig override does not unlock branches ──

    [Fact]
    public async Task SystemConfigOverride_DoesNotUnlockForeignBranch()
    {
        await using var s = Scope();
        // The actor is NOT in the SystemAdmin role but holds both SystemConfig
        // and CreateDraft overrides. HeadOfDepartment keeps the fresh user out
        // of the branch NormAdmin/Operator assignment pools, so granting
        // overrides cannot leak into other tests.
        var actor = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, actor);
        await GrantAsync(s, actor, PermissionCodes.IndividualCardCreateDraft);
        await GrantAsync(s, actor, PermissionCodes.SystemConfig);

        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchB);

        var preflight = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardPreflightRootState.Missing, preflight.RootState);
        Assert.Empty(preflight.RootCandidates);
        Assert.Null(preflight.BranchId);
        // Foreign branch data is not revealed.
        Assert.Empty(preflight.HKSources);
    }

    // ── Corrective D2: A3 multiple approved children of one object ────────

    [Fact]
    public async Task MultipleApprovedAggregateChildren_InconsistentGap_NoSourceChosen()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var v0826 = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA, code: "HK-DUP1-" + Suffix());
        var v0926 = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA, code: "HK-DUP2-" + Suffix());
        await AddComponentAsync(s, root, v0826);
        await AddComponentAsync(s, root, v0926);

        var preflight = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.InconsistentNormativeChain);
        Assert.DoesNotContain(preflight.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Aggregate);
        Assert.DoesNotContain(preflight.HKSources, h => h.HKCardId == v0826.Id || h.HKCardId == v0926.Id);
    }

    [Fact]
    public async Task MultipleApprovedNodeChildren_InconsistentGap_NoSourceChosen()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var v1 = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA, code: "HK-DUPN1-" + Suffix());
        var v2 = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA, code: "HK-DUPN2-" + Suffix());
        await AddComponentAsync(s, root, v1);
        await AddComponentAsync(s, root, v2);

        var preflight = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.InconsistentNormativeChain);
        Assert.DoesNotContain(preflight.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Node);
    }

    // ── D3: create ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDraft_SingleRoot_CreatesWithCodeVersionAndBranch()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardStatus.Draft, dto.Status);
        Assert.Equal(1, dto.RevisionNumber);
        Assert.StartsWith("ИК-УЗЛ-", dto.Code);
        Assert.Matches(@"^v\d{4}\.1$", dto.Version);
        Assert.Equal(root.BranchId, dto.BranchId);
        Assert.Equal(_fixture.SystemAdminUser.Id, dto.CreatedByUserId);
        Assert.True(TrimMs(dto.CreatedAt) <= TrimMs(DateTime.UtcNow));
        Assert.Equal("Узел", dto.ObjectLevelDisplay);
        Assert.False(dto.HasNormativeGaps);
    }

    [Fact]
    public async Task CreateDraft_MultipleRootsWithoutExplicit_Rejected_NoCardNoAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-A-" + Suffix());
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-B-" + Suffix());
        var before = await s.Db.IndividualCards.CountAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateDraftAsync(
                new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId)));

        Assert.Contains("выберите утверждённую ХК вручную", ex.Message);
        Assert.Equal(before, await s.Db.IndividualCards.CountAsync());
    }

    [Fact]
    public async Task CreateDraft_ExplicitValidRoot_Creates()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var first = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-A-" + Suffix());
        var second = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-B-" + Suffix());

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId, second.Id));

        Assert.Equal(IndividualCardStatus.Draft, dto.Status);
        Assert.Equal(second.BranchId, dto.BranchId);
        // Root source recorded.
        Assert.Contains(dto.HKSources, h => h.SourceHKCardId == second.Id);
        Assert.DoesNotContain(dto.HKSources, h => h.SourceHKCardId == first.Id);
    }

    [Fact]
    public async Task CreateDraft_MissingRoot_Rejected_NoCardNoAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var before = await s.Db.IndividualCards.CountAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateDraftAsync(
                new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId)));

        Assert.Contains("не найдена утверждённая ХК верхнего уровня", ex.Message);
        Assert.Equal(before, await s.Db.IndividualCards.CountAsync());
    }

    [Fact]
    public async Task CreateDraft_PartialChain_CreatesAndPersistsGapSnapshots()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        // No aggregate/node HK chain → partial Draft.

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.Equal(IndividualCardStatus.Draft, dto.Status);
        Assert.True(dto.HasNormativeGaps);
        Assert.Contains(dto.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingLinkedHKCard);
        // Composition snapshot still persisted for the resolved branch.
        Assert.Contains(dto.Compositions, c => c.TargetObjectId == modelId);
    }

    [Fact]
    public async Task CreateDraft_PersistsResolvedSnapshotsOnly_NoFakeMissing()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node1 = await CreateNodeAsync(s);
        var node2 = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node1, 1), (node2, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var node1HK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node1, _fixture.BranchA);
        await AddComponentAsync(s, root, node1HK);
        // node2 branch is unresolved.

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Aggregate, aggregateId));

        // Only the resolved branch snapshot is persisted.
        Assert.Single(dto.HKSources.Where(h => h.SourceHKCardId == node1HK.Id));
        Assert.Equal(2, dto.HKSources.Count); // root + resolved node
        Assert.Contains(dto.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingLinkedHKCard);
    }

    [Fact]
    public async Task CreateDraft_ParentSnapshotMapping_Correct()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.Equal(3, dto.HKSources.Count);
        var rootSnapshot = dto.HKSources.Single(h => h.SourceHKCardId == root.Id);
        var aggregateSnapshot = dto.HKSources.Single(h => h.SourceHKCardId == aggregateHK.Id);
        var nodeSnapshot = dto.HKSources.Single(h => h.SourceHKCardId == nodeHK.Id);
        Assert.Null(rootSnapshot.ParentHKSourceSnapshotId);
        Assert.Equal(rootSnapshot.Id, aggregateSnapshot.ParentHKSourceSnapshotId);
        Assert.Equal(aggregateSnapshot.Id, nodeSnapshot.ParentHKSourceSnapshotId);
    }

    [Fact]
    public async Task CreateDraft_SnapshotScalars_SurviveSourceRename()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, nodeHK);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Aggregate, aggregateId));

        var nodeEntity = await s.Db.Nodes.AsNoTracking().FirstAsync(n => n.Id == node);
        var originalNodeName = nodeEntity.Name;
        var originalHKCode = nodeHK.Code;
        var originalHKVersion = nodeHK.Version;

        // Rename the live sources afterwards.
        nodeHK.Code = "HK-RENAMED";
        nodeHK.Version = "vRENAMED";
        nodeEntity = await s.Db.Nodes.FirstAsync(n => n.Id == node);
        nodeEntity.Name = "Переименованный узел";
        await s.Db.SaveChangesAsync();

        var reloaded = await s.IndividualCards.GetDraftByIdAsync(dto.Id);
        Assert.NotNull(reloaded);
        var nodeSource = reloaded!.HKSources.Single(h => h.SourceHKCardId == nodeHK.Id);
        Assert.Equal(originalHKCode, nodeSource.HKCardCode);
        Assert.Equal(originalHKVersion, nodeSource.HKCardVersion);
        Assert.Equal(originalNodeName, nodeSource.SourceObjectName);
    }

    [Fact]
    public async Task CreateDraft_NoCoefficientOrItemRecords()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var itemsCount = await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == dto.Id);
        var coefficientCount = await s.Db.IndividualCardCoefficientSnapshots.CountAsync(c => c.IndividualCardId == dto.Id);
        Assert.Equal(0, itemsCount);
        Assert.Equal(0, coefficientCount);
    }

    [Fact]
    public async Task CreateDraft_DuplicateCode_RejectedAtomically()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var first = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var before = await s.Db.IndividualCards.CountAsync();
        var auditsBefore = await CountAuditsAsync(s, first.Id, "IndividualCard.DraftCreated");
        Assert.Equal(1, auditsBefore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateDraftAsync(
                new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId)));

        Assert.Contains("уже существует ИК", ex.Message);
        Assert.Equal(before, await s.Db.IndividualCards.CountAsync());
        Assert.Equal(1, await CountAuditsAsync(s, first.Id, "IndividualCard.DraftCreated"));
    }

    [Fact]
    public async Task CreateDraft_WithoutCreateDraftPermission_Rejected()
    {
        await using var s = Scope();
        // A Guest role user naturally lacks IndividualCard.CreateDraft.
        var guest = await CreateUserAsync(s, nameof(UserRole.Guest), _fixture.BranchA);
        SetUser(s, guest);
        var nodeId = await CreateNodeAsync(s);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.CreateDraftAsync(
                new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId)));
    }

    [Fact]
    public async Task CreateDraft_NonAdmin_CannotUseForeignRoot()
    {
        await using var s = Scope();
        var actor = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, actor);
        await GrantAsync(s, actor, PermissionCodes.IndividualCardCreateDraft);
        var nodeId = await CreateNodeAsync(s);
        var foreignRoot = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchB);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateDraftAsync(
                new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId, foreignRoot.Id)));

        Assert.Contains("не является допустимым утверждённым источником", ex.Message);
    }

    [Fact]
    public async Task CreateDraft_AuditExactlyOnce()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(1, await CountAuditsAsync(s, dto.Id, "IndividualCard.DraftCreated"));
        var audit = await s.Db.AuditLogs
            .FirstAsync(a => a.EntityType == "IndividualCard" && a.EntityId == dto.Id.ToString()
                && a.Action == "IndividualCard.DraftCreated");
        Assert.Contains("SelectedRootHKCardId", audit.Details);
    }

    // ── D3: read / refresh ────────────────────────────────────────────────

    [Fact]
    public async Task GetDraftById_DeterministicOrdering_OwnBranch()
    {
        await using var s = Scope();
        // NormAdmin role template already includes IndividualCard.CreateDraft.
        SetUser(s, _fixture.NormAdminA);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 2));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 3));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK, 1);
        await AddComponentAsync(s, aggregateHK, nodeHK, 2);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        var reloaded = await s.IndividualCards.GetDraftByIdAsync(dto.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.HasNormativeGaps);
        Assert.Equal(new[] { 0, 1, 2 }, reloaded.HKSources.Select(h => h.SortOrder).Take(3));
        Assert.True(reloaded.Compositions.SelectMany(c => c.Aggregates).All(a => a.Quantity == 2));
        Assert.True(reloaded.Compositions.SelectMany(c => c.Aggregates).SelectMany(a => a.Nodes).All(n => n.Quantity == 3));
        Assert.Equal(_fixture.BranchA, reloaded.BranchId);
    }

    [Fact]
    public async Task GetDraftById_DoesNotRebuildSnapshots()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        // Change live sources after creation: a newer approved HK appears.
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-NEWER-" + Suffix());

        var reloaded = await s.IndividualCards.GetDraftByIdAsync(dto.Id);
        Assert.NotNull(reloaded);
        // Snapshot still references the original root only; no automatic refresh.
        Assert.Single(reloaded!.HKSources);
        Assert.Equal(root.Id, reloaded.HKSources.Single().SourceHKCardId);
    }

    [Fact]
    public async Task GetDraftById_ReturnsNullForFormed()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var tracked = await s.Db.IndividualCards.FirstAsync(c => c.Id == dto.Id);
        tracked.Status = IndividualCardStatus.Formed;
        tracked.FormedByUserId = _fixture.SystemAdminUser.Id;
        tracked.FormedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        Assert.Null(await s.IndividualCards.GetDraftByIdAsync(dto.Id));
    }

    [Fact]
    public async Task Refresh_AuthorMayRefresh()
    {
        await using var s = Scope();
        // NormAdmin role template already includes IndividualCard.CreateDraft.
        SetUser(s, _fixture.NormAdminA);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var refreshed = await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(dto.Id));

        Assert.Equal(dto.Id, refreshed.Id);
        Assert.Equal(1, await CountAuditsAsync(s, dto.Id, "IndividualCard.SourcesRefreshed"));
    }

    [Fact]
    public async Task Refresh_UserWithEditDraft_MayRefreshOthersDraft()
    {
        await using var s = Scope();
        // NormAdmin role template already includes IndividualCard.CreateDraft.
        SetUser(s, _fixture.NormAdminA);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        // Fresh same-branch user: EditDraft granted and CreateDraft explicitly
        // denied — refresh must not require IndividualCard.CreateDraft.
        var editor = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, editor);
        await DenyAsync(s, editor, PermissionCodes.IndividualCardCreateDraft);
        await GrantAsync(s, editor, PermissionCodes.IndividualCardEditDraft);

        var refreshed = await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(dto.Id));

        Assert.Equal(dto.Id, refreshed.Id);
    }

    [Fact]
    public async Task Refresh_WithoutAuthorOrEditDraft_Rejected()
    {
        await using var s = Scope();
        // NormAdmin role template already includes IndividualCard.CreateDraft.
        SetUser(s, _fixture.NormAdminA);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        // Fresh same-branch user with the role-based CreateDraft but EditDraft
        // explicitly denied: neither the author nor an EditDraft holder.
        var outsider = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, outsider);
        await GrantAsync(s, outsider, PermissionCodes.IndividualCardCreateDraft);
        await DenyAsync(s, outsider, PermissionCodes.IndividualCardEditDraft);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.RefreshDraftSourcesAsync(
                new RefreshIndividualCardDraftSourcesRequest(dto.Id)));
        Assert.Contains("Недостаточно прав для изменения черновика", ex.Message);
    }

    [Fact]
    public async Task Refresh_MultipleRoots_FailsAndRetainsSnapshots()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-SECOND-" + Suffix());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RefreshDraftSourcesAsync(
                new RefreshIndividualCardDraftSourcesRequest(dto.Id)));

        Assert.Contains("выберите утверждённую ХК вручную", ex.Message);
        var reloaded = await s.IndividualCards.GetDraftByIdAsync(dto.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.HKSources);
        Assert.Equal(root.Id, reloaded.HKSources.Single().SourceHKCardId);
        Assert.Equal(0, await CountAuditsAsync(s, dto.Id, "IndividualCard.SourcesRefreshed"));
    }

    [Fact]
    public async Task Refresh_MissingRoot_FailsAndRetainsSnapshots()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        root.Status = HKCardStatus.Archived;
        await s.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RefreshDraftSourcesAsync(
                new RefreshIndividualCardDraftSourcesRequest(dto.Id)));

        Assert.Contains("не найдена утверждённая ХК верхнего уровня", ex.Message);
        var reloaded = await s.IndividualCards.GetDraftByIdAsync(dto.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.HKSources);
        Assert.Equal(root.Id, reloaded.HKSources.Single().SourceHKCardId);
    }

    [Fact]
    public async Task Refresh_ReplacesNeverMerges()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        Assert.Empty(dto.NormativeGaps);
        Assert.Equal(3, dto.HKSources.Count);

        // Remove the root → aggregate link, so the refresh resolves a smaller tree.
        var edge = await s.Db.HKCardComponents.FirstAsync(c => c.ParentHKCardId == root.Id);
        s.Db.HKCardComponents.Remove(edge);
        await s.Db.SaveChangesAsync();

        var refreshed = await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(dto.Id));

        // Old aggregate/node snapshots are gone; the new set reflects current sources.
        Assert.DoesNotContain(refreshed.HKSources, h => h.SourceHKCardId == aggregateHK.Id);
        Assert.DoesNotContain(refreshed.HKSources, h => h.SourceHKCardId == nodeHK.Id);
        Assert.Contains(refreshed.HKSources, h => h.SourceHKCardId == root.Id);
        Assert.Equal(1, refreshed.HKSources.Count);
    }

    [Fact]
    public async Task Refresh_CompleteToPartial_PersistsNewGaps()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        Assert.Empty(dto.NormativeGaps);

        // The node HK is archived and its link removed → the refreshed Draft is partial.
        nodeHK.Status = HKCardStatus.Archived;
        await s.Db.SaveChangesAsync();
        var edge = await s.Db.HKCardComponents.FirstAsync(c => c.ParentHKCardId == aggregateHK.Id);
        s.Db.HKCardComponents.Remove(edge);
        await s.Db.SaveChangesAsync();

        var refreshed = await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(dto.Id));

        Assert.True(refreshed.HasNormativeGaps);
        Assert.Contains(refreshed.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingLinkedHKCard);
        Assert.DoesNotContain(refreshed.HKSources, h => h.SourceHKCardId == nodeHK.Id);
    }

    [Fact]
    public async Task Refresh_PartialToComplete_ClearsGaps()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        // No node link → partial Draft.

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        Assert.True(dto.HasNormativeGaps);

        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var refreshed = await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(dto.Id));

        Assert.False(refreshed.HasNormativeGaps);
        Assert.Empty(refreshed.NormativeGaps);
        Assert.Contains(refreshed.HKSources, h => h.SourceHKCardId == nodeHK.Id);
        Assert.Equal(3, refreshed.HKSources.Count);
    }

    [Fact]
    public async Task Refresh_CannotChangeBranchId_EvenForSystemAdmin()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var rootA = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-A-" + Suffix());
        var rootB = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchB, code: "HK-B-" + Suffix());

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId, rootA.Id));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RefreshDraftSourcesAsync(
                new RefreshIndividualCardDraftSourcesRequest(dto.Id, rootB.Id)));

        Assert.Contains("другой филиал", ex.Message);
        var reloaded = await s.IndividualCards.GetDraftByIdAsync(dto.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(_fixture.BranchA, reloaded!.BranchId);
        Assert.Contains(reloaded.HKSources, h => h.SourceHKCardId == rootA.Id);
    }

    [Fact]
    public async Task Refresh_AuditExactlyOnce_AfterSuccess()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        // Failed refresh (root archived) writes no audit.
        var root = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.Id == dto.HKSources.Single().SourceHKCardId);
        var trackedRoot = await s.Db.HKCards.FirstAsync(h => h.Id == root.Id);
        trackedRoot.Status = HKCardStatus.Archived;
        await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RefreshDraftSourcesAsync(
                new RefreshIndividualCardDraftSourcesRequest(dto.Id)));
        Assert.Equal(0, await CountAuditsAsync(s, dto.Id, "IndividualCard.SourcesRefreshed"));

        // Restore the root and refresh successfully.
        trackedRoot.Status = HKCardStatus.Approved;
        await s.Db.SaveChangesAsync();
        await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(dto.Id));
        Assert.Equal(1, await CountAuditsAsync(s, dto.Id, "IndividualCard.SourcesRefreshed"));
    }

    // ── D3: deletion ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteDraft_AuthorCanDelete_SnapshotsCascade()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        await s.IndividualCards.DeleteDraftAsync(dto.Id);

        Assert.Equal(0, await s.Db.IndividualCards.CountAsync(c => c.Id == dto.Id));
        Assert.Equal(0, await s.Db.IndividualCardCompositionSnapshots.CountAsync(x => x.IndividualCardId == dto.Id));
        Assert.Equal(0, await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.IndividualCardId == dto.Id));
        Assert.Equal(0, await s.Db.IndividualCardNormativeGapSnapshots.CountAsync(x => x.IndividualCardId == dto.Id));
        // Deletion is audited and the audit survives the delete.
        Assert.Equal(1, await CountAuditsAsync(s, dto.Id, "IndividualCard.DraftDeleted"));
    }

    [Fact]
    public async Task DeleteDraft_UserWithEditDraft_CanDeleteOthersDraft()
    {
        await using var s = Scope();
        // NormAdmin role template already includes IndividualCard.CreateDraft.
        SetUser(s, _fixture.NormAdminA);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var editor = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, editor);
        await DenyAsync(s, editor, PermissionCodes.IndividualCardCreateDraft);
        await GrantAsync(s, editor, PermissionCodes.IndividualCardEditDraft);

        await s.IndividualCards.DeleteDraftAsync(dto.Id);

        Assert.Equal(0, await s.Db.IndividualCards.CountAsync(c => c.Id == dto.Id));
    }

    [Fact]
    public async Task DeleteDraft_WithoutAuthorOrEditDraft_Rejected()
    {
        await using var s = Scope();
        // NormAdmin role template already includes IndividualCard.CreateDraft.
        SetUser(s, _fixture.NormAdminA);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        // Fresh same-branch user with the role-based CreateDraft but EditDraft
        // explicitly denied: neither the author nor an EditDraft holder.
        var outsider = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, outsider);
        await GrantAsync(s, outsider, PermissionCodes.IndividualCardCreateDraft);
        await DenyAsync(s, outsider, PermissionCodes.IndividualCardEditDraft);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.DeleteDraftAsync(dto.Id));
        Assert.Contains("Недостаточно прав для удаления черновика", ex.Message);
        Assert.Equal(1, await s.Db.IndividualCards.CountAsync(c => c.Id == dto.Id));
        Assert.Equal(0, await CountAuditsAsync(s, dto.Id, "IndividualCard.DraftDeleted"));
    }

    [Fact]
    public async Task DeleteDraft_Formed_Rejected()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var tracked = await s.Db.IndividualCards.FirstAsync(c => c.Id == dto.Id);
        tracked.Status = IndividualCardStatus.Formed;
        tracked.FormedByUserId = _fixture.SystemAdminUser.Id;
        tracked.FormedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.DeleteDraftAsync(dto.Id));
        Assert.Contains("Удалить можно только черновик", ex.Message);
        Assert.Equal(1, await s.Db.IndividualCards.IgnoreQueryFilters().CountAsync(c => c.Id == dto.Id));
        Assert.Equal(0, await CountAuditsAsync(s, dto.Id, "IndividualCard.DraftDeleted"));
    }

    [Fact]
    public async Task DeleteDraft_Archived_Rejected()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var tracked = await s.Db.IndividualCards.FirstAsync(c => c.Id == dto.Id);
        tracked.Status = IndividualCardStatus.Archived;
        tracked.FormedByUserId = _fixture.SystemAdminUser.Id;
        tracked.FormedAt = DateTime.UtcNow;
        tracked.ArchivedByUserId = _fixture.SystemAdminUser.Id;
        tracked.ArchivedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.DeleteDraftAsync(dto.Id));
        Assert.Contains("Удалить можно только черновик", ex.Message);
    }

    // ── Corrective D3: occurrence identity, completeness, rollback ────────

    [Fact]
    public async Task CreateDraft_Complex_RepeatedAggregateUnderTwoModels_TwoOccurrences()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var complexId = await CreateComplexAsync(s);
        var (modelAId, _) = await CreateEquipmentAsync(s);
        var modelB = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Suffix(), Name = "Изделие " + Suffix(), IsDeleted = false };
        s.Db.EquipmentModels.Add(modelB);
        await s.Db.SaveChangesAsync();
        var sharedAggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);

        // The complex holds two Изделия; both models require the SAME aggregate.
        await CreateComplexCompositionAsync(s, complexId, (modelAId, 1), (modelB.Id, 2));
        await CreateProductCompositionAsync(s, modelAId, (sharedAggregate, 1));
        await CreateProductCompositionAsync(s, modelB.Id, (sharedAggregate, 1));
        await CreateAggregateCompositionAsync(s, sharedAggregate, (node, 3));

        var complexHK = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);
        var modelAHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelAId, _fixture.BranchA);
        var modelBHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelB.Id, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, sharedAggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, complexHK, modelAHK);
        await AddComponentAsync(s, complexHK, modelBHK);
        await AddComponentAsync(s, modelAHK, aggregateHK);
        await AddComponentAsync(s, modelBHK, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Complex, complexId));

        Assert.False(dto.HasNormativeGaps);
        // Root + 2 Изделия + 2 повторных агрегата + 2 повторных узла.
        Assert.Equal(7, dto.HKSources.Count);

        var modelSources = dto.HKSources.Where(h => h.ObjectLevel == IndividualCardObjectLevel.EquipmentModel).ToList();
        Assert.Equal(2, modelSources.Count);
        var aggregateSources = dto.HKSources.Where(h => h.SourceHKCardId == aggregateHK.Id).ToList();
        Assert.Equal(2, aggregateSources.Count);
        // Each repeated aggregate is a distinct tree position under its own Изделие.
        Assert.NotEqual(aggregateSources[0].Id, aggregateSources[1].Id);
        Assert.Equal(
            new HashSet<Guid?>(modelSources.Select(m => (Guid?)m.Id)),
            new HashSet<Guid?>(aggregateSources.Select(a => a.ParentHKSourceSnapshotId)));

        var nodeSources = dto.HKSources.Where(h => h.SourceHKCardId == nodeHK.Id).ToList();
        Assert.Equal(2, nodeSources.Count);
        // Each repeated node is a distinct tree position under its own aggregate occurrence.
        Assert.Equal(
            new HashSet<Guid?>(aggregateSources.Select(a => (Guid?)a.Id)),
            new HashSet<Guid?>(nodeSources.Select(n => n.ParentHKSourceSnapshotId)));

        // Compositions: one per Изделие, with the complex-level Quantity.
        var compositionB = dto.Compositions.Single(c => c.TargetObjectId == modelB.Id);
        Assert.Equal(2, compositionB.Quantity);
        Assert.Single(compositionB.Aggregates);
        Assert.Equal(3, compositionB.Aggregates.Single().Nodes.Single().Quantity);
        // Every source of a complete chain is complete.
        Assert.All(dto.HKSources, h => Assert.True(h.IsComplete));
    }

    [Fact]
    public async Task CreateDraft_PartialChain_SourceCompletenessPropagation()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        // Node HK link missing → the chain is partial.

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.True(dto.HasNormativeGaps);
        // The aggregate position is broken (its child is missing) and the root
        // inherits incompleteness from it.
        var rootSource = dto.HKSources.Single(h => h.SourceHKCardId == root.Id);
        var aggregateSource = dto.HKSources.Single(h => h.SourceHKCardId == aggregateHK.Id);
        Assert.False(aggregateSource.IsComplete);
        Assert.False(rootSource.IsComplete);
    }

    [Fact]
    public async Task Refresh_IsComplete_PartialToCompletePropagation()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        // No node link → partial Draft.

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        Assert.True(dto.HasNormativeGaps);

        // Close the gap and refresh: every source becomes complete.
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var refreshed = await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(dto.Id));

        Assert.False(refreshed.HasNormativeGaps);
        Assert.Empty(refreshed.NormativeGaps);
        Assert.All(refreshed.HKSources, h => Assert.True(h.IsComplete));
    }

    [Fact]
    public async Task Refresh_InjectedFailureMidReplace_RollsBackAndRetainsOldSnapshots()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var dto = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        Assert.Equal(3, dto.HKSources.Count);
        Assert.Empty(dto.NormativeGaps);

        // Change the sources, then make the composition-snapshot delete fail
        // mid-replace: the whole refresh (including the earlier node/aggregate
        // snapshot deletes) must roll back and keep the old snapshot set.
        var secondNode = await CreateNodeAsync(s);
        var secondNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, secondNode, _fixture.BranchA);
        await AddComponentAsync(s, aggregateHK, secondNodeHK);

        try
        {
            FailingCommandInterceptor.ArmAt("IndividualCardCompositionSnapshots");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                s.IndividualCards.RefreshDraftSourcesAsync(
                    new RefreshIndividualCardDraftSourcesRequest(dto.Id)));
            Assert.Contains("Injected test failure", ex.Message);
            Assert.True(FailingCommandInterceptor.Fired, "Interceptor did not fire.");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        // The old snapshot set is fully restored: 3 HK sources and the original
        // composition with exactly one node — the second node never appears.
        var reloaded = await s.IndividualCards.GetDraftByIdAsync(dto.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(3, reloaded!.HKSources.Count);
        Assert.DoesNotContain(reloaded.HKSources, h => h.SourceHKCardId == secondNodeHK.Id);
        Assert.Equal(1, reloaded.Compositions.Single().Aggregates.Single().Nodes.Count);
        Assert.Equal(0, await CountAuditsAsync(s, dto.Id, "IndividualCard.SourcesRefreshed"));
    }
}
