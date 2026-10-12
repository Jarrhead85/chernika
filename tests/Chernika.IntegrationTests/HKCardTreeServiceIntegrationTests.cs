using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Проверки сервисного сохранения собственного дерева. В отличие от
/// HKCardNodeTreeIntegrationTests (там проверяются ограничения БД), здесь
/// вызываются SaveTreeAsync и ValidateStructureAsync через фикстуру.
/// </summary>
[Collection("Database")]
public class HKCardTreeServiceIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardTreeServiceIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static HKCardNodeWrite Node(
        Guid id, Guid? parentId, HKObjectLevel level, string name, int quantity = 1, int sortOrder = 0)
        => new(id, parentId, level, name, quantity, sortOrder);

    // Тестовый пользователь работает в филиале BranchA (см. TestDatabaseFixture).
    private Guid CurrentBranchId(TestScope scope) => _fixture.BranchA;

    /// <summary>
    /// Нормативный администратор филиала A: у него есть права создания,
    /// редактирования и согласования ХК. Выставляется явно, как в остальных
    /// тестах фикстуры: по умолчанию CurrentUserId не задан.
    /// <para>
    /// HK.Work выдаётся ИНДИВИДУАЛЬНЫМ решением: переход на единое право
    /// частичный, и ролевые шаблоны в этом PR не меняются. Шаблон NormAdmin
    /// получает HK.Work вместе с остальными командами в отдельном PR.
    /// </para>
    /// </summary>
    private TestScope AuthorScope(bool grantHkWork = true)
    {
        var scope = _fixture.CreateScope();
        scope.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        return scope;
    }

    /// <summary>
    /// Выдать пользователю индивидуальное разрешение на HK.Work — так же,
    /// как это делает UserManagementService.GrantPermissionAsync, но без
    /// затрагивания шаблонов ролей.
    /// </summary>
    private async Task<TestScope> AuthorScopeAsync(bool grantHkWork = true)
    {
        var scope = AuthorScope();

        // БД общая для всех тестов класса, поэтому решение приводится к
        // нужному состоянию ВСЕГДА: иначе выдача из одного теста протекала бы
        // в тесты, которые проверяют отказ.
        await GrantHkWorkAsync(scope, granted: grantHkWork);
        return scope;
    }

    private static async Task GrantHkWorkAsync(TestScope scope, bool granted)
    {
        var userId = scope.User.CurrentUserId!.Value.ToString();

        // Upsert: тесты делят одну фикстуру и одного пользователя, поэтому
        // повторная выдача того же права нарушала бы уникальный индекс
        // (UserId, PermissionCode).
        var existing = await scope.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == userId && o.PermissionCode == PermissionCodes.HKWork);

        if (existing is null)
        {
            scope.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PermissionCode = PermissionCodes.HKWork,
                IsGranted = granted,
                Reason = "выдано тестом",
                GrantedByUserId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.IsGranted = granted;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await scope.Db.SaveChangesAsync();

        // PermissionService кэширует эффективные права в IMemoryCache (синглтон
        // на всё приложение). Прямая запись через DbSet этот кэш не сбрасывает,
        // поэтому решение из одного теста протекало бы в следующий. Сброс идёт
        // тем же публичным методом, которым пользуется UserManagementService.
        scope.Permissions.InvalidateCache(userId);
    }

    // ── Фикстура для тестовых автономных карт ──
    // Публичное создание автономной карты отклоняется до утверждения источника
    // номера, поэтому тестовая карта создаётся напрямую с явным тестовым номером.

    private async Task<Guid> CreateAutonomousCardAsync(Guid? branchId = null)
    {
        await using var scope = await AuthorScopeAsync();
        var db = scope.Db;
        // По умолчанию — филиал автора. Иной филиал передаётся явно в тесте
        // на границу организаций.
        var branch = branchId ?? _fixture.BranchA;

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "TESTTREE-" + Guid.NewGuid().ToString("N")[..8],
            Version = "v0101",
            Status = HKCardStatus.Draft,
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            BranchId = branch,
            AuthorId = scope.User.CurrentUserId!.Value,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.HKCards.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }

    private async Task<Guid> CreateAssemblyUnitAsync()
    {
        await using var scope = await AuthorScopeAsync();
        var db = scope.Db;
        var au = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "AUTREE-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Сборочная единица для дерева"
        };
        db.AssemblyUnits.Add(au);
        await db.SaveChangesAsync();
        return au.Id;
    }

    private async Task AttachItemAsync(Guid cardId, Guid auId, Guid? nodeId)
    {
        await using var scope = await AuthorScopeAsync();
        var db = scope.Db;
        db.HKCardItems.Add(new HKCardItem
        {
            Id = Guid.NewGuid(),
            HKCardId = cardId,
            HKCardNodeId = nodeId,
            AssemblyUnitId = auId,
            Quantity = 1,
            SortOrder = 0
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<HKCardNode>> ReadTreeAsync(Guid cardId)
    {
        await using var scope = await AuthorScopeAsync();
        return await scope.Db.HKCardNodes.AsNoTracking().Where(n => n.HKCardId == cardId).ToListAsync();
    }

    // ── Сохранение ──

    [Fact]
    public async Task SaveTree_InitialSave_PersistsTree()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var root = Guid.NewGuid();
        var child = Guid.NewGuid();

        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(root, null, HKObjectLevel.EquipmentModel, "Изделие"),
            Node(child, root, HKObjectLevel.Aggregate, "Агрегат", 2, 0)
        });

        var tree = await ReadTreeAsync(cardId);
        Assert.Equal(2, tree.Count);
        Assert.Contains(tree, n => n.Id == root && n.ParentId == null);
        Assert.Contains(tree, n => n.Id == child && n.ParentId == root && n.Quantity == 2);
    }

    [Fact]
    public async Task SaveTree_SameTreeTwice_Succeeds()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var positions = new[]
        {
            Node(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие"),
            Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат")
        };

        await scope.HK.SaveTreeAsync(cardId, positions);
        await scope.HK.SaveTreeAsync(cardId, positions);

        Assert.Equal(2, (await ReadTreeAsync(cardId)).Count);
    }

    [Fact]
    public async Task SaveTree_KeepsStableIds_AcrossSaves()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var id = Guid.NewGuid();
        var positions = new[] { Node(id, null, HKObjectLevel.EquipmentModel, "Изделие") };

        await scope.HK.SaveTreeAsync(cardId, positions);
        await scope.HK.SaveTreeAsync(cardId, positions.Select(p => p with { Name = "Изделие А" }).ToList());

        var tree = await ReadTreeAsync(cardId);
        Assert.Single(tree);
        Assert.Equal(id, tree[0].Id);
        Assert.Equal("Изделие А", tree[0].Name);
    }

    [Fact]
    public async Task SaveTree_UpdatesNameQuantitySortOrder()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var id = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(cardId,
            new[] { Node(id, null, HKObjectLevel.Aggregate, "Старое", 1, 0) });

        await scope.HK.SaveTreeAsync(cardId,
            new[] { Node(id, null, HKObjectLevel.Aggregate, "Новое", 5, 3) });

        var node = (await ReadTreeAsync(cardId))[0];
        Assert.Equal("Новое", node.Name);
        Assert.Equal(5, node.Quantity);
        Assert.Equal(3, node.SortOrder);
    }

    [Fact]
    public async Task SaveTree_MovesNode_KeepsTerminalRows()
    {
        var cardId = await CreateAutonomousCardAsync();
        var auId = await CreateAssemblyUnitAsync();
        await using var scope = await AuthorScopeAsync();

        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();
        var leaf = Guid.NewGuid();

        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(parentA, null, HKObjectLevel.EquipmentModel, "А"),
            Node(parentB, null, HKObjectLevel.EquipmentModel, "Б"),
            Node(leaf, parentA, HKObjectLevel.Node, "Узел")
        });
        await AttachItemAsync(cardId, auId, leaf);

        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(parentA, null, HKObjectLevel.EquipmentModel, "А"),
            Node(parentB, null, HKObjectLevel.EquipmentModel, "Б"),
            Node(leaf, parentB, HKObjectLevel.Node, "Узел")
        });

        var tree = await ReadTreeAsync(cardId);
        Assert.Equal(parentB, tree.Single(n => n.Id == leaf).ParentId);

        await using var check = AuthorScope();
        var items = await check.Db.HKCardItems.AsNoTracking().Where(i => i.HKCardId == cardId).ToListAsync();
        Assert.Single(items);
        Assert.Equal(leaf, items[0].HKCardNodeId);
    }

    [Fact]
    public async Task SaveTree_NormsAndMaterialsSurviveEdit()
    {
        var cardId = await CreateAutonomousCardAsync();
        var auId = await CreateAssemblyUnitAsync();
        await using var scope = await AuthorScopeAsync();

        var gsmId = await CreateGsmMaterialAsync();
        var leaf = Guid.NewGuid();

        await scope.HK.SaveTreeAsync(cardId,
            new[] { Node(leaf, null, HKObjectLevel.Node, "Узел") });

        await using var attach = AuthorScope();
        var item = new HKCardItem
        {
            Id = Guid.NewGuid(), HKCardId = cardId, HKCardNodeId = leaf,
            AssemblyUnitId = auId, Quantity = 3, Volume = 7.25m, SortOrder = 0
        };
        item.Materials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(), HKCardItemId = item.Id,
            GsmMaterialId = gsmId, Category = GsmCategory.Primary
        });
        attach.Db.HKCardItems.Add(item);
        await attach.Db.SaveChangesAsync();

        // Правка дерева: переименование позиции и добавление второй.
        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(leaf, null, HKObjectLevel.Node, "Узел переименован"),
            Node(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие")
        });

        await using var check = AuthorScope();
        var saved = await check.Db.HKCardItems
            .AsNoTracking().Include(i => i.Materials)
            .FirstAsync(i => i.Id == item.Id);

        Assert.Equal(3, saved.Quantity);
        Assert.Equal(7.25m, saved.Volume);
        Assert.Single(saved.Materials);
    }

    private async Task<Guid> CreateGsmMaterialAsync()
    {
        await using var scope = await AuthorScopeAsync();
        var db = scope.Db;
        var gsm = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "Марка для дерева " + Guid.NewGuid().ToString("N")[..6],
            Nd = "НД-дерево"
        };
        db.GsmMaterials.Add(gsm);
        await db.SaveChangesAsync();
        return gsm.Id;
    }

    [Fact]
    public async Task SaveTree_AddsNodeToExistingTree()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var root = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(cardId, new[] { Node(root, null, HKObjectLevel.EquipmentModel, "Изделие") });

        var added = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(root, null, HKObjectLevel.EquipmentModel, "Изделие"),
            Node(added, root, HKObjectLevel.Aggregate, "Добавленный агрегат")
        });

        Assert.Equal(2, (await ReadTreeAsync(cardId)).Count);
    }

    [Fact]
    public async Task SaveTree_RemovesUnreferencedNode()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var root = Guid.NewGuid();
        var extra = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(root, null, HKObjectLevel.EquipmentModel, "Изделие"),
            Node(extra, root, HKObjectLevel.Aggregate, "Агрегат")
        });

        await scope.HK.SaveTreeAsync(cardId, new[] { Node(root, null, HKObjectLevel.EquipmentModel, "Изделие") });

        var tree = await ReadTreeAsync(cardId);
        Assert.Single(tree);
        Assert.Equal(root, tree[0].Id);
    }

    [Fact]
    public async Task SaveTree_RefusesRemovingNodeWithTerminalRows()
    {
        var cardId = await CreateAutonomousCardAsync();
        var auId = await CreateAssemblyUnitAsync();
        await using var scope = await AuthorScopeAsync();

        var root = Guid.NewGuid();
        var leaf = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(root, null, HKObjectLevel.EquipmentModel, "Изделие"),
            Node(leaf, root, HKObjectLevel.Node, "Узел")
        });
        await AttachItemAsync(cardId, auId, leaf);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(root, null, HKObjectLevel.EquipmentModel, "Изделие") }));

        Assert.Contains("конечные строки", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Дерево не изменилось: позиция со строками осталась.
        Assert.Equal(2, (await ReadTreeAsync(cardId)).Count);
    }

    [Fact]
    public async Task SaveTree_RemovesMultipleNodes_ParentBeforeChild()
    {
        // Порядок удаления вычисляется по глубине: родитель идёт первым во
        // входном порядке, но FK — RESTRICT, поэтому SQL-порядок важен.
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var root = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var leaf = Guid.NewGuid();

        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(root, null, HKObjectLevel.EquipmentModel, "Корень"),
            Node(mid, root, HKObjectLevel.Aggregate, "Средний"),
            Node(leaf, mid, HKObjectLevel.Node, "Лист")
        });

        // Пустое дерево удаляет все три позиции одновременно.
        await scope.HK.SaveTreeAsync(cardId, Array.Empty<HKCardNodeWrite>());

        Assert.Empty(await ReadTreeAsync(cardId));
    }

    // ── Отказы по содержимому набора ──

    [Fact]
    public async Task SaveTree_RefusesMultiStepCycle()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[]
            {
                Node(a, c, HKObjectLevel.EquipmentModel, "A"),
                Node(b, a, HKObjectLevel.Aggregate, "B"),
                Node(c, b, HKObjectLevel.Node, "C")
            }));

        Assert.Contains("цикл", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadTreeAsync(cardId));
    }

    [Fact]
    public async Task SaveTree_RefusesSelfReference()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var id = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(id, id, HKObjectLevel.Aggregate, "Себя") }));

        Assert.Contains("сам", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesDuplicateId()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var id = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[]
            {
                Node(id, null, HKObjectLevel.EquipmentModel, "Первая"),
                Node(id, null, HKObjectLevel.Aggregate, "Вторая")
            }));

        Assert.Contains("дважды", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesEmptyGuid()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.Empty, null, HKObjectLevel.Aggregate, "Пустой") }));

        Assert.Contains("Идентификатор", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesUnknownParent()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), Guid.NewGuid(), HKObjectLevel.Node, "Сирота") }));

        Assert.Contains("родитель", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesForeignId()
    {
        var ownCard = await CreateAutonomousCardAsync();
        var otherCard = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var foreign = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(otherCard, new[] { Node(foreign, null, HKObjectLevel.Aggregate, "Чужая") });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(ownCard, new[] { Node(foreign, null, HKObjectLevel.Aggregate, "Чужая") }));

        Assert.Contains("другой карт", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Позиция осталась в своей карте.
        Assert.Equal(otherCard, (await ReadTreeAsync(otherCard))[0].HKCardId);
    }

    [Fact]
    public async Task SaveTree_RefusesUnknownEnumValue()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, (HKObjectLevel)77, "Странный уровень") }));

        Assert.Contains("уровн", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesUndefinedLevel()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Undefined, "Без уровня") }));

        Assert.Contains("уровн", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesBlankName()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "   ") }));

        Assert.Contains("Название", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesTooLongName()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, new string('я', 201)) }));

        Assert.Contains("длиннее", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesNonPositiveQuantity()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 0, 0) }));

        Assert.Contains("количество", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_RefusesNegativeSortOrder()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, -1) }));

        Assert.Contains("порядок", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTree_AllowsSameNameOnDifferentPositions()
    {
        // Одноимённые независимые позиции разрешены: уникальность по имени
        // не вводится и не проверяется.
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Насосный блок"),
            Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Насосный блок")
        });

        Assert.Equal(2, (await ReadTreeAsync(cardId)).Count);
    }

    [Fact]
    public async Task SaveTree_FailedValidation_LeavesTreeUnchanged()
    {
        // Отказ не должен оставить частично изменённое дерево.
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var keep = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(cardId, new[] { Node(keep, null, HKObjectLevel.EquipmentModel, "Изделие") });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[]
            {
                Node(keep, null, HKObjectLevel.EquipmentModel, "Переименовано"),
                Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Плохая", 0, 0)
            }));

        var tree = await ReadTreeAsync(cardId);
        Assert.Single(tree);
        Assert.Equal("Изделие", tree[0].Name);
    }

    [Fact]
    public async Task SaveTree_OtherBranch_IsRefused()
    {
        await using var scope = await AuthorScopeAsync();
        var db = scope.Db;

        // Карта в чужом филиале: сервисные проверки доступа должны отклонить.
        var foreignBranch = _fixture.BranchB;
        if (foreignBranch == Guid.Empty)
            return;

        var cardId = await CreateAutonomousCardAsync(foreignBranch);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Чужая") }));

        Assert.Empty(await ReadTreeAsync(cardId));
    }

    // ── Доступ по единому праву HK.Work ──

    [Fact]
    public async Task SaveTree_HkWork_Allows_UndefinedLevel()
    {
        // Уровень карты ещё не выведен: остаётся Undefined, и HK.Work
        // должно работать именно в этом состоянии.
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        await scope.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат") });

        var card = await scope.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(HKObjectLevel.Undefined, card.ObjectLevel);
        Assert.Single(await ReadTreeAsync(cardId));
    }

    [Fact]
    public async Task SaveTree_HkWork_Allows_DefinedLevel()
    {
        // Определённый уровень не добавляет условия: то же право работает.
        await using var setup = await AuthorScopeAsync();

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "TESTLVL-" + Guid.NewGuid().ToString("N")[..8],
            Version = "v0101",
            Status = HKCardStatus.Draft,
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Node,
            BranchId = _fixture.BranchA,
            AuthorId = setup.User.CurrentUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        setup.Db.HKCards.Add(card);
        await setup.Db.SaveChangesAsync();

        await using var scope = await AuthorScopeAsync();
        await scope.HK.SaveTreeAsync(card.Id,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        Assert.Single(await ReadTreeAsync(card.Id));
    }

    [Fact]
    public async Task SaveTree_WithoutHkWork_IsRefused()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync(grantHkWork: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "А") }));

        Assert.Empty(await ReadTreeAsync(cardId));
    }

    [Fact]
    public async Task SaveTree_LevelPermission_DoesNotSubstituteForHkWork()
    {
        // Уровневые права создания и редактирования НЕ заменяют HK.Work
        // после перехода команды на единое право.
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync(grantHkWork: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "А") }));

        Assert.Empty(await ReadTreeAsync(cardId));
    }

    [Fact]
    public async Task SaveTree_IndividualDenyOfHkWork_IsRespected()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync(grantHkWork: false);
        await GrantHkWorkAsync(scope, granted: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "А") }));

        Assert.Empty(await ReadTreeAsync(cardId));
    }

    [Fact]
    public async Task SaveTree_HkWork_DoesNotBypassBranchRestriction()
    {
        var cardId = await CreateAutonomousCardAsync(_fixture.BranchB);
        await using var scope = await AuthorScopeAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "А") }));

        Assert.Empty(await ReadTreeAsync(cardId));
    }

    [Fact]
    public async Task SaveTree_HkWork_DoesNotBypassEditableStatus()
    {
        var cardId = await CreateAutonomousCardAsync();

        // Перевод карты в статус, в котором редактирование запрещено.
        await using (var setup = await AuthorScopeAsync())
        {
            var card = await setup.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.Status = HKCardStatus.Approved;
            await setup.Db.SaveChangesAsync();
        }

        await using var scope = await AuthorScopeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId, new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "А") }));

        Assert.Empty(await ReadTreeAsync(cardId));
    }

    // ── Конкурентное сохранение ──

[Fact]
    public async Task SaveTree_StaleRowVersion_IsRefused_AndTreeUnchanged()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var keep = Guid.NewGuid();
        await scope.HK.SaveTreeAsync(cardId,
            new[] { Node(keep, null, HKObjectLevel.EquipmentModel, "Изделие") });

        var currentVersion = await CurrentRowVersionAsync(scope, cardId);

        // Параллельная правка карты другим пользователем двигает xmin.
        await using (var other = await AuthorScopeAsync())
        {
            var card = await other.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.Purpose = "правка другого пользователя";
            await other.Db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.HK.SaveTreeAsync(cardId,
                new[] { Node(keep, null, HKObjectLevel.EquipmentModel, "Переименовано") },
                expectedRowVersion: currentVersion));

        Assert.Contains("изменена другим", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Дерево не изменено: отказ дошёл до записи.
        var tree = await ReadTreeAsync(cardId);
        Assert.Single(tree);
        Assert.Equal("Изделие", tree[0].Name);
    }

    [Fact]
    public async Task SaveTree_CurrentRowVersion_IsAccepted()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var version = await CurrentRowVersionAsync(scope, cardId);
        await scope.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат") },
            expectedRowVersion: version);

        Assert.Single(await ReadTreeAsync(cardId));
    }

    [Fact]
    public async Task SaveTree_UpdatesCardRow_UnderConcurrentCardEdit()
    {
        // После сохранения дерева версия карты обязана измениться: запись карты
        // обновляется в той же транзакции, поэтому токен участвует в
        // конкурентной проверке и следующий сохрантельщик увидит новую версию.
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var before = await CurrentRowVersionAsync(scope, cardId);

        await scope.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат") },
            expectedRowVersion: before);

        var after = await CurrentRowVersionAsync(scope, cardId);
        Assert.NotEqual(before, after);
    }

    private static async Task<uint> CurrentRowVersionAsync(TestScope scope, Guid cardId)
    {
        scope.Db.ChangeTracker.Clear();
        return (await scope.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).RowVersion;
    }

    // ── Удаление вместе с переносом сохраняемой позиции ──

    [Fact]
    public async Task SaveTree_RemovesBranch_WhileMovingKeptNodeOut()
    {
        // Удаляется вся ветвь, кроме одного узла, который одновременно
        // переносится в сохраняемую ветвь. Порядок удаления считается по
        // СОХРАНЁННОМУ дереву, иначе FK родителя срывает операцию.
        var cardId = await CreateAutonomousCardAsync();
        var auId = await CreateAssemblyUnitAsync();
        await using var scope = await AuthorScopeAsync();

        var root = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        var doomedChild = Guid.NewGuid();
        var kept = Guid.NewGuid();

        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(root, null, HKObjectLevel.EquipmentModel, "Корень", 1, 0),
            Node(kept, root, HKObjectLevel.Node, "Сохраняемый узел", 1, 1),
            Node(doomed, root, HKObjectLevel.Aggregate, "Удаляемая ветвь", 1, 2),
            Node(doomedChild, doomed, HKObjectLevel.Node, "Потомок удаляемого", 1, 0)
        });

        await AttachItemAsync(cardId, auId, kept);

        // kept переносится под root (остаётся), ветвь doomed удаляется целиком.
        await scope.HK.SaveTreeAsync(cardId, new[]
        {
            Node(root, null, HKObjectLevel.EquipmentModel, "Корень", 1, 0),
            Node(kept, root, HKObjectLevel.Node, "Сохраняемый узел", 1, 1)
        });

        var tree = await ReadTreeAsync(cardId);
        Assert.Equal(2, tree.Count);
        Assert.DoesNotContain(tree, n => n.Id == doomed);
        Assert.DoesNotContain(tree, n => n.Id == doomedChild);

        // Конечная строка сохраняемого узла на месте.
        await using var check = await AuthorScopeAsync();
        var item = await check.Db.HKCardItems.AsNoTracking().FirstAsync(i => i.HKCardId == cardId);
        Assert.Equal(kept, item.HKCardNodeId);
    }

    // ── Структурная валидация ──

    [Fact]
    public async Task ValidateStructure_RowsUnderRootOnly_IsNotEmpty()
    {
        var cardId = await CreateAutonomousCardAsync();
        var auId = await CreateAssemblyUnitAsync();
        await AttachItemAsync(cardId, auId, null);
        await using var scope = await AuthorScopeAsync();

        var result = await scope.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var validation = await scope.Validation.ValidateStructureAsync(result);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task ValidateStructure_EmptyDraft_IsRejectedOnlyAtComposition()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        var card = await scope.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        // Проверка заполненности состава: пусто отклоняется.
        Assert.False((await scope.Validation.ValidateStructureAsync(card)).IsValid);

        // Но пустой черновик сохраняется и на согласование реквизитов
        // ValidateDraftAsync структуру не проверяет.
        Assert.True((await scope.Validation.ValidateDraftAsync(card)).IsValid);
    }

    [Fact]
    public async Task ValidateStructure_ReportsUndefinedLevel()
    {
        // Undefined — признак невыведенного уровня карты, а не позиции.
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        // Прямая вставка в обход сервиса: сервис Undefined не принимает.
        scope.Db.HKCardNodes.Add(new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = cardId, ParentId = null,
            Level = HKObjectLevel.Undefined, Name = "Без уровня", SortOrder = 0
        });
        await scope.Db.SaveChangesAsync();

        var card = await scope.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var validation = await scope.Validation.ValidateStructureAsync(card);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Code == "node-level-undefined");
    }

    [Fact]
    public async Task ValidateStructure_ReportsCycleInPersistedTree()
    {
        var cardId = await CreateAutonomousCardAsync();
        await using var scope = await AuthorScopeAsync();

        // Цикл, минуя сервис: прямой вставкой, чтобы проверить именно
        // сервисную проверку на уже сохранённом дереве. Позиции вставляются
        // отдельными SaveChanges: единым графом EF обнаружил бы цикл в графе
        // отслеживаемых объектов и не дошёл бы до проверки сервиса.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        scope.Db.HKCardNodes.Add(
            new HKCardNode { Id = a, HKCardId = cardId, ParentId = null, Level = HKObjectLevel.EquipmentModel, Name = "A", SortOrder = 0 });
        await scope.Db.SaveChangesAsync();

        scope.Db.HKCardNodes.Add(
            new HKCardNode { Id = b, HKCardId = cardId, ParentId = a, Level = HKObjectLevel.Aggregate, Name = "B", SortOrder = 1 });
        await scope.Db.SaveChangesAsync();

        // Замыкаем цикл обновлением: A становится потомком B.
        var nodeA = await scope.Db.HKCardNodes.FirstAsync(n => n.Id == a);
        nodeA.ParentId = b;
        await scope.Db.SaveChangesAsync();

        var card = await scope.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var validation = await scope.Validation.ValidateStructureAsync(card);

        Assert.False(validation.IsValid);
    }

    // ── Создание автономной карты ──

    [Fact]
    public async Task CreateAsync_Autonomous_IsRefusedWithoutNumberSource()
    {
        await using var scope = await AuthorScopeAsync();

        var card = new HKCard
        {
            Code = "",
            Version = "",
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            BranchId = CurrentBranchId(scope)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.HK.CreateAsync(card));
        Assert.Contains("номер", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Карта не сохранена.
        await using var check = AuthorScope();
        var saved = await check.Db.HKCards.AsNoTracking().AnyAsync(c => c.IsAutonomous && c.Code == "");
        Assert.False(saved);
    }
}