using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardPreflightIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardPreflightIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task GrantCreateDraftAsync(TestScope s, ApplicationUser user)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == PermissionCodes.IndividualCardCreateDraft)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = PermissionCodes.IndividualCardCreateDraft,
            IsGranted = true, Reason = "Test", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private async Task DenyCreateDraftAsync(TestScope s, ApplicationUser user)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == PermissionCodes.IndividualCardCreateDraft)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = PermissionCodes.IndividualCardCreateDraft,
            IsGranted = false, Reason = "Test deny", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

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

    private static IndividualCardPreflightRequest Preflight(
        IndividualCardObjectLevel level, Guid objectId, Guid? rootHKCardId = null) =>
        new(level, objectId, rootHKCardId);

    // ── 8.1 Target / root selection ───────────────────────────────────────

    [Fact]
    public async Task FiveLevels_ResolveTargetDisplayData()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);

        var nodeId = await CreateNodeAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var (modelId, instanceId) = await CreateEquipmentAsync(s);
        var complexId = await CreateComplexAsync(s);

        var nodeResult = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));
        var aggregateResult = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));
        var modelResult = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));
        var instanceResult = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentInstance, instanceId));
        var complexResult = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Complex, complexId));

        Assert.NotEqual(string.Empty, nodeResult.ObjectName);
        Assert.NotEqual(string.Empty, aggregateResult.ObjectName);
        Assert.NotEqual(string.Empty, modelResult.ObjectName);
        Assert.NotEqual(string.Empty, instanceResult.ObjectName);
        Assert.NotEqual(string.Empty, complexResult.ObjectName);
        Assert.Equal("Комплекс", complexResult.ObjectDisplayType);
        Assert.Equal("Изделие", modelResult.ObjectDisplayType);
        Assert.Equal("Агрегат", aggregateResult.ObjectDisplayType);
        Assert.Equal("Узел", nodeResult.ObjectDisplayType);
        Assert.Equal("Экземпляр техники", instanceResult.ObjectDisplayType);
    }

    [Fact]
    public async Task EquipmentModelTarget_UsesIzdelieLabel_NeverModelTechniki()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.Equal("Изделие", result.ObjectDisplayType);
        Assert.DoesNotContain("Модель техники", result.NormativeGaps.Select(g => g.Message)
            .Concat(new[] { result.ObjectDisplayType, result.ObjectName }));
    }

    [Fact]
    public async Task ZeroRootCandidates_MissingStateWithGap()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardPreflightRootState.Missing, result.RootState);
        Assert.Null(result.SelectedRoot);
        Assert.Null(result.BranchId);
        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingRootHKCard);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task SingleRootCandidate_AutomaticallySelected()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardPreflightRootState.AutomaticallySelected, result.RootState);
        Assert.NotNull(result.SelectedRoot);
        Assert.Equal(root.Id, result.SelectedRoot!.HKCardId);
        Assert.Equal(root.BranchId, result.BranchId);
    }

    [Fact]
    public async Task MultipleRootCandidates_SelectionRequiredWithoutSelectedRoot()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var first = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-A-" + Suffix());
        var second = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-B-" + Suffix());

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardPreflightRootState.SelectionRequired, result.RootState);
        Assert.Null(result.SelectedRoot);
        Assert.Null(result.BranchId);
        Assert.Equal(2, result.RootCandidates.Count);
        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.RootSelectionRequired);

        // Deterministic display order: Code ASC.
        Assert.True(string.CompareOrdinal(result.RootCandidates[0].Code, result.RootCandidates[1].Code) <= 0);
    }

    [Fact]
    public async Task ExplicitValidRoot_ExplicitlySelected()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var first = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-A-" + Suffix());
        var second = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, code: "HK-B-" + Suffix());

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId, second.Id));

        Assert.Equal(IndividualCardPreflightRootState.ExplicitlySelected, result.RootState);
        Assert.Equal(second.Id, result.SelectedRoot!.HKCardId);
    }

    [Fact]
    public async Task ExplicitRootWrongObject_ControlledRejection()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var otherNodeId = await CreateNodeAsync(s);
        var wrongObjectRoot = await CreateHKAsync(s, IndividualCardObjectLevel.Node, otherNodeId, _fixture.BranchA);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId, wrongObjectRoot.Id)));
        Assert.Contains("не является допустимым утверждённым источником", ex.Message);
    }

    [Fact]
    public async Task ExplicitRootWrongLevel_ControlledRejection()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId, aggregateHK.Id)));
        Assert.Contains("не является допустимым утверждённым источником", ex.Message);
    }

    [Fact]
    public async Task ExplicitRootNotApproved_ControlledRejection()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var draftRoot = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA, HKCardStatus.Draft);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId, draftRoot.Id)));
        Assert.Contains("не является допустимым утверждённым источником", ex.Message);
    }

    [Fact]
    public async Task ExplicitRootOtherBranch_NotAccessibleForNonAdmin()
    {
        await using var s = Scope();
        SetUser(s, _fixture.NormAdminA);
        await GrantCreateDraftAsync(s, _fixture.NormAdminA);
        var nodeId = await CreateNodeAsync(s);
        var foreignRoot = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchB);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId, foreignRoot.Id)));
        Assert.Contains("не является допустимым утверждённым источником", ex.Message);
    }

    [Fact]
    public async Task Dates_DoNotExcludeApprovedRoot()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var hk = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        // An Approved matching root remains valid regardless of dates,
        // including an expiration date in the past.
        hk.ExpirationDate = DateTime.UtcNow.AddDays(-30);
        hk.EffectiveDate = DateTime.UtcNow.AddDays(-90);
        await s.Db.SaveChangesAsync();

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardPreflightRootState.AutomaticallySelected, result.RootState);
        Assert.NotNull(result.SelectedRoot);
        // Dates survive a PostgreSQL timestamptz round-trip (microsecond precision).
        Assert.NotNull(result.SelectedRoot!.ExpirationDate);
        Assert.Equal(TrimMs(hk.ExpirationDate!.Value), TrimMs(result.SelectedRoot.ExpirationDate.Value));
        Assert.Equal(TrimMs(hk.EffectiveDate!.Value), TrimMs(result.SelectedRoot.EffectiveDate.Value));
    }

    // ── 8.2 Constructive paths ────────────────────────────────────────────

    [Fact]
    public async Task NodePreflight_RequiresNoComposition()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        Assert.Empty(result.Compositions);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task AggregatePreflight_ReturnsActiveApprovedCompositionWithNodes()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node1 = await CreateNodeAsync(s);
        var node2 = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node1, 2), (node2, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.Equal(root.Id, result.SelectedRoot!.HKCardId);
        var composition = Assert.Single(result.Compositions);
        Assert.Equal(IndividualCardObjectLevel.Aggregate, composition.SourceLevel);
        var aggregateDto = Assert.Single(composition.Aggregates);
        Assert.Equal(2, aggregateDto.Nodes.Count);
        Assert.Contains(aggregateDto.Nodes, n => n.NodeId == node1 && n.Quantity == 2);
        // Structural data resolved; the chain itself is not linked yet, so only
        // linked-chain gaps are expected, never root/composition gaps.
        Assert.DoesNotContain(result.NormativeGaps, g => g.Kind is IndividualCardNormativeGapKind.MissingRootHKCard
            or IndividualCardNormativeGapKind.RootSelectionRequired
            or IndividualCardNormativeGapKind.MissingApprovedComposition);
    }

    [Fact]
    public async Task EquipmentModelPreflight_ReturnsCompositionWithAggregates()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate1 = await CreateAggregateAsync(s);
        var aggregate2 = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate1, 2), (aggregate2, 1));
        await CreateAggregateCompositionAsync(s, aggregate1, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.Equal(root.Id, result.SelectedRoot!.HKCardId);
        var composition = Assert.Single(result.Compositions);
        Assert.Equal(IndividualCardObjectLevel.EquipmentModel, composition.SourceLevel);
        Assert.Equal(2, composition.Aggregates.Count);
        Assert.Equal(2, composition.Aggregates.First(a => a.AggregateId == aggregate1).Quantity);
        Assert.Equal("Изделие", result.ObjectDisplayType);
    }

    [Fact]
    public async Task EquipmentInstancePreflight_UsesLinkedModelComposition()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, instanceId) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentInstance, instanceId));

        Assert.Equal(root.Id, result.SelectedRoot!.HKCardId);
        Assert.Equal(IndividualCardObjectLevel.EquipmentModel, result.SelectedRoot!.ObjectLevel);
        var composition = Assert.Single(result.Compositions);
        Assert.Equal(IndividualCardObjectLevel.EquipmentModel, composition.SourceLevel);
        Assert.Contains(composition.Aggregates, a => a.AggregateId == aggregate);
    }

    [Fact]
    public async Task ComplexPreflight_ReturnsFullHierarchy()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var complexId = await CreateComplexAsync(s);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateComplexCompositionAsync(s, complexId, (modelId, 2));
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 3));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Complex, complexId));

        Assert.Equal(root.Id, result.SelectedRoot!.HKCardId);
        var composition = Assert.Single(result.Compositions);
        Assert.Equal(IndividualCardObjectLevel.Complex, composition.SourceLevel);
        // ComplexCompositionItem.Quantity must be exposed as the row multiplier.
        Assert.Equal(2, composition.Quantity);
        Assert.Contains(composition.Aggregates, a => a.AggregateId == aggregate);
    }

    [Fact]
    public async Task InactiveApprovedComposition_NotUsedAsFallback()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var inactive = await _dbAddInactiveCompositionAsync(s, modelId);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.Empty(result.Compositions);
        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingApprovedComposition);
        Assert.False(result.IsComplete);
    }

    private async Task<ProductComposition> _dbAddInactiveCompositionAsync(TestScope s, Guid modelId)
    {
        var pc = new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = modelId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.ProductCompositions.Add(pc);
        await s.Db.SaveChangesAsync();
        return pc;
    }

    [Fact]
    public async Task NonApprovedComposition_NotUsedAsFallback()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var draft = new AggregateComposition
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Draft,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.AggregateCompositions.Add(draft);
        await s.Db.SaveChangesAsync();
        await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.Empty(result.Compositions);
        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingApprovedComposition);
    }

    [Fact]
    public async Task MissingComposition_YieldsGapNotException()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        var gap = Assert.Single(result.NormativeGaps);
        Assert.Equal(IndividualCardNormativeGapKind.MissingApprovedComposition, gap.Kind);
        Assert.Contains("изделия", gap.Message);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task RepeatedQuantities_ReturnedAsStructuralQuantity()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        // Quantity 5: one composition row, one HK requirement — not five.
        await CreateProductCompositionAsync(s, modelId, (aggregate, 5));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 4));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.True(result.IsComplete);
        var aggregateDto = Assert.Single(result.Compositions[0].Aggregates);
        Assert.Equal(5, aggregateDto.Quantity);
        Assert.Equal(4, aggregateDto.Nodes.Single().Quantity);
        // Exactly one aggregate HK source — quantity is structural, not duplicated.
        Assert.Equal(1, result.HKSources.Count(h => h.ObjectLevel == IndividualCardObjectLevel.Aggregate));
    }

    // ── 8.3 HKCardComponent chain ─────────────────────────────────────────

    [Fact]
    public async Task NodeRoot_ResolvesItselfComplete()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        var source = Assert.Single(result.HKSources);
        Assert.Equal(root.Id, source.HKCardId);
        Assert.Null(source.ParentHKCardId);
        Assert.True(source.IsComplete);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task AggregateRoot_WithLinkedApprovedNodeHK_ResolvesChain()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, nodeHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.HKSources.Count);
        var nodeSource = result.HKSources.Single(h => h.ObjectLevel == IndividualCardObjectLevel.Node);
        Assert.Equal(root.Id, nodeSource.ParentHKCardId);
        Assert.True(nodeSource.IsComplete);
        Assert.Empty(result.NormativeGaps);
    }

    [Fact]
    public async Task ModelRoot_WithLinkedAggregateAndNodes_ResolvesChain()
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

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.HKSources.Count);
        Assert.Contains(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Aggregate && h.ParentHKCardId == root.Id);
        Assert.Contains(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Node && h.ParentHKCardId == aggregateHK.Id);
        Assert.Empty(result.NormativeGaps);
    }

    [Fact]
    public async Task ComplexRoot_ResolvesFullHierarchy()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var complexId = await CreateComplexAsync(s);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateComplexCompositionAsync(s, complexId, (modelId, 1));
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        await CreateAggregateCompositionAsync(s, aggregate, (node, 1));

        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA);
        await AddComponentAsync(s, root, modelHK);
        await AddComponentAsync(s, modelHK, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Complex, complexId));

        Assert.True(result.IsComplete);
        Assert.Equal(4, result.HKSources.Count);
        Assert.Contains(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.EquipmentModel && h.ParentHKCardId == root.Id);
        Assert.Contains(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Aggregate && h.ParentHKCardId == modelHK.Id);
        Assert.Contains(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Node && h.ParentHKCardId == aggregateHK.Id);
        Assert.Empty(result.NormativeGaps);
    }

    [Fact]
    public async Task MissingLinkedHK_ProducesGap()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        // No component edge to the node HK.

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingLinkedHKCard);
        Assert.False(result.IsComplete);
        // No fallback substitutes the missing link.
        Assert.DoesNotContain(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Node);
    }

    [Fact]
    public async Task LinkedHKNotApproved_ProducesGap()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchA, HKCardStatus.Draft);
        await AddComponentAsync(s, root, nodeHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.LinkedHKCardNotApproved);
        Assert.DoesNotContain(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Node);
    }

    [Fact]
    public async Task LinkedHKWrongObject_ProducesGap()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var requiredNode = await CreateNodeAsync(s);
        var otherNode = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (requiredNode, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var wrongNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, otherNode, _fixture.BranchA);
        await AddComponentAsync(s, root, wrongNodeHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.LinkedHKCardWrongObject);
        Assert.DoesNotContain(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Node);
    }

    [Fact]
    public async Task LinkedHKWrongLevel_ProducesGap()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var otherAggregate = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        // The component edge points to an aggregate-level HK (of another object)
        // instead of the expected node-level HK.
        var wrongLevelHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, otherAggregate, _fixture.BranchA);
        await AddComponentAsync(s, root, wrongLevelHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.Equal(IndividualCardPreflightRootState.AutomaticallySelected, result.RootState);
        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.LinkedHKCardWrongLevel);
    }

    [Fact]
    public async Task LinkedHKWrongBranch_ProducesGap()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var aggregateId = await CreateAggregateAsync(s);
        var node = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, aggregateId, (node, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var foreignNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node, _fixture.BranchB);
        await AddComponentAsync(s, root, foreignNodeHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Aggregate, aggregateId));

        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.LinkedHKCardWrongBranch);
        Assert.Equal(_fixture.BranchA, result.BranchId);
        // No substitution by another HK of the same object.
        Assert.DoesNotContain(result.HKSources, h => h.ObjectLevel == IndividualCardObjectLevel.Node);
    }

    [Fact]
    public async Task ExtraLinkedHK_ProducesInconsistentChainGap()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate = await CreateAggregateAsync(s);
        var extraAggregate = await CreateAggregateAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate, _fixture.BranchA);
        var extraHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, extraAggregate, _fixture.BranchA);
        await AddComponentAsync(s, root, aggregateHK);
        await AddComponentAsync(s, root, extraHK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.InconsistentNormativeChain);
        // The required aggregate is still resolved; the extra one is not part of the chain.
        Assert.Contains(result.HKSources, h => h.HKCardId == aggregateHK.Id);
        Assert.DoesNotContain(result.HKSources, h => h.HKCardId == extraHK.Id);
    }

    [Fact]
    public async Task PartialTree_ReturnsResolvedBranchesAndGaps()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregate1 = await CreateAggregateAsync(s);
        var aggregate2 = await CreateAggregateAsync(s);
        var node1 = await CreateNodeAsync(s);
        var node2 = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregate1, 1), (aggregate2, 1));
        await CreateAggregateCompositionAsync(s, aggregate1, (node1, 1));
        await CreateAggregateCompositionAsync(s, aggregate2, (node2, 1));
        var root = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var agg1HK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate1, _fixture.BranchA);
        var agg2HK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate2, _fixture.BranchA);
        var node1HK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node1, _fixture.BranchA);
        var node2HK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, node2, _fixture.BranchA);
        await AddComponentAsync(s, root, agg1HK);
        await AddComponentAsync(s, root, agg2HK);
        // aggregate2 branch is fully valid; aggregate1 branch is missing its node HK.
        await AddComponentAsync(s, agg2HK, node2HK);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.EquipmentModel, modelId));

        // Valid branches are resolved, including the node of aggregate2.
        Assert.Contains(result.HKSources, h => h.HKCardId == agg1HK.Id);
        Assert.Contains(result.HKSources, h => h.HKCardId == agg2HK.Id);
        Assert.Contains(result.HKSources, h => h.HKCardId == node2HK.Id && h.ParentHKCardId == agg2HK.Id);
        // aggregate1's node HK is missing → gap.
        Assert.Contains(result.NormativeGaps, g => g.Kind == IndividualCardNormativeGapKind.MissingLinkedHKCard);
        Assert.False(result.IsComplete);
    }

    // ── 8.4 Authorization and legacy lock ─────────────────────────────────

    [Fact]
    public async Task WithoutCreateDraftPermission_CannotPreflight()
    {
        await using var s = Scope();
        SetUser(s, _fixture.OperatorA);
        await DenyCreateDraftAsync(s, _fixture.OperatorA);
        var nodeId = await CreateNodeAsync(s);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId)));
    }

    [Fact]
    public async Task OperatorWithCreateDraft_CanPreflightOwnBranch()
    {
        await using var s = Scope();
        SetUser(s, _fixture.OperatorA);
        await GrantCreateDraftAsync(s, _fixture.OperatorA);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardPreflightRootState.AutomaticallySelected, result.RootState);
        Assert.Equal(_fixture.BranchA, result.BranchId);
    }

    [Fact]
    public async Task NonAdmin_CannotReachOtherBranchViaObjectOrRoot()
    {
        await using var s = Scope();
        SetUser(s, _fixture.NormAdminA);
        await GrantCreateDraftAsync(s, _fixture.NormAdminA);

        // Node HK exists only in BranchB; NormAdminA (BranchA) must not see it.
        var nodeId = await CreateNodeAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchB);

        var result = await s.IndividualCards.BuildPreflightAsync(Preflight(IndividualCardObjectLevel.Node, nodeId));

        Assert.Equal(IndividualCardPreflightRootState.Missing, result.RootState);
        Assert.Empty(result.RootCandidates);
        Assert.Null(result.BranchId);
    }

    [Fact]
    public async Task LegacyGenerate_CreatesNoCard_ReturnsControlledError()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, instanceId) = await CreateEquipmentAsync(s);
        var beforeCount = await s.Db.IndividualCards.CountAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.GenerateCardsForInstanceAsync(instanceId, new List<Guid>()));

        Assert.Contains("временно недоступно", ex.Message);
        Assert.Equal(beforeCount, await s.Db.IndividualCards.CountAsync());
    }
}
