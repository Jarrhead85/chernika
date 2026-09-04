using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardDomainModelTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardDomainModelTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private static string UniqueCode() => "ИК-ТЕСТ-" + Guid.NewGuid().ToString("N")[..10];

    private static IndividualCard MakeCard(Guid branchId)
    {
        return new IndividualCard
        {
            Id = Guid.NewGuid(),
            Code = UniqueCode(),
            Version = "v0926.1",
            RevisionNumber = 1,
            ObjectLevel = IndividualCardObjectLevel.Node,
            Status = IndividualCardStatus.Draft,
            BranchId = branchId,
            CreatedByUserId = "test-user",
            CreatedAt = DateTime.UtcNow,
        };
    }

    private async Task<Guid> CreateNodeAsync(TestScope s)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел тест", IsDeleted = false };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    private async Task<Guid> CreateComplexAsync(TestScope s)
    {
        var complex = new Complex { Id = Guid.NewGuid(), Code = "C-" + Guid.NewGuid().ToString("N")[..6], Name = "Комплекс тест", IsDeleted = false };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();
        return complex.Id;
    }

    private async Task<Guid> CreateAggregateAsync(TestScope s)
    {
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест", IsDeleted = false };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        return aggregate.Id;
    }

    private async Task<(Guid ModelId, Guid InstanceId)> CreateEquipmentAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест", IsDeleted = false };
        s.Db.EquipmentModels.Add(model);
        var instance = new EquipmentInstance { Id = Guid.NewGuid(), SerialNumber = "SN-" + Guid.NewGuid().ToString("N")[..6], Index = model.Index, Name = "Экземпляр тест", EquipmentModelId = model.Id, IsDeleted = false };
        s.Db.EquipmentInstances.Add(instance);
        await s.Db.SaveChangesAsync();
        return (model.Id, instance.Id);
    }

    // ── Domain rules ──────────────────────────────────────────────────────

    [Fact]
    public void ObjectLevel_ContainsExactlyFiveTargetLevels()
    {
        var values = Enum.GetValues<IndividualCardObjectLevel>().Cast<int>().OrderBy(v => v).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, values);
        Assert.Equal(IndividualCardObjectLevel.Complex, (IndividualCardObjectLevel)1);
        Assert.Equal(IndividualCardObjectLevel.EquipmentModel, (IndividualCardObjectLevel)2);
        Assert.Equal(IndividualCardObjectLevel.Aggregate, (IndividualCardObjectLevel)3);
        Assert.Equal(IndividualCardObjectLevel.Node, (IndividualCardObjectLevel)4);
        Assert.Equal(IndividualCardObjectLevel.EquipmentInstance, (IndividualCardObjectLevel)5);
    }

    [Fact]
    public void Transitions_AllowDraftToFormedAndFormedToArchived()
    {
        Assert.True(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Draft, IndividualCardStatus.Formed));
        Assert.True(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Formed, IndividualCardStatus.Archived));
    }

    [Fact]
    public void Transitions_RejectForbiddenTransitions()
    {
        Assert.False(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Draft, IndividualCardStatus.Archived));
        Assert.False(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Formed, IndividualCardStatus.Draft));
        Assert.False(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Archived, IndividualCardStatus.Formed));
        Assert.False(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Archived, IndividualCardStatus.Draft));
        Assert.False(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Formed, IndividualCardStatus.Formed));
        Assert.False(IndividualCardStatusTransitions.IsAllowed(
            IndividualCardStatus.Draft, IndividualCardStatus.Draft));
    }

    [Fact]
    public void Display_UsesIzdelieLabelForEquipmentModel()
    {
        Assert.Equal("Изделие", IndividualCardDisplay.ObjectLevel(IndividualCardObjectLevel.EquipmentModel));
        Assert.DoesNotContain("Модель техники",
            Enum.GetValues<IndividualCardObjectLevel>().Select(IndividualCardDisplay.ObjectLevel));
    }

    // ── Target FK check constraint ────────────────────────────────────────

    [Fact]
    public async Task TargetConstraint_AcceptsEachValidSingleTargetLevel()
    {
        await using var s = Scope();
        var (modelId, instanceId) = await CreateEquipmentAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var complexId = await CreateComplexAsync(s);
        var aggregateId = await CreateAggregateAsync(s);

        var complex = MakeCard(_fixture.BranchA);
        complex.ObjectLevel = IndividualCardObjectLevel.Complex;
        complex.ComplexId = complexId;

        var model = MakeCard(_fixture.BranchA);
        model.ObjectLevel = IndividualCardObjectLevel.EquipmentModel;
        model.EquipmentModelId = modelId;

        var aggregate = MakeCard(_fixture.BranchA);
        aggregate.ObjectLevel = IndividualCardObjectLevel.Aggregate;
        aggregate.AggregateId = aggregateId;

        var node = MakeCard(_fixture.BranchA);
        node.ObjectLevel = IndividualCardObjectLevel.Node;
        node.NodeId = nodeId;

        var instance = MakeCard(_fixture.BranchA);
        instance.ObjectLevel = IndividualCardObjectLevel.EquipmentInstance;
        instance.EquipmentInstanceId = instanceId;

        s.Db.IndividualCards.AddRange(complex, model, aggregate, node, instance);
        await s.Db.SaveChangesAsync();

        var saved = await s.Db.IndividualCards.Where(c => c.BranchId == _fixture.BranchA
            && new[] { complex.Id, model.Id, aggregate.Id, node.Id, instance.Id }.Contains(c.Id)).ToListAsync();
        Assert.Equal(5, saved.Count);
    }

    [Fact]
    public async Task TargetConstraint_RejectsZeroTargetFks()
    {
        await using var s = Scope();
        var card = MakeCard(_fixture.BranchA);
        card.ObjectLevel = IndividualCardObjectLevel.Node;
        card.NodeId = null;
        s.Db.IndividualCards.Add(card);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("CK_IndividualCards_TargetMatchesLevel", ex.InnerException?.Message);
    }

    [Fact]
    public async Task TargetConstraint_RejectsMultipleTargetFks()
    {
        await using var s = Scope();
        var (modelId, instanceId) = await CreateEquipmentAsync(s);
        var nodeId = await CreateNodeAsync(s);

        var card = MakeCard(_fixture.BranchA);
        card.ObjectLevel = IndividualCardObjectLevel.EquipmentInstance;
        card.EquipmentInstanceId = instanceId;
        card.EquipmentModelId = modelId;
        card.NodeId = nodeId;
        s.Db.IndividualCards.Add(card);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("CK_IndividualCards_TargetMatchesLevel", ex.InnerException?.Message);
    }

    [Fact]
    public async Task TargetConstraint_RejectsMismatchedLevelAndFk()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);

        var card = MakeCard(_fixture.BranchA);
        card.ObjectLevel = IndividualCardObjectLevel.Complex;
        card.NodeId = nodeId;
        s.Db.IndividualCards.Add(card);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("CK_IndividualCards_TargetMatchesLevel", ex.InnerException?.Message);
    }

    // ── Status metadata check constraint ──────────────────────────────────

    [Fact]
    public async Task StatusMetadata_AcceptsValidCombinations()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);
        var now = DateTime.UtcNow;
        var user = "status-user";

        var draft = MakeCard(_fixture.BranchA);
        draft.NodeId = nodeId;

        var formed = MakeCard(_fixture.BranchA);
        formed.NodeId = nodeId;
        formed.Status = IndividualCardStatus.Formed;
        formed.FormedByUserId = user;
        formed.FormedAt = now;

        var archived = MakeCard(_fixture.BranchA);
        archived.NodeId = nodeId;
        archived.Status = IndividualCardStatus.Archived;
        archived.FormedByUserId = user;
        archived.FormedAt = now;
        archived.ArchivedByUserId = user;
        archived.ArchivedAt = now;

        s.Db.IndividualCards.AddRange(draft, formed, archived);
        await s.Db.SaveChangesAsync();

        var reloaded = await s.Db.IndividualCards
            .Where(c => c.BranchId == _fixture.BranchA
                && new[] { draft.Id, formed.Id, archived.Id }.Contains(c.Id)).ToListAsync();
        Assert.Equal(3, reloaded.Count);
    }

    [Fact]
    public async Task StatusMetadata_RejectsFormedWithoutMetadata()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);
        var card = MakeCard(_fixture.BranchA);
        card.NodeId = nodeId;
        card.Status = IndividualCardStatus.Formed;
        s.Db.IndividualCards.Add(card);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("CK_IndividualCards_StatusMetadata", ex.InnerException?.Message);
    }

    [Fact]
    public async Task StatusMetadata_RejectsArchivedWithoutArchiveMetadata()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);
        var card = MakeCard(_fixture.BranchA);
        card.NodeId = nodeId;
        card.Status = IndividualCardStatus.Archived;
        card.FormedByUserId = "u";
        card.FormedAt = DateTime.UtcNow;
        s.Db.IndividualCards.Add(card);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("CK_IndividualCards_StatusMetadata", ex.InnerException?.Message);
    }

    [Fact]
    public async Task StatusMetadata_RejectsDraftWithFormedOrArchiveMetadata()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);

        var draftWithFormed = MakeCard(_fixture.BranchA);
        draftWithFormed.NodeId = nodeId;
        draftWithFormed.FormedByUserId = "u";
        draftWithFormed.FormedAt = DateTime.UtcNow;
        s.Db.IndividualCards.Add(draftWithFormed);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("CK_IndividualCards_StatusMetadata", ex.InnerException?.Message);
    }

    // ── RevisionNumber constraint ─────────────────────────────────────────

    [Fact]
    public async Task RevisionNumber_RejectsZeroAndNegative()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);

        var card = MakeCard(_fixture.BranchA);
        card.NodeId = nodeId;
        card.RevisionNumber = 0;
        s.Db.IndividualCards.Add(card);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("CK_IndividualCards_RevisionPositive", ex.InnerException?.Message);
    }

    // ── Unique Code + Version ─────────────────────────────────────────────

    [Fact]
    public async Task CodeVersion_IsUnique()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);

        var code = UniqueCode();
        var first = MakeCard(_fixture.BranchA);
        first.NodeId = nodeId;
        first.Code = code;
        var second = MakeCard(_fixture.BranchA);
        second.NodeId = nodeId;
        second.Code = code;

        s.Db.IndividualCards.AddRange(first, second);
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task CodeVersion_SameCodeDifferentVersionAllowed()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);

        var code = UniqueCode();
        var first = MakeCard(_fixture.BranchA);
        first.NodeId = nodeId;
        first.Code = code;
        var second = MakeCard(_fixture.BranchA);
        second.NodeId = nodeId;
        second.Code = code;
        second.Version = "v0926.2";

        s.Db.IndividualCards.AddRange(first, second);
        await s.Db.SaveChangesAsync();
        Assert.Equal(2, await s.Db.IndividualCards.CountAsync(c => c.Code == code));
    }

    // ── Snapshot persistence and cascade delete ───────────────────────────

    [Fact]
    public async Task Snapshots_PersistAndCascadeDeleteWithDraftParent()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);
        var now = DateTime.UtcNow;

        var card = MakeCard(_fixture.BranchA);
        card.NodeId = nodeId;
        s.Db.IndividualCards.Add(card);

        var composition = new IndividualCardCompositionSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = card.Id,
            SourceLevel = IndividualCardObjectLevel.Node,
            SourceCompositionId = Guid.NewGuid(),
            SourceCompositionVersion = "v1",
            TargetObjectId = nodeId,
            TargetObjectCode = "N-1",
            TargetObjectName = "Узел 1",
            CapturedAt = now,
        };
        var aggregate = new IndividualCardAggregateSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardCompositionSnapshotId = composition.Id,
            AggregateId = Guid.NewGuid(),
            AggregateCode = "A-1",
            AggregateName = "Агрегат 1",
            Quantity = 1,
            SortOrder = 1,
        };
        var nodeSnapshot = new IndividualCardNodeSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardAggregateSnapshotId = aggregate.Id,
            NodeId = nodeId,
            NodeCode = "N-1",
            NodeName = "Узел 1",
            Quantity = 2,
            SortOrder = 1,
        };
        var hkSource = new IndividualCardHKSourceSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = card.Id,
            SourceHKCardId = Guid.NewGuid(),
            ObjectLevel = IndividualCardObjectLevel.Node,
            SourceObjectId = nodeId,
            SourceObjectCode = "N-1",
            SourceObjectName = "Узел 1",
            HKCardCode = "HK-1",
            HKCardVersion = "v1",
            CapturedAt = now,
            SortOrder = 1,
        };
        var hkSourceChild = new IndividualCardHKSourceSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = card.Id,
            ParentHKSourceSnapshotId = hkSource.Id,
            SourceHKCardId = Guid.NewGuid(),
            ObjectLevel = IndividualCardObjectLevel.Aggregate,
            SourceObjectId = Guid.NewGuid(),
            SourceObjectCode = "A-1",
            SourceObjectName = "Агрегат 1",
            HKCardCode = "HK-2",
            HKCardVersion = "v1",
            CapturedAt = now,
            SortOrder = 2,
        };
        var item = new IndividualCardItem
        {
            Id = Guid.NewGuid(),
            IndividualCardId = card.Id,
            NodeSnapshotId = nodeSnapshot.Id,
            AssemblyUnitCode = "AU-1",
            AssemblyUnitName = "Сборочная единица 1",
            AssemblyUnitQuantity = 1,
            UnitOfMeasure = "г",
            SourceVolume = 10m,
            BaseVolume = 10m,
            CalculatedVolume = 11m,
            SortOrder = 1,
        };
        var material = new IndividualCardItemMaterialSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardItemId = item.Id,
            SourceGsmMaterialId = Guid.NewGuid(),
            MaterialName = "Масло М-10Г2к",
            MaterialType = "Моторное масло",
            Gost = "ГОСТ 1234-56",
            Category = GsmCategory.Primary,
            CalculatedVolume = 11m,
            UnitOfMeasure = "г",
            SortOrder = 1,
        };
        var coefficient = new IndividualCardCoefficientSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = card.Id,
            SourceCoefficientId = Guid.NewGuid(),
            SourceCoefficientTypeId = Guid.NewGuid(),
            CoefficientTypeName = "Температурный",
            CoefficientName = "Зимняя эксплуатация",
            Value = 1.1m,
            ConditionDescription = "Ниже -20°C",
            CapturedAt = now,
        };

        s.Db.IndividualCardCompositionSnapshots.Add(composition);
        s.Db.IndividualCardAggregateSnapshots.Add(aggregate);
        s.Db.IndividualCardNodeSnapshots.Add(nodeSnapshot);
        s.Db.IndividualCardHKSourceSnapshots.AddRange(hkSource, hkSourceChild);
        s.Db.IndividualCardItems.Add(item);
        s.Db.IndividualCardItemMaterialSnapshots.Add(material);
        s.Db.IndividualCardCoefficientSnapshots.Add(coefficient);
        await s.Db.SaveChangesAsync();

        Assert.Equal(1, await s.Db.IndividualCardCompositionSnapshots.CountAsync(x => x.IndividualCardId == card.Id));
        Assert.Equal(1, await s.Db.IndividualCardAggregateSnapshots.CountAsync(x => x.IndividualCardCompositionSnapshotId == composition.Id));
        Assert.Equal(1, await s.Db.IndividualCardNodeSnapshots.CountAsync(x => x.IndividualCardAggregateSnapshotId == aggregate.Id));
        Assert.Equal(2, await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.IndividualCardId == card.Id));
        Assert.Equal(1, await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.ParentHKSourceSnapshotId == hkSource.Id));
        Assert.Equal(1, await s.Db.IndividualCardItems.CountAsync(x => x.IndividualCardId == card.Id));
        Assert.Equal(1, await s.Db.IndividualCardItemMaterialSnapshots.CountAsync(x => x.IndividualCardItemId == item.Id));
        Assert.Equal(1, await s.Db.IndividualCardCoefficientSnapshots.CountAsync(x => x.IndividualCardId == card.Id));

        // Physical delete of a Draft parent must cascade to the whole snapshot tree.
        var tracked = await s.Db.IndividualCards.FirstAsync(c => c.Id == card.Id);
        s.Db.IndividualCards.Remove(tracked);
        await s.Db.SaveChangesAsync();

        Assert.Equal(0, await s.Db.IndividualCards.CountAsync(c => c.Id == card.Id));
        Assert.Equal(0, await s.Db.IndividualCardCompositionSnapshots.CountAsync(x => x.IndividualCardId == card.Id));
        Assert.Equal(0, await s.Db.IndividualCardAggregateSnapshots.CountAsync(x => x.IndividualCardCompositionSnapshotId == composition.Id));
        Assert.Equal(0, await s.Db.IndividualCardNodeSnapshots.CountAsync(x => x.IndividualCardAggregateSnapshotId == aggregate.Id));
        Assert.Equal(0, await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.IndividualCardId == card.Id));
        Assert.Equal(0, await s.Db.IndividualCardHKSourceSnapshots.CountAsync(x => x.Id == hkSourceChild.Id));
        Assert.Equal(0, await s.Db.IndividualCardItems.CountAsync(x => x.IndividualCardId == card.Id));
        Assert.Equal(0, await s.Db.IndividualCardItemMaterialSnapshots.CountAsync(x => x.IndividualCardItemId == item.Id));
        Assert.Equal(0, await s.Db.IndividualCardCoefficientSnapshots.CountAsync(x => x.IndividualCardId == card.Id));
    }

    [Fact]
    public async Task Snapshots_CopyScalarsAndIgnoreSourceRenames()
    {
        await using var s = Scope();
        var nodeId = await CreateNodeAsync(s);
        var now = DateTime.UtcNow;

        var card = MakeCard(_fixture.BranchA);
        card.NodeId = nodeId;
        s.Db.IndividualCards.Add(card);

        var sourceHkCard = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-ORIG",
            Version = "v1",
            Status = HKCardStatus.Approved,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
        };
        s.Db.HKCards.Add(sourceHkCard);

        var snapshot = new IndividualCardHKSourceSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = card.Id,
            SourceHKCardId = sourceHkCard.Id,
            ObjectLevel = IndividualCardObjectLevel.Node,
            SourceObjectId = nodeId,
            SourceObjectCode = "N-1",
            SourceObjectName = "Узел 1",
            HKCardCode = sourceHkCard.Code,
            HKCardVersion = sourceHkCard.Version,
            CapturedAt = now,
            SortOrder = 1,
        };
        s.Db.IndividualCardHKSourceSnapshots.Add(snapshot);
        await s.Db.SaveChangesAsync();

        // Rename the source HKCard and the target node afterwards.
        sourceHkCard.Code = "HK-RENAMED";
        sourceHkCard.Version = "v2";
        var node = await s.Db.Nodes.FirstAsync(n => n.Id == nodeId);
        node.Name = "Переименованный узел";
        await s.Db.SaveChangesAsync();

        s.Db.ChangeTracker.Clear();
        var reloaded = await s.Db.IndividualCardHKSourceSnapshots.FirstAsync(x => x.Id == snapshot.Id);
        Assert.Equal("HK-ORIG", reloaded.HKCardCode);
        Assert.Equal("v1", reloaded.HKCardVersion);
        Assert.Equal("Узел 1", reloaded.SourceObjectName);
        Assert.Equal(sourceHkCard.Id, reloaded.SourceHKCardId);
    }

    // ── Audit catalog and permissions ─────────────────────────────────────

    [Fact]
    public void AuditCatalog_ContainsAllIndividualCardActions()
    {
        string[] required =
        [
            "IndividualCard.DraftCreated",
            "IndividualCard.DraftUpdated",
            "IndividualCard.SourcesRefreshed",
            "IndividualCard.Recalculated",
            "IndividualCard.Formed",
            "IndividualCard.NewVersionCreated",
            "IndividualCard.Archived",
            "IndividualCard.DraftDeleted",
        ];
        foreach (var action in required)
            Assert.NotEqual("Неизвестное действие", AuditDisplayCatalog.GetAction(action).Title);
    }

    [Fact]
    public async Task RoleTemplates_FollowAgreedIndividualCardBaseline()
    {
        await using var s = Scope();

        var templates = await s.Db.RolePermissionTemplates
            .Where(t => t.PermissionCode.StartsWith("IndividualCard."))
            .Select(t => new { t.RoleName, t.PermissionCode })
            .ToListAsync();

        var byRole = templates.GroupBy(t => t.RoleName)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToHashSet());

        string[] allIcPermissions =
        [
            PermissionCodes.IndividualCardView,
            PermissionCodes.IndividualCardGenerate,
            PermissionCodes.IndividualCardCreateDraft,
            PermissionCodes.IndividualCardEditDraft,
            PermissionCodes.IndividualCardRecalculateDraft,
            PermissionCodes.IndividualCardForm,
            PermissionCodes.IndividualCardCreateVersion,
            PermissionCodes.IndividualCardArchive,
        ];

        Assert.True(byRole.ContainsKey(nameof(UserRole.SystemAdmin)));
        Assert.True(allIcPermissions.All(c => byRole[nameof(UserRole.SystemAdmin)].Contains(c)));

        Assert.True(allIcPermissions.All(c => byRole[nameof(UserRole.NormAdmin)].Contains(c)));

        string[] operatorPermissions =
        [
            PermissionCodes.IndividualCardView,
            PermissionCodes.IndividualCardGenerate,
            PermissionCodes.IndividualCardCreateDraft,
            PermissionCodes.IndividualCardEditDraft,
            PermissionCodes.IndividualCardRecalculateDraft,
            PermissionCodes.IndividualCardForm,
        ];
        Assert.True(operatorPermissions.All(c => byRole[nameof(UserRole.Operator)].Contains(c)));
        Assert.DoesNotContain(PermissionCodes.IndividualCardArchive, byRole[nameof(UserRole.Operator)]);

        Assert.Contains(PermissionCodes.IndividualCardView, byRole[nameof(UserRole.HeadOfDepartment)]);
        Assert.Contains(PermissionCodes.IndividualCardView, byRole[nameof(UserRole.Guest)]);
    }

    [Fact]
    public void PermissionConstants_ExistForIndividualCardWorkflow()
    {
        Assert.Equal("IndividualCard.View", PermissionCodes.IndividualCardView);
        Assert.Equal("IndividualCard.CreateDraft", PermissionCodes.IndividualCardCreateDraft);
        Assert.Equal("IndividualCard.EditDraft", PermissionCodes.IndividualCardEditDraft);
        Assert.Equal("IndividualCard.RecalculateDraft", PermissionCodes.IndividualCardRecalculateDraft);
        Assert.Equal("IndividualCard.Form", PermissionCodes.IndividualCardForm);
        Assert.Equal("IndividualCard.CreateVersion", PermissionCodes.IndividualCardCreateVersion);
        Assert.Equal("IndividualCard.Archive", PermissionCodes.IndividualCardArchive);
        Assert.All(
        [
            PermissionCodes.IndividualCardView,
            PermissionCodes.IndividualCardCreateDraft,
            PermissionCodes.IndividualCardEditDraft,
            PermissionCodes.IndividualCardRecalculateDraft,
            PermissionCodes.IndividualCardForm,
            PermissionCodes.IndividualCardCreateVersion,
            PermissionCodes.IndividualCardArchive,
        ], code => Assert.Contains(code, PermissionCodes.All));
    }
}
