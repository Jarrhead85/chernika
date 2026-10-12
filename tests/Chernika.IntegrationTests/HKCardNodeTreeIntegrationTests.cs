using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardNodeTreeIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardNodeTreeIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SaveTree_PersistsNodes()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "TEST-001",
            Version = "v0101",
            Status = HKCardStatus.Draft,
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            BranchId = await GetFirstBranchIdAsync(db)
        };

        var root = new HKCardNode
        {
            Id = Guid.NewGuid(),
            HKCardId = card.Id,
            Level = HKObjectLevel.EquipmentModel,
            Name = "Изделие А",
            Quantity = 1,
            SortOrder = 0
        };

        var child = new HKCardNode
        {
            Id = Guid.NewGuid(),
            HKCardId = card.Id,
            ParentId = root.Id,
            Level = HKObjectLevel.Aggregate,
            Name = "Агрегат Б",
            Quantity = 2,
            SortOrder = 0
        };

        card.TreeNodes.Add(root);
        card.TreeNodes.Add(child);
        db.HKCards.Add(card);
        await db.SaveChangesAsync();

        var loaded = await db.HKCards
            .AsNoTracking()
            .Include(c => c.TreeNodes)
            .FirstAsync(c => c.Id == card.Id);

        Assert.Equal(2, loaded.TreeNodes.Count);
        Assert.Contains(loaded.TreeNodes, n => n.Name == "Изделие А" && n.ParentId == null);
        Assert.Contains(loaded.TreeNodes, n => n.Name == "Агрегат Б" && n.ParentId == root.Id);
    }

    [Fact]
    public async Task MultipleCards_SameName_Allowed()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        var card1 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-002", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };
        var card2 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-002", Version = "v0102",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        db.HKCards.AddRange(card1, card2);
        await db.SaveChangesAsync();

        var count = await db.HKCards.CountAsync(c => c.Code == "TEST-002");
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ParentFromAnotherCard_Rejected()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        var card1 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-003", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };
        var card2 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-004", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        var root = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card1.Id,
            Level = HKObjectLevel.EquipmentModel, Name = "Root",
            Quantity = 1, SortOrder = 0
        };

        var child = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card2.Id,
            ParentId = root.Id, // Родитель из другой карты
            Level = HKObjectLevel.Aggregate, Name = "Child",
            Quantity = 1, SortOrder = 0
        };

        card1.TreeNodes.Add(root);
        card2.TreeNodes.Add(child);
        db.HKCards.AddRange(card1, card2);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ItemFromAnotherCard_Rejected()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);
        var auId = await GetFirstAssemblyUnitIdAsync(db);

        var card1 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-005", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };
        var card2 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-006", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        var node = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card1.Id,
            Level = HKObjectLevel.Node, Name = "Узел",
            Quantity = 1, SortOrder = 0
        };

        var item = new HKCardItem
        {
            Id = Guid.NewGuid(), HKCardId = card2.Id, // Строка из другой карты
            HKCardNodeId = node.Id,
            AssemblyUnitId = auId,
            Quantity = 1, SortOrder = 0
        };

        card1.TreeNodes.Add(node);
        db.HKCards.AddRange(card1, card2);
        db.HKCardItems.Add(item);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SelfReference_Rejected()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        var card = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-007", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        var node = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id,
            Level = HKObjectLevel.EquipmentModel, Name = "Root",
            Quantity = 1, SortOrder = 0
        };
        node.ParentId = node.Id; // Самоссылка

        card.TreeNodes.Add(node);
        db.HKCards.Add(card);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task MultiStepCycle_Rejected()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        var card = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-008", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        var a = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id,
            Level = HKObjectLevel.EquipmentModel, Name = "A",
            Quantity = 1, SortOrder = 0
        };
        var b = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id,
            Level = HKObjectLevel.Aggregate, Name = "B",
            Quantity = 1, SortOrder = 0
        };
        var c = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id,
            Level = HKObjectLevel.Node, Name = "C",
            Quantity = 1, SortOrder = 0
        };

        // Создаём цикл: A -> B -> C -> A
        b.ParentId = a.Id;
        c.ParentId = b.Id;
        a.ParentId = c.Id;

        card.TreeNodes.Add(a);
        card.TreeNodes.Add(b);
        card.TreeNodes.Add(c);
        db.HKCards.Add(card);

        // EF Core обнаруживает цикл на уровне графа зависимостей до отправки в БД.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task QuantityAndSortOrder_Validated()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        var card = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-009", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        var node = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id,
            Level = HKObjectLevel.EquipmentModel, Name = "Root",
            Quantity = 0, // Недопустимо
            SortOrder = -1 // Недопустимо
        };

        card.TreeNodes.Add(node);
        db.HKCards.Add(card);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NormsAndMaterials_PersistViaHKCardItem()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        // Создаём собственные справочные данные — тестовая БД может быть пустой.
        var au = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "AU-TEST",
            Name = "Сборочная единица тестовая"
        };
        db.AssemblyUnits.Add(au);

        var gsm = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "Марка ГСМ тестовая",
            Nd = "НД-тест"
        };
        db.GsmMaterials.Add(gsm);
        await db.SaveChangesAsync();

        var card = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-010", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        var node = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id,
            Level = HKObjectLevel.Node, Name = "Узел",
            Quantity = 1, SortOrder = 0
        };

        var item = new HKCardItem
        {
            Id = Guid.NewGuid(), HKCardId = card.Id,
            HKCardNodeId = node.Id,
            AssemblyUnitId = au.Id,
            Quantity = 2, Volume = 1.5m,
            UnitOfMeasure = "кг", SortOrder = 0
        };

        item.Materials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = item.Id,
            GsmMaterialId = gsm.Id,
            Category = GsmCategory.Primary
        });

        card.TreeNodes.Add(node);
        db.HKCards.Add(card);
        db.HKCardItems.Add(item);
        await db.SaveChangesAsync();

        var loaded = await db.HKCardItems
            .AsNoTracking()
            .Include(i => i.Materials)
            .FirstAsync(i => i.HKCardId == card.Id);

        Assert.Equal(2, loaded.Quantity);
        Assert.Equal(1.5m, loaded.Volume);
        Assert.Single(loaded.Materials);
    }

    [Fact]
    public async Task EmptyAutonomousDraft_Allowed()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        var card = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-011", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        db.HKCards.Add(card);
        await db.SaveChangesAsync();

        var loaded = await db.HKCards.AsNoTracking().FirstAsync(c => c.Id == card.Id);
        Assert.True(loaded.IsAutonomous);
        Assert.Empty(loaded.TreeNodes);
    }

    [Fact]
    public async Task OldCard_RequiresObject()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        var card = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-012", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = false,
            ObjectLevel = HKObjectLevel.Node, BranchId = branchId
            // NodeId не указан — для старой карты это ошибка
        };

        db.HKCards.Add(card);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AutonomousCard_NotLimitedByActivePerObject()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.Db;
        var branchId = await GetFirstBranchIdAsync(db);

        // Создаём две автономные карты с одинаковым кодом — допустимо
        var card1 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-013", Version = "v0101",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };
        var card2 = new HKCard
        {
            Id = Guid.NewGuid(), Code = "TEST-013", Version = "v0102",
            Status = HKCardStatus.Draft, IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined, BranchId = branchId
        };

        db.HKCards.AddRange(card1, card2);
        await db.SaveChangesAsync();

        var count = await db.HKCards.CountAsync(c => c.Code == "TEST-013");
        Assert.Equal(2, count);
    }

    private static async Task<Guid> GetFirstBranchIdAsync(AppDbContext db) =>
        await db.Branches.Select(b => b.Id).FirstAsync();

    private static async Task<Guid> GetFirstAssemblyUnitIdAsync(AppDbContext db) =>
        await db.AssemblyUnits.Select(a => a.Id).FirstAsync();

    private static async Task<Guid> GetFirstGsmMaterialIdAsync(AppDbContext db) =>
        await db.GsmMaterials.Select(g => g.Id).FirstAsync();
}
