using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardRegistryIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardRegistryIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

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

    private async Task<Guid> CreateEquipmentAsync(TestScope s, string? index = null)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = index ?? ("EM-" + Suffix()), Name = "Изделие " + Suffix(), IsDeleted = false };
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

    private async Task<Guid> CreateComplexAsync(TestScope s)
    {
        var complex = new Complex { Id = Guid.NewGuid(), Code = "C-" + Suffix(), Name = "Комплекс " + Suffix(), IsDeleted = false };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();
        return complex.Id;
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

    /// <summary>Full chain + a calculated Draft (formed on demand).</summary>
    private async Task<(Guid DraftId, Guid ModelId, Guid AggregateId, Guid NodeId, Guid NodeHKCardId, Guid AggregateHKCardId)>
        CreateCardAsync(TestScope s, string actorBranch = "A", bool formed = false,
            bool calculated = true, string? modelIndex = null, bool gaps = false)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var branchId = actorBranch == "A" ? _fixture.BranchA : _fixture.BranchB;
        var modelId = await CreateEquipmentAsync(s, modelIndex);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        await CreateProductCompositionAsync(s, modelId, (aggregateId, 1));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, 1));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, branchId);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, branchId);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, branchId);
        await AddComponentAsync(s, modelHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        if (gaps)
        {
            // A required node without HK items: D4 problem snapshot, no D3 gap.
            var emptyNodeId = await CreateNodeAsync(s);
            var emptyNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, emptyNodeId, branchId);
            await AddComponentAsync(s, aggregateHK.Id, emptyNodeHK.Id);
            var trackedAC = await s.Db.AggregateCompositions.FirstAsync(ac => ac.AggregateId == aggregateId);
            s.Db.AggregateCompositionNodes.Add(new AggregateCompositionNode
            {
                Id = Guid.NewGuid(),
                AggregateCompositionId = trackedAC.Id,
                NodeId = emptyNodeId,
                Quantity = 1,
                SortOrder = 2,
            });
            await s.Db.SaveChangesAsync();
        }

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        if (calculated)
            await s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        if (formed)
            await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        return (draft.Id, modelId, aggregateId, nodeId, nodeHK.Id, aggregateHK.Id);
    }

    // ── 1: server-side filter/sort/paging ─────────────────────────────────

    [Fact]
    public async Task Registry_FilterSortPaging()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (_, _, _, _, _, _) = await CreateCardAsync(s, formed: true);
        var (_, modelB, _, _, _, _) = await CreateCardAsync(s);
        var (_, _, _, _, _, _) = await CreateCardAsync(s);

        // Status filter + paging.
        var page1 = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(Status: IndividualCardStatus.Formed, Page: 1, PageSize: 1));
        Assert.Equal(1, page1.Items.Count);
        Assert.True(page1.TotalCount >= 1);
        Assert.All(page1.Items, i => Assert.Equal(IndividualCardStatus.Formed, i.Status));

        var page2 = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(Page: 2, PageSize: 1));
        Assert.Single(page2.Items);
        Assert.NotEqual(page1.Items[0].Id, page2.Items[0].Id);

        // ObjectLevel filter.
        var models = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(ObjectLevel: IndividualCardObjectLevel.EquipmentModel));
        Assert.Contains(models.Items, i => i.ObjectCode == "EM-" || i.ObjectCode.StartsWith("EM-"));

        // Sort by Code ascending.
        var sorted = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(SortBy: "Code", SortDescending: false, PageSize: 50));
        var codes = sorted.Items.Select(i => i.Code).ToList();
        Assert.Equal(codes.OrderBy(c => c).ToList(), codes);
        _ = modelB;
    }

    // ── 2, 11, 14: branch scope and permissions ───────────────────────────

    [Fact]
    public async Task Registry_BranchScope_NonAdmin_CannotSeeForeignBranch()
    {
        await using var s = Scope();
        var (draftA, _, _, _, _, _) = await CreateCardAsync(s, actorBranch: "A");
        var (draftB, _, _, _, _, _) = await CreateCardAsync(s, actorBranch: "B");
        var userA = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        SetUser(s, userA);

        var registry = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery());
        Assert.Contains(registry.Items, i => i.Id == draftA);
        Assert.DoesNotContain(registry.Items, i => i.Id == draftB);

        // A non-admin UI branch filter must never expand access.
        var forcedB = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(BranchId: _fixture.BranchB));
        Assert.DoesNotContain(forcedB.Items, i => i.Id == draftB);

        // Foreign detail/history are hidden.
        Assert.Null(await s.IndividualCards.GetDetailAsync(draftB));
        Assert.Empty(await s.IndividualCards.GetHistoryAsync(draftB));
    }

    [Fact]
    public async Task Registry_RequiresView()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateCardAsync(s);

        var viewer = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        SetUser(s, viewer);
        await DenyAsync(s, viewer, PermissionCodes.IndividualCardView);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.GetDetailAsync(draftId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.GetHistoryAsync(draftId));
    }

    // ── 3: search matches code/version/context ────────────────────────────

    [Fact]
    public async Task Registry_Search_MatchesCodeVersionAndObjectContext()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (_, modelId, _, _, _, _) = await CreateCardAsync(s, calculated: false, formed: false);
        var modelIndex = (await s.Db.EquipmentModels.AsNoTracking().FirstAsync(m => m.Id == modelId)).Index;

        var byCode = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(SearchText: modelIndex));
        Assert.Single(byCode.Items);
        Assert.Equal(modelIndex, byCode.Items[0].ObjectCode);

        var card = byCode.Items[0];
        var byVersion = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(SearchText: card.Version));
        Assert.Contains(byVersion.Items, i => i.Id == card.Id);

        // Composition context: the composition target is the model itself.
        var byObjectName = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(SearchText: card.ObjectName[..Math.Min(6, card.ObjectName.Length)]));
        Assert.Contains(byObjectName.Items, i => i.Id == card.Id);
    }

    // ── 4: OnlyMine ───────────────────────────────────────────────────────

    [Fact]
    public async Task Registry_OnlyMine_FiltersByAuthor()
    {
        await using var s = Scope();
        var author = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        SetUser(s, author);
        var (_, modelA, _, _, _, _) = await CreateCardAsync(s, calculated: false, formed: false);
        // The helper switches to SystemAdmin; create the second card as author.
        SetUser(s, author);
        var modelId2 = await CreateEquipmentAsync(s);
        var aggregateId2 = await CreateAggregateAsync(s);
        var nodeId2 = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId2, (aggregateId2, 1));
        await CreateAggregateCompositionAsync(s, aggregateId2, (nodeId2, 1));
        var modelHK2 = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId2, _fixture.BranchA);
        var aggregateHK2 = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId2, _fixture.BranchA);
        var nodeHK2 = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId2, _fixture.BranchA);
        await AddComponentAsync(s, modelHK2.Id, aggregateHK2.Id);
        await AddComponentAsync(s, aggregateHK2.Id, nodeHK2.Id);
        var secondDraft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId2));
        SetUser(s, _fixture.SystemAdminUser);
        var (_, modelC, _, _, _, _) = await CreateCardAsync(s, calculated: false, formed: false);
        SetUser(s, author);

        var mine = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(OnlyMine: true));
        Assert.Contains(mine.Items, i => i.Id == secondDraft.Id);
        Assert.DoesNotContain(mine.Items, i => i.Id == modelA);
        Assert.DoesNotContain(mine.Items, i => i.Id == modelC);
        Assert.All(mine.Items, i => Assert.Equal(author.Id, i.CreatedByUserId));
    }

    // ── 5: OnlyWithNormativeGaps includes D3 gaps and D4 problems ─────────

    [Fact]
    public async Task Registry_OnlyWithGaps_IncludesD3GapsAndD4Problems()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (_, _, _, _, _, _) = await CreateCardAsync(s, calculated: false, formed: false);
        var (problemCard, _, _, _, _, _) = await CreateCardAsync(s, gaps: true);

        // D3 gaps: a broken second aggregate without node links.
        var (_, model2, _, _, _, _) = await CreateCardAsync(s, calculated: false, formed: false);
        var brokenAggregateId = await CreateAggregateAsync(s);
        var brokenAggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, brokenAggregateId, _fixture.BranchA);
        var modelHK2 = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.EquipmentModelId == model2);
        await AddComponentAsync(s, modelHK2.Id, brokenAggregateHK.Id);
        var draft2 = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.EquipmentModelId == model2);
        await s.IndividualCards.RefreshDraftSourcesAsync(
            new RefreshIndividualCardDraftSourcesRequest(draft2.Id));
        var gapCount = await s.Db.IndividualCardNormativeGapSnapshots.CountAsync(g => g.IndividualCardId == draft2.Id);
        Assert.True(gapCount > 0);

        var filtered = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(OnlyWithNormativeGaps: true));
        Assert.Contains(filtered.Items, i => i.Id == problemCard);
        Assert.Contains(filtered.Items, i => i.Id == draft2.Id);
        Assert.All(filtered.Items, i => Assert.True(i.HasNormativeGaps || i.CalculationProblemCount > 0));
    }

    // ── 6: registry context from snapshots after live rename ──────────────

    [Fact]
    public async Task Registry_UsesSnapshotObject_AfterLiveRename()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var uniqueIndex = "EM-" + Suffix();
        var (_, modelId, _, _, _, _) = await CreateCardAsync(s, formed: true, modelIndex: uniqueIndex);
        var before = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(SearchText: uniqueIndex));
        var row = Assert.Single(before.Items);

        // Rename the live model: the registry must keep showing snapshots.
        var liveModel = await s.Db.EquipmentModels.FirstAsync(m => m.Id == modelId);
        liveModel.Index = "EM-RENAMED-" + Suffix();
        liveModel.Name = "Изделие Переименованное";
        await s.Db.SaveChangesAsync();

        var after = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(SearchText: uniqueIndex));
        var rowAfter = Assert.Single(after.Items);
        Assert.Equal(row.ObjectCode, rowAfter.ObjectCode);
        Assert.Equal(row.ObjectName, rowAfter.ObjectName);
    }

    // ── 7, 13: detail for Formed/Archived ─────────────────────────────────

    [Fact]
    public async Task GetDetail_FormedAndArchived_ReadOnlyGraph()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateCardAsync(s, formed: true);

        var formedDetail = await s.IndividualCards.GetDetailAsync(draftId);
        Assert.NotNull(formedDetail);
        Assert.Equal(IndividualCardStatus.Formed, formedDetail!.Status);
        Assert.NotEmpty(formedDetail.Rows);
        Assert.NotEmpty(formedDetail.HKSources);
        Assert.NotEmpty(formedDetail.Compositions);
        Assert.NotEmpty(formedDetail.History);
        Assert.NotEmpty(formedDetail.Audit);

        await s.IndividualCards.ArchiveIndividualCardAsync(draftId);
        var archivedDetail = await s.IndividualCards.GetDetailAsync(draftId);
        Assert.NotNull(archivedDetail);
        Assert.Equal(IndividualCardStatus.Archived, archivedDetail!.Status);
        Assert.NotNull(archivedDetail.ArchivedAt);
        Assert.Equal(1, archivedDetail.Audit.Count(a => a.Action == "IndividualCard.Archived"));
    }

    // ── 8: detail rows use immutable D4 source fields ─────────────────────

    [Fact]
    public async Task Detail_CalculationSource_ImmutableAfterLiveHKRename()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateCardAsync(s, formed: true);

        var before = await s.IndividualCards.GetDetailAsync(draftId);
        var row = Assert.Single(before!.Rows);
        Assert.False(string.IsNullOrWhiteSpace(row.HKCardCode));

        var liveHK = await s.Db.HKCards.FirstAsync(h => h.Id == row.HKCardId);
        liveHK.Code = "HK-RENAMED-" + Suffix();
        liveHK.Version = "vRENAMED";
        await s.Db.SaveChangesAsync();

        var after = await s.IndividualCards.GetDetailAsync(draftId);
        var rowAfter = Assert.Single(after!.Rows);
        Assert.Equal(row.HKCardCode, rowAfter.HKCardCode);
        Assert.Equal(row.HKCardVersion, rowAfter.HKCardVersion);
    }

    // ── 9: repeated source occurrences stay separate ──────────────────────

    [Fact]
    public async Task Detail_RepeatedSourceOccurrences_SeparateRows()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var complexId = await CreateComplexAsync(s);
        var modelAId = await CreateEquipmentAsync(s);
        var modelBId = await CreateEquipmentAsync(s);
        var sharedAggregate = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ");
        await CreateComplexCompositionAsync(s, complexId, (modelAId, 1), (modelBId, 2));
        await CreateProductCompositionAsync(s, modelAId, (sharedAggregate, 3));
        await CreateProductCompositionAsync(s, modelBId, (sharedAggregate, 3));
        await CreateAggregateCompositionAsync(s, sharedAggregate, (nodeId, 2));
        var complexHK = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);
        var modelAHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelAId, _fixture.BranchA);
        var modelBHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelBId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, sharedAggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, complexHK.Id, modelAHK.Id);
        await AddComponentAsync(s, complexHK.Id, modelBHK.Id);
        await AddComponentAsync(s, modelAHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, modelBHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Complex, complexId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        var detail = await s.IndividualCards.GetDetailAsync(draft.Id);
        Assert.Equal(2, detail!.Rows.Count);
        Assert.Contains(detail.Rows, r => r.ProductQuantity == 1 && r.CalculatedVolume == 600m);
        Assert.Contains(detail.Rows, r => r.ProductQuantity == 2 && r.CalculatedVolume == 1200m);
        _ = modelBId;
    }

    // ── 10: multiple primary brands are alternatives, not a grand total ───

    [Fact]
    public async Task Detail_MultiplePrimary_BrandsAreSeparateNotSummed()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var primaryA = await CreateGsmMaterialAsync(s, "Масло А", "ГОСТ-А");
        var primaryB = await CreateGsmMaterialAsync(s, "Масло Б", "ГОСТ-Б");

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
            new[] { (primaryA, GsmCategory.Primary), (primaryB, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        var detail = await s.IndividualCards.GetDetailAsync(draft.Id);
        Assert.Equal(2, detail!.PrimaryTotals.Count);
        Assert.All(detail.PrimaryTotals, t => Assert.Equal(100m, t.TotalVolume));
        Assert.Equal(100m, detail.TotalNorm);
    }

    // ── 12: version chain ─────────────────────────────────────────────────

    [Fact]
    public async Task History_VersionChain_PredecessorSuccessor()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (draftId, _, _, _, _, _) = await CreateCardAsync(s, formed: true);

        var successor = await s.IndividualCards.CreateNewVersionAsync(
            new CreateIndividualCardVersionRequest(draftId));

        var history = await s.IndividualCards.GetHistoryAsync(draftId);
        Assert.Equal(2, history.Count);
        Assert.Equal(1, history[0].RevisionNumber);
        Assert.Equal(2, history[1].RevisionNumber);
        Assert.Null(history[0].SupersedesIndividualCardId);
        Assert.Equal(draftId, history[1].SupersedesIndividualCardId);

        var detail = await s.IndividualCards.GetDetailAsync(successor.Id);
        Assert.Equal(2, detail!.History.Count);
        Assert.Contains(detail.History, v => v.Id == draftId);
        Assert.Contains(detail.History, v => v.Id == successor.Id);
    }

    // ── Corrective D6 ─────────────────────────────────────────────────────

    [Fact]
    public async Task WizardRoot_RepeatPreflightThenCreate_UsesSelectedRoot()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        await CreateProductCompositionAsync(s, modelId, (aggregateId, 1));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, 1));
        var rootA = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var rootB = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, rootA.Id, aggregateHK.Id);
        await AddComponentAsync(s, rootB.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        // Wizard: initial preflight → SelectionRequired; choosing a root →
        // repeat preflight with that root → ExplicitlySelected.
        var initial = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        Assert.Equal(IndividualCardPreflightRootState.SelectionRequired, initial.RootState);

        var repeated = await s.IndividualCards.BuildPreflightAsync(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.EquipmentModel, modelId, rootB.Id));
        Assert.Equal(IndividualCardPreflightRootState.ExplicitlySelected, repeated.RootState);
        Assert.NotNull(repeated.SelectedRoot);
        Assert.Equal(rootB.Id, repeated.SelectedRoot!.HKCardId);

        // Create with the selected root: root snapshot points to it.
        var created = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(
                IndividualCardObjectLevel.EquipmentModel, modelId, rootB.Id));
        var rootSource = created.HKSources.Single(h => h.ParentHKSourceSnapshotId == null);
        Assert.Equal(rootB.Id, rootSource.SourceHKCardId);
    }

    [Fact]
    public async Task Registry_PeriodFilter_InclusiveBounds()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var uniqueIndex = "EM-" + Suffix();
        var (draftId, _, _, _, _, _) = await CreateCardAsync(s, calculated: false, formed: false, modelIndex: uniqueIndex);

        // CreatedFrom in the future excludes the card.
        var future = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(
            SearchText: uniqueIndex, CreatedFrom: DateTime.UtcNow.AddDays(1)));
        Assert.Empty(future.Items);

        // CreatedTo yesterday excludes the card.
        var past = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(
            SearchText: uniqueIndex, CreatedTo: DateTime.UtcNow.AddDays(-1)));
        Assert.Empty(past.Items);

        // Same From/To day (next-day exclusive bound) includes the card.
        var today = await s.IndividualCards.GetRegistryAsync(new IndividualCardRegistryQuery(
            SearchText: uniqueIndex,
            CreatedFrom: DateTime.UtcNow.Date,
            CreatedTo: DateTime.UtcNow.Date.AddDays(1)));
        Assert.Contains(today.Items, i => i.Id == draftId);
    }

    [Fact]
    public async Task TargetSnapshots_InstanceCard_ImmutableAfterLiveRename()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s, "EM-" + Suffix());
        var model = await s.Db.EquipmentModels.AsNoTracking().FirstAsync(m => m.Id == modelId);
        var instance = new EquipmentInstance
        {
            Id = Guid.NewGuid(),
            SerialNumber = "SN-" + Suffix(),
            Index = model.Index,
            Name = "Экземпляр " + Suffix(),
            EquipmentModelId = modelId,
            IsDeleted = false,
        };
        s.Db.EquipmentInstances.Add(instance);
        await s.Db.SaveChangesAsync();
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
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentInstance, instance.Id));

        var registryBefore = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(SearchText: instance.SerialNumber));
        var row = Assert.Single(registryBefore.Items);
        Assert.Equal(instance.SerialNumber, row.ObjectCode);
        Assert.Equal($"Изделие {model.Index}", row.ContextText);

        var detailBefore = await s.IndividualCards.GetDetailAsync(draft.Id);
        Assert.Equal(instance.SerialNumber, detailBefore!.ObjectCode);

        // Rename the live instance: snapshot display must not change.
        var originalSerial = instance.SerialNumber;
        var originalContext = row.ContextText;
        var liveInstance = await s.Db.EquipmentInstances.FirstAsync(i => i.Id == instance.Id);
        liveInstance.SerialNumber = "SN-RENAMED-" + Suffix();
        liveInstance.Name = "Экземпляр Переименованный";
        liveInstance.Index = "EM-RENAMED-" + Suffix();
        await s.Db.SaveChangesAsync();

        var registryAfter = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(SearchText: originalSerial));
        var rowAfter = Assert.Single(registryAfter.Items);
        Assert.Equal(row.ObjectCode, rowAfter.ObjectCode);
        Assert.Equal(row.ObjectName, rowAfter.ObjectName);
        Assert.Equal(originalContext, rowAfter.ContextText);

        var detailAfter = await s.IndividualCards.GetDetailAsync(draft.Id);
        Assert.Equal(detailBefore!.ObjectCode, detailAfter!.ObjectCode);
        Assert.Equal(detailBefore.ObjectName, detailAfter.ObjectName);
        Assert.Equal(detailBefore.ContextText, detailAfter.ContextText);
    }

    [Fact]
    public async Task TargetSnapshots_AllLevels_ImmutableAfterLiveRename()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);

        // Node-level card.
        var nodeId = await CreateNodeAsync(s);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        var nodeDraft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        // Aggregate-level card.
        var aggregateId = await CreateAggregateAsync(s);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var aggregateDraft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Aggregate, aggregateId));

        // Model-level card.
        var modelId = await CreateEquipmentAsync(s);
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var modelDraft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        // Complex-level card with a model item (context from preflight).
        var complexId = await CreateComplexAsync(s);
        await CreateComplexCompositionAsync(s, complexId, (modelId, 1));
        // The model needs a resolvable composition for the complex preflight.
        var modelAggregate = await CreateAggregateAsync(s);
        var modelNode = await CreateNodeAsync(s);
        await CreateProductCompositionAsync(s, modelId, (modelAggregate, 1));
        await CreateAggregateCompositionAsync(s, modelAggregate, (modelNode, 1));
        var complexHK = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);
        var modelHKForComplex = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var modelAggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, modelAggregate, _fixture.BranchA);
        var modelNodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, modelNode, _fixture.BranchA);
        await AddComponentAsync(s, complexHK.Id, modelHKForComplex.Id);
        await AddComponentAsync(s, modelHKForComplex.Id, modelAggregateHK.Id);
        await AddComponentAsync(s, modelAggregateHK.Id, modelNodeHK.Id);
        var complexDraft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Complex, complexId));

        // Rename all live target rows.
        (await s.Db.Nodes.FirstAsync(n => n.Id == nodeId)).Name = "Узел Переименованный";
        (await s.Db.Aggregates.FirstAsync(a => a.Id == aggregateId)).Name = "Агрегат Переименованный";
        (await s.Db.Aggregates.FirstAsync(a => a.Id == aggregateId)).Code = "A-RENAMED-" + Suffix();
        (await s.Db.EquipmentModels.FirstAsync(m => m.Id == modelId)).Name = "Изделие Переименованное";
        (await s.Db.EquipmentModels.FirstAsync(m => m.Id == modelId)).Index = "EM-RENAMED-" + Suffix();
        (await s.Db.Complexes.FirstAsync(x => x.Id == complexId)).Name = "Комплекс Переименованный";
        await s.Db.SaveChangesAsync();

        var nodeDetail = await s.IndividualCards.GetDetailAsync(nodeDraft.Id);
        var nodeRow = (await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(SearchText: nodeDraft.Code))).Items.Single(i => i.Id == nodeDraft.Id);
        Assert.NotEqual("Узел Переименованный", nodeDetail!.ObjectName);
        Assert.Equal(nodeDetail.ObjectName, nodeRow.ObjectName);

        var aggregateDetail = await s.IndividualCards.GetDetailAsync(aggregateDraft.Id);
        Assert.NotEqual("Агрегат Переименованный", aggregateDetail!.ObjectName);
        Assert.NotEqual("Агрегат Переименованный", aggregateDetail.ObjectCode);

        var modelDetail = await s.IndividualCards.GetDetailAsync(modelDraft.Id);
        Assert.NotEqual("Изделие Переименованное", modelDetail!.ObjectName);
        Assert.NotEqual("EM-RENAMED", modelDetail.ObjectCode[..Math.Min(10, modelDetail.ObjectCode.Length)]);

        var complexDetail = await s.IndividualCards.GetDetailAsync(complexDraft.Id);
        Assert.NotEqual("Комплекс Переименованный", complexDetail!.ObjectName);
        // Complex context is an immutable snapshot ("Изделие {oldIndex}").
        Assert.StartsWith("Изделие ", complexDetail.ContextText ?? string.Empty);
        Assert.DoesNotContain("EM-RENAMED", complexDetail.ContextText ?? string.Empty);
    }
}
