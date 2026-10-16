using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Domain.Enums;
using Chernika.Domain.Leveling;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Определение уровня ХК по собственной структуре на реальной базе.
/// <para>
/// Проверяется не чистое правило (оно покрыто unit-тестами), а интеграция:
/// пересчёт рабочего уровня при сохранении, специальный выбор верхнего узла,
/// связь с этапами согласования и неизменность принятой редакции.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardLevelIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardLevelIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private TestScope As(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    private async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();
    }

    private async Task<HKObjectLevel> LevelAsync(Guid cardId)
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.ObjectLevel).FirstAsync();
    }

    private static HKCardNodeWrite Node(
        Guid id, Guid? parent, HKObjectLevel level, string name) =>
        new(id, parent, level, name, 1, 0);

    // ── Пересчёт рабочего уровня ──

    [Fact]
    public async Task SaveTree_TopAggregate_SetsEquipmentModelLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат") });

        Assert.Equal(HKObjectLevel.EquipmentModel, await LevelAsync(cardId));
    }

    /// <summary>
    /// Верхнее изделие даёт ХК комплекса — единственный путь к уровню Complex.
    /// </summary>
    [Fact]
    public async Task SaveTree_TopEquipmentModel_SetsComplexLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие") });

        Assert.Equal(HKObjectLevel.Complex, await LevelAsync(cardId));
    }

    /// <summary>
    /// Позиция Complex — дополнительная копия корня: сохранить её нельзя, и
    /// ничего не сохраняется наполовину.
    /// </summary>
    [Fact]
    public async Task SaveTree_TopComplexPosition_IsRejected()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Complex, "Комплекс") }));

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardNodes.CountAsync(n => n.HKCardId == cardId));
        Assert.Equal(HKObjectLevel.Undefined, await LevelAsync(cardId));
    }

    /// <summary>
    /// Отсутствие HKCardNode не означает пустую карту, если есть конечные строки
    /// под корнем: такая карта — ХК узла.
    /// </summary>
    [Fact]
    public async Task TerminalRowsUnderRoot_AreCountedAsNodeCard()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        Assert.Equal(0, await s.Db.HKCardNodes.CountAsync(n => n.HKCardId == cardId));
        Assert.Equal(HKObjectLevel.Node, await LevelAsync(cardId));
    }

    [Fact]
    public async Task EmptyCard_HasUndefinedLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        Assert.Equal(HKObjectLevel.Undefined, await LevelAsync(cardId));
    }

    /// <summary>
    /// Правка только реквизитов не меняет уровень: структура не менялась.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_WithoutStructureChange_KeepsLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        var before = await LevelAsync(cardId);

        var card = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        card.Notes = "правка без структуры";
        await s.HK.UpdateAsync(card);

        Assert.Equal(before, await LevelAsync(cardId));
    }

    // ── Специальный выбор верхнего узла ──

    [Fact]
    public async Task TopNodeWithoutChoice_RequiresChoice()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        await using var read = As(_fixture.NormAdminA2.Id);
        var projection = await read.HK.GetLevelProjectionAsync(cardId);

        Assert.True(projection.NeedsNodeRootChoice);
        Assert.Equal(HKCardLevelStatus.NodeRootChoiceRequired, projection.Status);
        Assert.Equal(HKObjectLevel.Undefined, await LevelAsync(cardId));
    }

    [Theory]
    [InlineData(HKObjectLevel.Aggregate)]
    [InlineData(HKObjectLevel.EquipmentModel)]
    public async Task SetNodeRootLevel_SavesChoiceAndDerivedLevel(HKObjectLevel choice)
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        await s.HK.SetNodeRootCardLevelAsync(cardId, choice, await VersionAsync(s, cardId));

        await using var read = _fixture.CreateScope();
        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        Assert.Equal(choice, card.NodeRootCardLevel);
        Assert.Equal(choice, card.ObjectLevel);
    }

    /// <summary>
    /// Выбор не применим, когда старшая верхняя позиция — не узел.
    /// </summary>
    [Fact]
    public async Task SetNodeRootLevel_WhenTopIsNotNode_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат") });

        // Версия читается ДО вызова: await внутри не-async лямбды недопустим.
        var version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SetNodeRootCardLevelAsync(
                cardId, HKObjectLevel.Aggregate, version));

        await using var read = _fixture.CreateScope();
        Assert.Null((await read.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == cardId)).NodeRootCardLevel);
    }

    /// <summary>
    /// Конфликт версии не оставляет частичного изменения: ни выбора, ни уровня.
    /// </summary>
    [Fact]
    public async Task SetNodeRootLevel_StaleRowVersion_LeavesNothingChanged()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        var staleVersion = await VersionAsync(s, cardId);

        // Другая команда двигает версию карты.
        var card = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        card.UpdatedAt = DateTime.UtcNow.AddMinutes(1);
        await s.Db.SaveChangesAsync();

        // Предметный конфликт версии, а не случайная ошибка.
        await Assert.ThrowsAsync<HkCardVersionConflictException>(
            () => s.HK.SetNodeRootCardLevelAsync(
                cardId, HKObjectLevel.Aggregate, staleVersion));

        await using var read = _fixture.CreateScope();
        var stored = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Null(stored.NodeRootCardLevel);
        Assert.Equal(HKObjectLevel.Undefined, stored.ObjectLevel);
    }

    /// <summary>
    /// Прежний выбор перестаёт влиять, когда старший верхний уровень изменился с
    /// узла на другой: он сбрасывается, а не хранится скрыто устаревшим.
    /// </summary>
    [Fact]
    public async Task StaleChoice_IsClearedWhenTopLevelChanges()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var nodeId = Guid.NewGuid();
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Node, "Узел") });
        await s.HK.SetNodeRootCardLevelAsync(
            cardId, HKObjectLevel.Aggregate, await VersionAsync(s, cardId));

        // Старшая верхняя позиция меняется на агрегат.
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Aggregate, "Агрегат") });

        await using var read = _fixture.CreateScope();
        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        Assert.Null(card.NodeRootCardLevel);
        Assert.Equal(HKObjectLevel.EquipmentModel, card.ObjectLevel);
    }

    // ── Сохранение специального выбора при верхнем узле ──

    /// <summary>
    /// Выбор верхнего узла переживает сохранение дерева, сохранение строк и
    /// правку реквизитов. NeedsNodeRootChoice означает «выбор ещё необходим»,
    /// а не «выбор неприменим»: по нему сбрасывать действующий выбор нельзя.
    /// </summary>
    [Theory]
    [InlineData(HKObjectLevel.Aggregate)]
    [InlineData(HKObjectLevel.EquipmentModel)]
    public async Task NodeRootChoice_SurvivesTreeRowAndDetailsSaves(HKObjectLevel choice)
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var nodeId = Guid.NewGuid();
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Node, "Узел") });
        await s.HK.SetNodeRootCardLevelAsync(cardId, choice, await VersionAsync(s, cardId));

        // Повторное сохранение того же дерева.
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Node, "Узел иной") });
        await AssertChoiceAsync(cardId, choice);

        // Сохранение строки под корнем.
        var unitId = await UnitAsync();
        var card = await s.HK.GetByIdAsync(cardId);
        card!.Items.Add(new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = unitId,
            Quantity = 1,
            SortOrder = 1
        });
        await s.HK.UpdateAsync(card);
        await AssertChoiceAsync(cardId, choice);

        // Правка только реквизитов.
        var again = await s.HK.GetByIdAsync(cardId);
        again!.Notes = "правка реквизитов " + Suffix();
        await s.HK.UpdateAsync(again);
        await AssertChoiceAsync(cardId, choice);

        // Итог: уровень определён, состав отправляется.
        await AssertChoiceAsync(cardId, choice);

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);
    }

    private async Task AssertChoiceAsync(Guid cardId, HKObjectLevel expected)
    {
        await using var read = _fixture.CreateScope();
        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        Assert.Equal(expected, card.NodeRootCardLevel);
        Assert.Equal(expected, card.ObjectLevel);
    }

    /// <summary>
    /// После возврата к структуре, начинающейся с узла, старый выбор не действует:
    /// требуется новый явный выбор.
    /// </summary>
    [Fact]
    public async Task StructureBackToNode_RequiresFreshChoice()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var nodeId = Guid.NewGuid();
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Node, "Узел") });
        await s.HK.SetNodeRootCardLevelAsync(
            cardId, HKObjectLevel.Aggregate, await VersionAsync(s, cardId));

        // Старший верхний уровень меняется — выбор сбрасывается.
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Aggregate, "Агрегат") });
        await using (var after = _fixture.CreateScope())
        {
            var card = await after.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
            Assert.Null(card.NodeRootCardLevel);
            Assert.Equal(HKObjectLevel.EquipmentModel, card.ObjectLevel);
        }

        // Возврат к узлу: новый выбор требуется явно.
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Node, "Узел снова") });

        await using var read = As(_fixture.NormAdminA2.Id);
        var projection = await read.HK.GetLevelProjectionAsync(cardId);
        Assert.True(projection.NeedsNodeRootChoice);
        Assert.Equal(HKObjectLevel.Undefined,
            (await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).ObjectLevel);
    }

    // ── Сценарии конечных строк ──

    /// <summary>Добавление первой строки под корнем: Undefined → Node.</summary>
    [Fact]
    public async Task AddingFirstRowUnderRoot_ChangesLevelUndefinedToNode()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        Assert.Equal(HKObjectLevel.Undefined, await LevelAsync(cardId));

        var card = await s.HK.GetByIdAsync(cardId);
        card!.Items.Add(new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = await UnitAsync(),
            Quantity = 1,
            SortOrder = 1
        });
        await s.HK.UpdateAsync(card);

        Assert.Equal(HKObjectLevel.Node, await LevelAsync(cardId));
    }

    /// <summary>Удаление последней строки при отсутствии позиций: Node → Undefined.</summary>
    [Fact]
    public async Task RemovingLastRowWithoutPositions_ChangesLevelNodeToUndefined()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        Assert.Equal(HKObjectLevel.Node, await LevelAsync(cardId));

        var card = await s.HK.GetByIdAsync(cardId);
        card!.Items.Clear();
        await s.HK.UpdateAsync(card);

        Assert.Equal(HKObjectLevel.Undefined, await LevelAsync(cardId));
    }

    /// <summary>
    /// Перенос строки под позицию сохраняет её идентификатор, нормы и материалы.
    /// Без переноса узла пересчёт видел бы прежнюю привязку.
    /// </summary>
    [Fact]
    public async Task MovingRowUnderPosition_KeepsIdNormsAndMaterials()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var unitId = await UnitAsync();
        var gsmId = await GsmAsync();

        var draft = await s.HK.GetByIdAsync(cardId);
        var itemId = Guid.NewGuid();
        draft!.Items.Add(new HKCardItem
        {
            Id = itemId,
            AssemblyUnitId = unitId,
            Quantity = 4,
            Volume = 2.5m,
            SortOrder = 1,
            Materials =
            {
                new HKCardItemMaterial
                {
                    Id = Guid.NewGuid(),
                    GsmMaterialId = gsmId,
                    Category = GsmCategory.Primary
                }
            }
        });
        await s.HK.UpdateAsync(draft);

        Assert.Equal(HKObjectLevel.Node, await LevelAsync(cardId));

        // Верхняя позиция-агрегат поднимает уровень до изделия.
        var nodeId = Guid.NewGuid();
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Aggregate, "Агрегат") });
        Assert.Equal(HKObjectLevel.EquipmentModel, await LevelAsync(cardId));

        // Перенос той же строки под позицию.
        var moving = await s.HK.GetByIdAsync(cardId);
        moving!.Items.Single().HKCardNodeId = nodeId;
        await s.HK.UpdateAsync(moving);

        await using var read = _fixture.CreateScope();
        var stored = await read.Db.HKCardItems.AsNoTracking()
            .SingleAsync(i => i.HKCardId == cardId);

        Assert.Equal(itemId, stored.Id);
        Assert.Equal(nodeId, stored.HKCardNodeId);
        Assert.Equal(4, stored.Quantity);
        Assert.Equal(2.5m, stored.Volume);
        Assert.Equal(1, await read.Db.HKCardItemMaterials.CountAsync(
            m => m.HKCardItemId == itemId && m.GsmMaterialId == gsmId));
    }

    /// <summary>Перенос строки обратно под корень сохраняет её данные.</summary>
    [Fact]
    public async Task MovingRowBackUnderRoot_PersistsNullNode()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var nodeId = Guid.NewGuid();
        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(nodeId, null, HKObjectLevel.Aggregate, "Агрегат") });

        var card = await s.HK.GetByIdAsync(cardId);
        var itemId = Guid.NewGuid();
        card!.Items.Add(new HKCardItem
        {
            Id = itemId,
            HKCardNodeId = nodeId,
            AssemblyUnitId = await UnitAsync(),
            Quantity = 1,
            SortOrder = 1
        });
        await s.HK.UpdateAsync(card);

        // Обратно под корень.
        var back = await s.HK.GetByIdAsync(cardId);
        back!.Items.Single().HKCardNodeId = null;
        await s.HK.UpdateAsync(back);

        await using var read = _fixture.CreateScope();
        var stored = await read.Db.HKCardItems.AsNoTracking()
            .SingleAsync(i => i.HKCardId == cardId);

        Assert.Equal(itemId, stored.Id);
        Assert.Null(stored.HKCardNodeId);
    }

    [Fact]
    public async Task UpdatingSeveralRowsAtOnce_RecomputesLevelFromFinalSet()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var unitId = await UnitAsync();
        var card = await s.HK.GetByIdAsync(cardId);
        for (var i = 1; i <= 3; i++)
        {
            card!.Items.Add(new HKCardItem
            {
                Id = Guid.NewGuid(),
                AssemblyUnitId = unitId,
                Quantity = i,
                SortOrder = i
            });
        }
        await s.HK.UpdateAsync(card);
        Assert.Equal(HKObjectLevel.Node, await LevelAsync(cardId));

        // Одновременное изменение: две строки остаются, одна удаляется.
        var edit = await s.HK.GetByIdAsync(cardId);
        var removedId = edit!.Items.OrderBy(i => i.SortOrder).First().Id;
        var toRemove = edit.Items.First(i => i.Id == removedId);
        edit.Items.Remove(toRemove);
        foreach (var row in edit.Items)
        {
            row.Quantity += 1;
        }
        await s.HK.UpdateAsync(edit);

        await using var read = _fixture.CreateScope();
        Assert.Equal(2, await read.Db.HKCardItems.CountAsync(i => i.HKCardId == cardId));
        Assert.False(await read.Db.HKCardItems.AsNoTracking()
            .AnyAsync(i => i.Id == removedId));
        Assert.Equal(HKObjectLevel.Node, await LevelAsync(cardId));
    }

    /// <summary>Правка норм и материалов без изменения структуры не меняет уровень.</summary>
    [Fact]
    public async Task SavingNormsAndMaterials_KeepsLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        var before = await LevelAsync(cardId);
        var gsmId = await GsmAsync();

        var card = await s.HK.GetByIdAsync(cardId);
        var row = card!.Items.Single();
        row.Quantity = 9;
        row.Materials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            GsmMaterialId = gsmId,
            Category = GsmCategory.Reserve
        });
        await s.HK.UpdateAsync(card);

        Assert.Equal(before, await LevelAsync(cardId));
        await using var read = _fixture.CreateScope();
        Assert.Equal(1, await read.Db.HKCardItemMaterials.CountAsync(
            m => m.HKCardItemId == row.Id && m.GsmMaterialId == gsmId));
    }

    /// <summary>
    /// Конфликт версии не оставляет частичного пересчёта: уровень соответствует
    /// сохранённым данным, а не той структуре, которую команда готовила.
    /// </summary>
    [Fact]
    public async Task VersionConflict_LeavesNoPartialLevelRecalculation()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        Assert.Equal(HKObjectLevel.Undefined, await LevelAsync(cardId));

        var card = await s.HK.GetByIdAsync(cardId);
        card!.Items.Add(new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = await UnitAsync(),
            Quantity = 1,
            SortOrder = 1
        });

        // Конкурентное изменение версии между загрузкой и сохранением.
        var stale = card;
        await using (var other = _fixture.CreateScope())
        {
            var row = await other.Db.HKCards.FirstAsync(c => c.Id == cardId);
            row.UpdatedAt = DateTime.UtcNow.AddMinutes(5);
            await other.Db.SaveChangesAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => s.HK.UpdateAsync(stale));

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardItems.CountAsync(i => i.HKCardId == cardId));
        Assert.Equal(HKObjectLevel.Undefined,
            (await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).ObjectLevel);
    }

    private async Task<Guid> UnitAsync()
    {
        await using var s = _fixture.CreateScope();
        var unit = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "АУ-" + Suffix(),
            Name = "СЕ тест"
        };
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        return unit.Id;
    }

    /// <summary>
    /// Марка, пригодная для новой строки ХК: опубликована и классифицирована.
    /// </summary>
    private async Task<Guid> GsmAsync()
    {
        await using var s = _fixture.CreateScope();
        var gsm = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "М-" + Suffix(),
            Nd = "ТУ " + Suffix()
        };
        s.Db.GsmMaterials.Add(gsm);
        s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        {
            Id = Guid.NewGuid(),
            GsmMaterialId = gsm.Id,
            GroupName = "Группа " + Suffix(),
            SubgroupName = "Подгруппа " + Suffix()
        });
        await s.Db.SaveChangesAsync();
        return gsm.Id;
    }

    // ── Согласование ──

    /// <summary>
    /// Пустое дерево не блокирует отправку реквизитов: первый этап этого не
    /// требует.
    /// </summary>
    [Fact]
    public async Task SubmitDetails_EmptyStructure_IsAllowed()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);

        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details)).State);
    }

    /// <summary>Отсутствие выбора верхнего узла блокирует отправку состава.</summary>
    [Fact]
    public async Task SubmitComposition_TopNodeWithoutChoice_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HKApprovalScenario.SubmitCompositionAsync(s, cardId));
    }

    /// <summary>После выбора отправка состава доступна.</summary>
    [Fact]
    public async Task SubmitComposition_AfterChoice_IsAllowed()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);

        await s.HK.SetNodeRootCardLevelAsync(
            cardId, HKObjectLevel.Aggregate, await VersionAsync(s, cardId));

        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);
    }

    /// <summary>
    /// Принятие фиксирует в снимке тот уровень, который был определён из
    /// согласованной структуры.
    /// </summary>
    [Fact]
    public async Task AcceptComposition_SnapshotContainsAgreedLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }

        await using var read = As(_fixture.NormAdminA2.Id);
        var accepted = await read.Revisions.GetAcceptedAsync(cardId);

        Assert.NotNull(accepted);
        Assert.Equal(HKObjectLevel.Node, accepted!.ObjectLevel);

        await using var revision = _fixture.CreateScope();
        var stored = await revision.Db.HKCardRevisions.AsNoTracking()
            .FirstAsync(r => r.Id == accepted.RevisionId);
        Assert.Equal(HKObjectLevel.Node, stored.ObjectLevel);
    }

    /// <summary>
    /// Принятая карта не пересчитывается командой сохранения: её уровень и
    /// снимок остаются прежними.
    /// </summary>
    [Fact]
    public async Task AcceptedCard_LevelIsNotRecalculatedBySaveCommands()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }

        var levelBefore = await LevelAsync(cardId);

        await using var writer = As(_fixture.NormAdminA.Id);
        var treeVersion = await VersionAsync(writer, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.HK.SaveTreeAsync(cardId,
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие") },
                treeVersion));

        Assert.Equal(levelBefore, await LevelAsync(cardId));

        await using var read = As(_fixture.NormAdminA2.Id);
        var accepted = await read.Revisions.GetAcceptedAsync(cardId);
        Assert.Equal(HKObjectLevel.Node, accepted!.ObjectLevel);
    }

    // ── Чтение ──

    /// <summary>GET не меняет данные: уровень остаётся прежним.</summary>
    [Fact]
    public async Task GetLevelProjection_DoesNotChangeData()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        var versionBefore = await VersionAsync(s, cardId);

        var first = await s.HK.GetLevelProjectionAsync(cardId);
        var second = await s.HK.GetLevelProjectionAsync(cardId);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.CardLevel, second.CardLevel);
        Assert.Equal(versionBefore, await VersionAsync(s, cardId));
    }

    /// <summary>Реестр показывает реальный вычисленный уровень автономной карты.</summary>
    [Fact]
    public async Task Registry_ShowsRealLevelOfAutonomousCard()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат") });

        var row = await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => new { c.ObjectLevel, c.IsAutonomous, c.ComplexId, c.NodeId })
            .FirstAsync();

        Assert.True(row.IsAutonomous);
        Assert.Equal(HKObjectLevel.EquipmentModel, row.ObjectLevel);

        // Автономная карта не получает справочную привязку из-за уровня.
        Assert.Null(row.ComplexId);
        Assert.Null(row.NodeId);
    }

    /// <summary>Вычисление уровня не меняет название, номер и версию.</summary>
    [Fact]
    public async Task LevelCalculation_DoesNotChangeCodeOrName()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await using var before = _fixture.CreateScope();
        var beforeCard = await before.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => new { c.Code, c.Name, c.Version }).FirstAsync();

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие") });

        await using var after = _fixture.CreateScope();
        var afterCard = await after.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => new { c.Code, c.Name, c.Version }).FirstAsync();

        Assert.Equal(beforeCard.Code, afterCard.Code);
        Assert.Equal(beforeCard.Name, afterCard.Name);
        Assert.Equal(beforeCard.Version, afterCard.Version);
        Assert.StartsWith("ХК-", afterCard.Code);
    }

    // ── Реальная проекция реестра ──

    /// <summary>
    /// Реестр отдаёт фактический вычисленный уровень автономной карты через
    /// настоящую проекцию GetRegistryPageAsync, а не прямым чтением HKCards.
    /// Справочные ObjectCode/ObjectName остаются пустыми: у автономной карты их
    /// нет, и фиктивно они не заполняются.
    /// </summary>
    [Fact]
    public async Task RegistryProjection_ShowsDerivedLevelOfAutonomousCard()
    {
        await using var s = As(_fixture.NormAdminA.Id);

        var equipmentCard = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);
        await s.HK.SaveTreeAsync(equipmentCard,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие") });

        var aggregateCard = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);
        await s.HK.SaveTreeAsync(aggregateCard,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат") });

        var emptyCard = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var page = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery
        {
            Page = 1,
            PageSize = 500
        });

        var rows = page.Items
            .Where(r => r.Id == equipmentCard || r.Id == aggregateCard || r.Id == emptyCard)
            .ToList();

        Assert.Equal(3, rows.Count);

        var complex = rows.Single(r => r.Id == equipmentCard);
        Assert.True(complex.IsAutonomous);
        Assert.Equal(HKObjectLevel.Complex, complex.ObjectLevel);
        Assert.Null(complex.ObjectCode);
        Assert.Null(complex.ObjectName);

        var equipment = rows.Single(r => r.Id == aggregateCard);
        Assert.Equal(HKObjectLevel.EquipmentModel, equipment.ObjectLevel);

        var undefined = rows.Single(r => r.Id == emptyCard);
        Assert.Equal(HKObjectLevel.Undefined, undefined.ObjectLevel);
    }

    /// <summary>
    /// Фильтр уровня работает по вычисленному уровню и не зависит от наличия
    /// справочных привязок.
    /// </summary>
    [Fact]
    public async Task RegistryProjection_FiltersByDerivedLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);

        var equipmentCard = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);
        await s.HK.SaveTreeAsync(equipmentCard,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие") });

        var page = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery
        {
            Page = 1,
            PageSize = 500,
            ObjectLevel = HKObjectLevel.Complex
        });

        Assert.Contains(page.Items, r => r.Id == equipmentCard);

        // Фильтр не требует справочной привязки: ComplexId остаётся пустым.
        await using var read = _fixture.CreateScope();
        Assert.Null((await read.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == equipmentCard)).ComplexId);
    }

    /// <summary>
    /// DTO чтения отдаёт сохранённый уровень, специальный выбор, признак
    /// необходимости выбора и понятную причину. GET ничего не записывает.
    /// </summary>
    [Fact]
    public async Task DetailProjection_CarriesLevelChoiceAndReason()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        var versionBefore = await VersionAsync(s, cardId);

        var card = await s.HK.GetByIdAsync(cardId);
        var projection = await s.HK.GetLevelProjectionAsync(cardId);

        Assert.NotNull(projection);
        Assert.Null(card!.NodeRootCardLevel);
        Assert.Equal(HKObjectLevel.Undefined, card.ObjectLevel);
        Assert.True(projection.NeedsNodeRootChoice);
        Assert.Equal(HKCardLevelStatus.NodeRootChoiceRequired, projection.Status);

        // GET ничего не записал.
        Assert.Equal(versionBefore, await VersionAsync(s, cardId));
        await using var read = _fixture.CreateScope();
        Assert.Null((await read.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == cardId)).NodeRootCardLevel);
    }

    // ── Доступ ──

    /// <summary>Выбор требует HK.Work: итоговый уровень не даёт дополнительных прав.</summary>
    [Fact]
    public async Task SetNodeRootLevel_WithoutHkWork_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Node, "Узел") });

        // Пользователь без HK.Work: чужой филиал отсекается раньше по ветке.
        await using var guest = As(_fixture.GuestA.Id);
        var guestVersion = await VersionAsync(guest, cardId);
        await Assert.ThrowsAnyAsync<Exception>(
            () => guest.HK.SetNodeRootCardLevelAsync(
                cardId, HKObjectLevel.Aggregate, guestVersion));

        await using var read = _fixture.CreateScope();
        Assert.Null((await read.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == cardId)).NodeRootCardLevel);
    }
}