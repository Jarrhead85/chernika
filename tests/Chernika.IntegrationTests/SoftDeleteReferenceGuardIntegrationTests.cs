using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Guards на soft-delete справочников: удалять сущность нельзя, если на неё
/// ссылаются живые записи. Иначе глобальный query-фильтр скрывает родителя из
/// required-навигаций (FK остаётся, а навигация приходит null) и рушит отображение
/// документов и реестров.
/// </summary>
[Collection("Database")]
public class SoftDeleteReferenceGuardIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public SoftDeleteReferenceGuardIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SetRefEditor(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    private void SetSystemAdmin(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

    // ── Узел ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteNode_Blocked_WhenNodeUsedInActiveAggregateComposition()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var nodeId = await CreateNodeAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        await CreateAggregateCompositionWithNodeAsync(s, aggregateId, nodeId, isActive: true);

        var (deleted, error) = await s.Equipment.DeleteNodeAsync(nodeId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("состав", error!, StringComparison.OrdinalIgnoreCase);

        var stillLive = await s.Db.Nodes.IgnoreQueryFilters()
            .AnyAsync(n => n.Id == nodeId && !n.IsDeleted);
        Assert.True(stillLive);
    }

    [Fact]
    public async Task DeleteNode_Blocked_WhenNodeUsedInLiveHkCard()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var nodeId = await CreateNodeAsync(s);
        await CreateNodeHkCardAsync(s, nodeId, HKCardStatus.Draft);

        var (deleted, error) = await s.Equipment.DeleteNodeAsync(nodeId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("ХК", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteNode_Succeeds_WhenUnreferenced()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var nodeId = await CreateNodeAsync(s);

        var (deleted, error) = await s.Equipment.DeleteNodeAsync(nodeId);

        Assert.True(deleted);
        Assert.Null(error);
    }

    // ── Изделие ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteModel_Blocked_WhenUsedInActiveProductComposition()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var modelId = await CreateModelAsync(s);
        await CreateProductCompositionAsync(s, modelId, isActive: true);

        var (deleted, error) = await s.Equipment.DeleteModelAsync(modelId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("состав", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteModel_Blocked_WhenUsedInActiveComplexComposition()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var modelId = await CreateModelAsync(s);
        var complexId = await CreateComplexAsync(s);
        await CreateComplexCompositionWithModelAsync(s, complexId, modelId);

        var (deleted, error) = await s.Equipment.DeleteModelAsync(modelId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("состав", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteModel_Blocked_WhenUsedInLiveInstance()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var modelId = await CreateModelAsync(s);
        s.Db.EquipmentInstances.Add(new EquipmentInstance
        {
            Id = Guid.NewGuid(),
            SerialNumber = "SN-" + Suffix(),
            Index = "IX-" + Suffix(),
            Name = "Экземпляр " + Suffix(),
            EquipmentModelId = modelId,
            IsDeleted = false,
        });
        await s.Db.SaveChangesAsync();

        var (deleted, error) = await s.Equipment.DeleteModelAsync(modelId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("экземпляр", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteModel_Blocked_WhenUsedInLiveHkCard()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var modelId = await CreateModelAsync(s);
        await CreateModelHkCardAsync(s, modelId, HKCardStatus.Approved);

        var (deleted, error) = await s.Equipment.DeleteModelAsync(modelId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("ХК", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteModel_Succeeds_WhenUnreferenced()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var modelId = await CreateModelAsync(s);

        var (deleted, error) = await s.Equipment.DeleteModelAsync(modelId);

        Assert.True(deleted);
        Assert.Null(error);
    }

    // ── Сборочная единица ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAssemblyUnit_Blocked_WhenUsedInLiveHkCardItem()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var auId = await CreateAssemblyUnitAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var hkId = await CreateNodeHkCardAsync(s, nodeId, HKCardStatus.Approved);
        s.Db.HKCardItems.Add(new HKCardItem
        {
            Id = Guid.NewGuid(),
            HKCardId = hkId,
            AssemblyUnitId = auId,
            Quantity = 1,
            Volume = 1m,
            UnitOfMeasure = "шт",
            SortOrder = 1,
        });
        await s.Db.SaveChangesAsync();

        var (deleted, error) = await s.Equipment.DeleteAssemblyUnitAsync(auId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("ХК", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAssemblyUnit_Succeeds_WhenUnreferenced()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var auId = await CreateAssemblyUnitAsync(s);

        var (deleted, error) = await s.Equipment.DeleteAssemblyUnitAsync(auId);

        Assert.True(deleted);
        Assert.Null(error);
    }

    // ── Организация ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBranch_Blocked_WhenBranchHasIndividualCard()
    {
        await using var s = _fixture.CreateScope();
        SetSystemAdmin(s);

        var branchId = await CreateBranchAsync(s);
        var nodeId = await CreateNodeAsync(s);
        s.Db.IndividualCards.Add(new IndividualCard
        {
            Id = Guid.NewGuid(),
            Code = "ИК-ГАРД-" + Suffix(),
            Version = "v1",
            RevisionNumber = 1,
            ObjectLevel = IndividualCardObjectLevel.Node,
            NodeId = nodeId,
            Status = IndividualCardStatus.Draft,
            BranchId = branchId,
            CreatedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();

        var (deleted, error) = await s.Equipment.DeleteBranchAsync(branchId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("индивидуальные карты", error!, StringComparison.OrdinalIgnoreCase);

        var stillLive = await s.Db.Branches.IgnoreQueryFilters()
            .AnyAsync(b => b.Id == branchId && !b.IsDeleted);
        Assert.True(stillLive);
    }

    [Fact]
    public async Task DeleteBranch_Blocked_WhenBranchHasComposition()
    {
        await using var s = _fixture.CreateScope();
        SetSystemAdmin(s);

        var branchId = await CreateBranchAsync(s);
        var modelId = await CreateModelAsync(s);
        await CreateProductCompositionAsync(s, modelId, isActive: false, branchId: branchId);

        var (deleted, error) = await s.Equipment.DeleteBranchAsync(branchId);

        Assert.False(deleted);
        Assert.NotNull(error);
        Assert.Contains("конструктивные составы", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Фикстуры данных ────────────────────────────────────────────────────

    private async Task<Guid> CreateNodeAsync(TestScope s)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    private async Task<Guid> CreateModelAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Suffix(), Name = "Изделие " + Suffix(), IsDeleted = false };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();
        return model.Id;
    }

    private async Task<Guid> CreateAggregateAsync(TestScope s)
    {
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Suffix(), Name = "Агрегат " + Suffix(), IsDeleted = false };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        return aggregate.Id;
    }

    private async Task<Guid> CreateComplexAsync(TestScope s)
    {
        var complex = new Complex { Id = Guid.NewGuid(), Code = "C-" + Suffix(), Name = "Комплекс " + Suffix(), IsDeleted = false };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();
        return complex.Id;
    }

    private async Task<Guid> CreateAssemblyUnitAsync(TestScope s)
    {
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix(), IsDeleted = false };
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();
        return au.Id;
    }

    private async Task<Guid> CreateBranchAsync(TestScope s)
    {
        var branch = new Branch { Id = Guid.NewGuid(), Name = "Филиал " + Suffix(), Code = "F" + Suffix() };
        s.Db.Branches.Add(branch);
        await s.Db.SaveChangesAsync();
        return branch.Id;
    }

    private async Task CreateAggregateCompositionWithNodeAsync(TestScope s, Guid aggregateId, Guid nodeId, bool isActive)
    {
        var ac = new AggregateComposition
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = isActive,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.AggregateCompositions.Add(ac);
        s.Db.AggregateCompositionNodes.Add(new AggregateCompositionNode
        {
            Id = Guid.NewGuid(),
            AggregateCompositionId = ac.Id,
            NodeId = nodeId,
            Quantity = 1,
            SortOrder = 1,
        });
        await s.Db.SaveChangesAsync();
    }

    private async Task CreateProductCompositionAsync(TestScope s, Guid modelId, bool isActive, Guid? branchId = null)
    {
        s.Db.ProductCompositions.Add(new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = modelId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = isActive,
            BranchId = branchId,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
    }

    private async Task CreateComplexCompositionWithModelAsync(TestScope s, Guid complexId, Guid modelId)
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
        s.Db.ComplexCompositionItems.Add(new ComplexCompositionItem
        {
            Id = Guid.NewGuid(),
            ComplexCompositionId = cc.Id,
            EquipmentModelId = modelId,
            Quantity = 1,
            SortOrder = 1,
        });
        await s.Db.SaveChangesAsync();
    }

    private async Task<Guid> CreateNodeHkCardAsync(TestScope s, Guid nodeId, HKCardStatus status)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-N-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = status,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            ApprovedDate = status == HKCardStatus.Approved ? DateTime.UtcNow : null,
        };
        s.Db.HKCards.Add(hk);
        await s.Db.SaveChangesAsync();
        return hk.Id;
    }

    private async Task<Guid> CreateModelHkCardAsync(TestScope s, Guid modelId, HKCardStatus status)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-M-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = status,
            ObjectLevel = HKObjectLevel.EquipmentModel,
            EquipmentModelId = modelId,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            ApprovedDate = status == HKCardStatus.Approved ? DateTime.UtcNow : null,
        };
        s.Db.HKCards.Add(hk);
        await s.Db.SaveChangesAsync();
        return hk.Id;
    }
}
