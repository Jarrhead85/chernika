using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardVersionAndArchiveIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardVersionAndArchiveIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private async Task<int> CountAuditsAsync(TestScope s, Guid entityId, string action) =>
        await s.Db.AuditLogs.CountAsync(a =>
            a.EntityType == "IndividualCard" && a.EntityId == entityId.ToString() && a.Action == action);

    // ── reference helpers ─────────────────────────────────────────────────

    private async Task<Guid> CreateNodeAsync(TestScope s)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
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

    private async Task<Guid> CreateEquipmentAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Suffix(), Name = "Изделие " + Suffix(), IsDeleted = false };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();
        return model.Id;
    }

    private async Task<Guid> CreateAssemblyUnitAsync(TestScope s)
    {
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix(), IsDeleted = false };
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();
        return au.Id;
    }

    private async Task<Guid> CreateGsmMaterialAsync(TestScope s, string? name = null, string? gost = null)
    {
        var material = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = name ?? "ГСМ " + Suffix(),
            Type = "Тип " + Suffix(),
            Gost = gost,
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.GsmMaterials.Add(material);
        await s.Db.SaveChangesAsync();
        return material.Id;
    }

    private async Task<HKCard> CreateHKAsync(
        TestScope s, IndividualCardObjectLevel level, Guid objectId, Guid branchId,
        HKCardStatus status = HKCardStatus.Approved)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + level.ToString()[..3] + "-" + Suffix(),
            Version = "v" + Suffix()[..4],
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

    private async Task AddComponentAsync(TestScope s, Guid parentHKCardId, Guid childHKCardId, int sortOrder = 1)
    {
        var parent = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.Id == parentHKCardId);
        var child = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.Id == childHKCardId);
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

    private async Task RemoveComponentAsync(TestScope s, Guid parentHKCardId, Guid childHKCardId)
    {
        var edge = await s.Db.HKCardComponents
            .FirstAsync(c => c.ParentHKCardId == parentHKCardId && c.ChildHKCardId == childHKCardId);
        s.Db.HKCardComponents.Remove(edge);
        await s.Db.SaveChangesAsync();
    }

    private async Task AddNodeItemsAsync(
        TestScope s, Guid nodeHKCardId,
        params (Guid AssemblyUnitId, int Quantity, decimal Volume, string Unit, (Guid GsmId, GsmCategory Category)[] Materials)[] items)
    {
        foreach (var (auId, quantity, volume, unit, materials) in items)
        {
            var item = new HKCardItem
            {
                Id = Guid.NewGuid(),
                HKCardId = nodeHKCardId,
                AssemblyUnitId = auId,
                Quantity = quantity,
                Volume = volume,
                UnitOfMeasure = unit,
                SortOrder = 1,
            };
            foreach (var (gsmId, category) in materials)
            {
                item.Materials.Add(new HKCardItemMaterial
                {
                    Id = Guid.NewGuid(),
                    HKCardItemId = item.Id,
                    GsmMaterialId = gsmId,
                    Category = category,
                });
            }
            s.Db.HKCardItems.Add(item);
        }
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

    /// <summary>Full Изделие chain with a valid primary GSM item, plus a
    /// calculated+formed ИК ready for D5 operations.</summary>
    private async Task<(Guid DraftId, Guid ModelId, Guid AggregateId, Guid NodeId, Guid NodeHKCardId, Guid AggregateHKCardId)>
        CreateFormedCardAsync(TestScope s, decimal volume = 100m, int pcAggregateQuantity = 1, int acNodeQuantity = 1, bool form = true)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        await CreateProductCompositionAsync(s, modelId, (aggregateId, pcAggregateQuantity));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, acNodeQuantity));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, volume, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        if (form)
        {
            await s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
            await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        }
        return (draft.Id, modelId, aggregateId, nodeId, nodeHK.Id, aggregateHK.Id);
    }

    private async Task<Guid> CreateCoefficientTypeAsync(TestScope s, string name)
    {
        var type = new CoefficientType
        {
            Id = Guid.NewGuid(), Name = name, SortOrder = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        s.Db.CoefficientTypes.Add(type);
        await s.Db.SaveChangesAsync();
        return type.Id;
    }

    private async Task<Guid> CreateCoefficientAsync(TestScope s, Guid typeId, string name, decimal value)
    {
        var coefficient = new Coefficient
        {
            Id = Guid.NewGuid(),
            CoefficientTypeId = typeId,
            Name = name,
            Value = value,
            SortOrder = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.Coefficients.Add(coefficient);
        await s.Db.SaveChangesAsync();
        return coefficient.Id;
    }

    // ── 1–2: only Formed can create a new version ─────────────────────────

    [Fact]
    public async Task DraftSource_CannotBuildComparisonOrNewVersion()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (_, modelId, _, _, _, _) = await CreateFormedCardAsync(s);

        // A plain Draft (never formed) as source: full chain, no form step.
        var (_, draftModelId, _, _, _, _) = await CreateFormedCardAsync(s, form: false);
        var plainDraft = await s.Db.IndividualCards.AsNoTracking()
            .FirstAsync(c => c.EquipmentModelId == draftModelId && c.Status == IndividualCardStatus.Draft);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildNewVersionComparisonAsync(
                new IndividualCardVersionPreflightRequest(plainDraft.Id)));
        Assert.Contains("только для сформированной", ex.Message);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(plainDraft.Id)));
        Assert.Contains("только для сформированной", ex2.Message);
        Assert.Equal(0, await CountAuditsAsync(s, plainDraft.Id, "IndividualCard.NewVersionCreated"));
        _ = modelId;
    }

    [Fact]
    public async Task ArchivedSource_CannotCreateNewVersion()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        // A Draft cannot be archived: only Formed → Archived.
        var (_, draftModelId, _, _, _, _) = await CreateFormedCardAsync(s, form: false);
        var draftOnly = await s.Db.IndividualCards.AsNoTracking()
            .FirstAsync(c => c.EquipmentModelId == draftModelId && c.Status == IndividualCardStatus.Draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.ArchiveIndividualCardAsync(draftOnly.Id));

        await s.IndividualCards.ArchiveIndividualCardAsync(draftId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildNewVersionComparisonAsync(
                new IndividualCardVersionPreflightRequest(draftId)));
        Assert.Contains("только для сформированной", ex.Message);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId)));
        Assert.Contains("только для сформированной", ex2.Message);
    }

    // ── 3: permission and branch scope ────────────────────────────────────

    [Fact]
    public async Task NewVersion_PermissionAndBranchScope_Enforced()
    {
        await using var s = Scope();
        var author = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);
        SetUser(s, author);

        // Author without CreateVersion: denied.
        await DenyAsync(s, author, PermissionCodes.IndividualCardCreateVersion);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.BuildNewVersionComparisonAsync(
                new IndividualCardVersionPreflightRequest(draftId)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId)));

        // Foreign branch user with CreateVersion: denied.
        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        SetUser(s, foreign);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId)));
    }

    // ── 4: fresh preflight uses current sources ───────────────────────────

    [Fact]
    public async Task NewVersion_UsesFreshCurrentSources()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, modelId, aggregateId, _, _, aggregateHKCardId) = await CreateFormedCardAsync(s);

        // AFTER forming: extend the current aggregate composition with a new
        // node HK. The source card snapshots do not know about it; the fresh
        // preflight does.
        var newNodeId = await CreateNodeAsync(s);
        var newNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, newNodeId, _fixture.BranchA);
        await AddComponentAsync(s, aggregateHKCardId, newNodeHK.Id);
        var trackedAC = await s.Db.AggregateCompositions.FirstAsync(ac => ac.AggregateId == aggregateId);
        s.Db.AggregateCompositionNodes.Add(new AggregateCompositionNode
        {
            Id = Guid.NewGuid(),
            AggregateCompositionId = trackedAC.Id,
            NodeId = newNodeId,
            Quantity = 1,
            SortOrder = 2,
        });
        await s.Db.SaveChangesAsync();

        var created = await s.IndividualCards.CreateNewVersionAsync(
            new CreateIndividualCardVersionRequest(draftId));

        Assert.Contains(created.HKSources, h => h.SourceHKCardId == newNodeHK.Id);
    }

    // ── 5–6: root selection ───────────────────────────────────────────────

    [Fact]
    public async Task RootSelection_AutoAndExplicit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, modelId, _, _, nodeHKCardId, _) = await CreateFormedCardAsync(s);
        var rootHKCardId = (await s.Db.HKCards.AsNoTracking()
            .FirstAsync(h => h.EquipmentModelId == modelId)).Id;

        // Single root (the Изделие HK): auto-selected and ready.
        var comparison = await s.IndividualCards.BuildNewVersionComparisonAsync(
            new IndividualCardVersionPreflightRequest(draftId));
        Assert.Equal(IndividualCardPreflightRootState.AutomaticallySelected, comparison.RootState);
        Assert.True(comparison.IsReadyToCreateDraft);
        Assert.NotNull(comparison.SelectedRoot);
        Assert.Equal(rootHKCardId, comparison.SelectedRoot!.HKCardId);

        // Second approved HK for the same node OBJECT (not the root) does not
        // affect root selection; the root HK is the Изделие-level one.
        var secondNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, await CreateNodeAsync(s), _fixture.BranchA);
        _ = secondNodeHK;

        // A second approved root HK for the same object → explicit selection.
        var secondRootHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var comparison2 = await s.IndividualCards.BuildNewVersionComparisonAsync(
            new IndividualCardVersionPreflightRequest(draftId));
        Assert.Equal(IndividualCardPreflightRootState.SelectionRequired, comparison2.RootState);
        Assert.False(comparison2.IsReadyToCreateDraft);
        Assert.Equal(2, comparison2.RootCandidates.Count);

        var created = await s.IndividualCards.CreateNewVersionAsync(
            new CreateIndividualCardVersionRequest(draftId, secondRootHK.Id));
        Assert.Equal(IndividualCardStatus.Draft, created.Status);

        // Without an explicit root the creation is rejected.
        var (draftId2, modelId2, _, _, _, _) = await CreateFormedCardAsync(s);
        await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId2, _fixture.BranchA);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId2)));
        Assert.Contains("выберите утверждённую ХК", ex.Message);
        Assert.Equal(0, await CountAuditsAsync(s, draftId2, "IndividualCard.NewVersionCreated"));
        _ = nodeHKCardId;
    }

    [Fact]
    public async Task MissingRoot_RejectsWithNoCardAndNoAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, modelId, _, _, _, _) = await CreateFormedCardAsync(s);

        // Archive the ROOT (Изделие) HK → no approved root remains.
        var rootHK = await s.Db.HKCards.FirstAsync(h => h.EquipmentModelId == modelId);
        rootHK.Status = HKCardStatus.Archived;
        await s.Db.SaveChangesAsync();

        var comparison = await s.IndividualCards.BuildNewVersionComparisonAsync(
            new IndividualCardVersionPreflightRequest(draftId));
        Assert.Equal(IndividualCardPreflightRootState.Missing, comparison.RootState);
        Assert.False(comparison.IsReadyToCreateDraft);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId)));
        Assert.Contains("не найдена утверждённая ХК", ex.Message);
        Assert.Equal(0, await s.Db.IndividualCards.CountAsync(c => c.SupersedesIndividualCardId == draftId));
        Assert.Equal(0, await CountAuditsAsync(s, draftId, "IndividualCard.NewVersionCreated"));
    }

    // ── 7: partial fresh preflight ────────────────────────────────────────

    [Fact]
    public async Task PartialPreflight_CreatesPartialNewDraft()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, modelId, _, _, _, _) = await CreateFormedCardAsync(s);

        // Break the current chain: second aggregate without node HK link.
        var brokenAggregateId = await CreateAggregateAsync(s);
        var brokenAggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, brokenAggregateId, _fixture.BranchA);
        var modelHK = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.EquipmentModelId == modelId);
        await AddComponentAsync(s, modelHK.Id, brokenAggregateHK.Id);
        var trackedPC = await s.Db.ProductCompositions.FirstAsync(pc => pc.EquipmentModelId == modelId);
        s.Db.ProductCompositionAggregates.Add(new ProductCompositionAggregate
        {
            Id = Guid.NewGuid(),
            ProductCompositionId = trackedPC.Id,
            AggregateId = brokenAggregateId,
            Quantity = 1,
            SortOrder = 2,
        });
        await s.Db.SaveChangesAsync();

        var created = await s.IndividualCards.CreateNewVersionAsync(
            new CreateIndividualCardVersionRequest(draftId));

        Assert.True(created.HasNormativeGaps);
        Assert.NotEmpty(created.NormativeGaps);
    }

    // ── 8–10: identity, empty calculation, fresh snapshots ────────────────

    [Fact]
    public async Task NewDraft_Identity_NoCalculation_FreshSnapshots()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);
        var source = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == draftId);
        var sourceHKSourceCount = await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.IndividualCardId == draftId);

        var created = await s.IndividualCards.CreateNewVersionAsync(
            new CreateIndividualCardVersionRequest(draftId));

        Assert.Equal(source.Code, created.Code);
        Assert.Matches(@"^v\d{4}\.\d+$", created.Version);
        Assert.Equal(source.RevisionNumber + 1, created.RevisionNumber);
        Assert.Equal(source.BranchId, created.BranchId);
        Assert.Equal(IndividualCardStatus.Draft, created.Status);

        var successor = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.Equal(draftId, successor.SupersedesIndividualCardId);
        Assert.Equal(0m, successor.TotalNorm);
        Assert.Equal(0, await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == created.Id));
        Assert.Equal(0, await s.Db.IndividualCardCoefficientSnapshots.CountAsync(x => x.IndividualCardId == created.Id));

        // Fresh snapshots copied from the preflight, not the source.
        Assert.Equal(sourceHKSourceCount, created.HKSources.Count);
    }

    // ── 11: foreign branch root ───────────────────────────────────────────

    [Fact]
    public async Task ForeignBranchRoot_Rejected_EvenForSystemAdmin()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, nodeId, _, _) = await CreateFormedCardAsync(s);

        var foreignHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchB);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId, foreignHK.Id)));
        // The fresh preflight rejects a foreign-branch root as a candidate.
        Assert.Contains("допустимым утверждённым источником", ex.Message);
        Assert.Equal(0, await s.Db.IndividualCards.CountAsync(c => c.SupersedesIndividualCardId == draftId));
        Assert.Equal(0, await CountAuditsAsync(s, draftId, "IndividualCard.NewVersionCreated"));
    }

    // ── 12–13: single successor and audit ─────────────────────────────────

    [Fact]
    public async Task SecondCreate_YieldsOneSuccessor_ControlledError()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        var first = await s.IndividualCards.CreateNewVersionAsync(
            new CreateIndividualCardVersionRequest(draftId));
        Assert.Equal(1, await CountAuditsAsync(s, draftId, "IndividualCard.NewVersionCreated"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId)));
        Assert.Contains("новая версия уже создана", ex.Message);
        Assert.Equal(1, await s.Db.IndividualCards.CountAsync(c => c.SupersedesIndividualCardId == draftId));
        Assert.Equal(1, await CountAuditsAsync(s, draftId, "IndividualCard.NewVersionCreated"));
        _ = first;
    }

    // ── 14–16: comparison content ─────────────────────────────────────────

    [Fact]
    public async Task Comparison_ReportsCompositionAndHKChanges()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, modelId, aggregateId, nodeId, nodeHKCardId, aggregateHKCardId) = await CreateFormedCardAsync(s);

        // Change the node quantity in the aggregate composition.
        var trackedAC = await s.Db.AggregateCompositions.FirstAsync(ac => ac.AggregateId == aggregateId);
        var trackedNode = await s.Db.AggregateCompositionNodes
            .FirstAsync(n => n.AggregateCompositionId == trackedAC.Id && n.NodeId == nodeId);
        trackedNode.Quantity = 3;
        await s.Db.SaveChangesAsync();

        // Replace the node HK in the chain: new approved HK for the same node.
        var newNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await RemoveComponentAsync(s, aggregateHKCardId, nodeHKCardId);
        await AddComponentAsync(s, aggregateHKCardId, newNodeHK.Id);

        var comparison = await s.IndividualCards.BuildNewVersionComparisonAsync(
            new IndividualCardVersionPreflightRequest(draftId));

        Assert.Contains(comparison.CompositionChanges, c => c.State == "Changed" && c.What == "Узел" && c.Before == "×1" && c.After == "×3");

        // HK diff is keyed by the tree position: the same node position has a
        // changed source HK (code/version), root/aggregate stay unchanged.
        Assert.Contains(comparison.HKChanges, c => c.State == "Changed" && c.What == "Узел" && c.Before!.StartsWith("HK-Nod") && c.After!.StartsWith("HK-Nod") && c.Before != c.After);
        Assert.Contains(comparison.HKChanges, c => c.State == "Unchanged" && c.What == "Агрегат");
        Assert.Contains(comparison.HKChanges, c => c.State == "Unchanged" && c.What == "Изделие");
    }

    [Fact]
    public async Task Comparison_ShowsPreviousCoefficients_DoesNotCopyThem()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Сезонный-" + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Зимняя", 1.1m);
        var (draftId, _, _, _, _, _) = await CreateFormedCardWithCoefficientAsync(s, coefficientId);

        var comparison = await s.IndividualCards.BuildNewVersionComparisonAsync(
            new IndividualCardVersionPreflightRequest(draftId));
        Assert.Contains(comparison.PreviousCoefficients, c => c.SourceCoefficientId == coefficientId && c.Value == 1.1m);

        var created = await s.IndividualCards.CreateNewVersionAsync(
            new CreateIndividualCardVersionRequest(draftId));
        Assert.NotEmpty(created.HKSources);
        Assert.Equal(0, await s.Db.IndividualCardCoefficientSnapshots.CountAsync(x => x.IndividualCardId == created.Id));
        Assert.Equal(0, await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == created.Id));
    }

    [Fact]
    public async Task Comparison_PrimaryTotals_PerBrand_NoGrandTotal()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var primaryA = await CreateGsmMaterialAsync(s, "Масло А", "ГОСТ-А");
        var primaryB = await CreateGsmMaterialAsync(s, "Масло Б", "ГОСТ-Б");
        var duplicate = await CreateGsmMaterialAsync(s, "Масло В", "ГОСТ-В");

        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregateId, 1));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, 1));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г",
            new[] { (primaryA, GsmCategory.Primary), (primaryB, GsmCategory.Primary), (duplicate, GsmCategory.Duplicate) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));

        var comparison = await s.IndividualCards.BuildNewVersionComparisonAsync(
            new IndividualCardVersionPreflightRequest(draft.Id));

        Assert.Equal(2, comparison.PreviousPrimaryTotals.Count);
        Assert.All(comparison.PreviousPrimaryTotals, t => Assert.Equal(100m, t.TotalVolume));
        Assert.Equal(100m, (await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == draft.Id)).TotalNorm);
    }

    [Fact]
    public async Task Archive_RequiresPermissionAndBranchScope()
    {
        await using var s = Scope();
        var author = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);
        SetUser(s, author);

        await DenyAsync(s, author, PermissionCodes.IndividualCardArchive);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.ArchiveIndividualCardAsync(draftId));

        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        SetUser(s, foreign);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.ArchiveIndividualCardAsync(draftId));

        Assert.Equal(0, await CountAuditsAsync(s, draftId, "IndividualCard.Archived"));
    }

    [Fact]
    public async Task Archive_OnlyFormedToArchived()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        // A plain Draft cannot be archived.
        var (_, draftModelId, _, _, _, _) = await CreateFormedCardAsync(s, form: false);
        var plainDraft = await s.Db.IndividualCards.AsNoTracking()
            .FirstAsync(c => c.EquipmentModelId == draftModelId && c.Status == IndividualCardStatus.Draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.ArchiveIndividualCardAsync(plainDraft.Id));

        // Archive once, then archived cannot be archived again.
        await s.IndividualCards.ArchiveIndividualCardAsync(draftId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.ArchiveIndividualCardAsync(draftId));
        Assert.Equal(1, await CountAuditsAsync(s, draftId, "IndividualCard.Archived"));
    }

    [Fact]
    public async Task Archive_PreservesSnapshotsAndCalculation_Exactly()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);
        var source = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == draftId);
        var before = new
        {
            Compositions = await s.Db.IndividualCardCompositionSnapshots.CountAsync(x => x.IndividualCardId == draftId),
            HKSources = await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.IndividualCardId == draftId),
            Gaps = await s.Db.IndividualCardNormativeGapSnapshots.CountAsync(x => x.IndividualCardId == draftId),
            Items = await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == draftId),
            Materials = await s.Db.IndividualCardItemMaterialSnapshots
                .Join(s.Db.IndividualCardItems, m => m.IndividualCardItemId, i => i.Id, (m, i) => new { m, i })
                .CountAsync(x => x.i.IndividualCardId == draftId),
            Coefficients = await s.Db.IndividualCardCoefficientSnapshots.CountAsync(x => x.IndividualCardId == draftId),
            TotalNorm = source.TotalNorm,
            Code = source.Code,
            Version = source.Version,
            Revision = source.RevisionNumber,
        };

        await s.IndividualCards.ArchiveIndividualCardAsync(draftId);

        var after = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == draftId);
        Assert.Equal(before.Compositions, await s.Db.IndividualCardCompositionSnapshots.CountAsync(x => x.IndividualCardId == draftId));
        Assert.Equal(before.HKSources, await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.IndividualCardId == draftId));
        Assert.Equal(before.Gaps, await s.Db.IndividualCardNormativeGapSnapshots.CountAsync(x => x.IndividualCardId == draftId));
        Assert.Equal(before.Items, await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == draftId));
        Assert.Equal(before.Materials, await s.Db.IndividualCardItemMaterialSnapshots
            .Join(s.Db.IndividualCardItems, m => m.IndividualCardItemId, i => i.Id, (m, i) => new { m, i })
            .CountAsync(x => x.i.IndividualCardId == draftId));
        Assert.Equal(before.Coefficients, await s.Db.IndividualCardCoefficientSnapshots.CountAsync(x => x.IndividualCardId == draftId));
        Assert.Equal(before.TotalNorm, after.TotalNorm);
        Assert.Equal(before.Code, after.Code);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Revision, after.RevisionNumber);
    }

    [Fact]
    public async Task Archive_SetsFieldsAndAuditOnce()
    {
        await using var s = Scope();
        var author = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        var (draftId, _, _, _, _, _) = await CreateFormedCardAsync(s);
        SetUser(s, author);

        await s.IndividualCards.ArchiveIndividualCardAsync(draftId);

        var card = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == draftId);
        Assert.Equal(IndividualCardStatus.Archived, card.Status);
        Assert.NotNull(card.ArchivedAt);
        Assert.Equal(author.Id, card.ArchivedByUserId);
        Assert.Equal(DateTime.UtcNow, card.ArchivedAt!.Value, TimeSpan.FromSeconds(5));
        Assert.Equal(1, await CountAuditsAsync(s, draftId, "IndividualCard.Archived"));
    }

    // ── 21: archived is immutable ─────────────────────────────────────────

    [Fact]
    public async Task ArchivedCard_CannotBeModifiedByAnyD4D3Command()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, modelId, _, _, _, _) = await CreateFormedCardAsync(s);
        await s.IndividualCards.ArchiveIndividualCardAsync(draftId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draftId, Array.Empty<Guid>())));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RefreshDraftSourcesAsync(
                new RefreshIndividualCardDraftSourcesRequest(draftId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.DeleteDraftAsync(draftId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draftId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(
                new CreateIndividualCardVersionRequest(draftId)));
        Assert.Equal(1, await CountAuditsAsync(s, draftId, "IndividualCard.Archived"));
        _ = modelId;
    }

    private async Task<(Guid DraftId, Guid ModelId, Guid AggregateId, Guid NodeId, Guid NodeHKCardId, Guid AggregateHKCardId)>
        CreateFormedCardWithCoefficientAsync(TestScope s, Guid coefficientId)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        await CreateProductCompositionAsync(s, modelId, (aggregateId, 1));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, 1));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, new[] { coefficientId }));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        return (draft.Id, modelId, aggregateId, nodeId, nodeHK.Id, aggregateHK.Id);
    }
}

/// <summary>Adapter: CreateIndividualCardVersionRequest record constructor kept
/// terse in assertions above.</summary>
public sealed record CreateIndividualCardDraftWithVersionRequestAdapter(Guid SourceId)
{
    public static implicit operator CreateIndividualCardVersionRequest(
        CreateIndividualCardDraftWithVersionRequestAdapter adapter) =>
        new(adapter.SourceId);
}
