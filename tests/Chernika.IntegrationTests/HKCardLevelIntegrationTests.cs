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

    [Fact]
    public async Task SaveTree_TopComplex_SetsComplexLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await s.HK.SaveTreeAsync(cardId,
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Complex, "Комплекс") });

        Assert.Equal(HKObjectLevel.Complex, await LevelAsync(cardId));
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
                new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Complex, "Комплекс") },
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
            new[] { Node(Guid.NewGuid(), null, HKObjectLevel.Complex, "Комплекс") });

        await using var after = _fixture.CreateScope();
        var afterCard = await after.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => new { c.Code, c.Name, c.Version }).FirstAsync();

        Assert.Equal(beforeCard.Code, afterCard.Code);
        Assert.Equal(beforeCard.Name, afterCard.Name);
        Assert.Equal(beforeCard.Version, afterCard.Version);
        Assert.StartsWith("ХК-", afterCard.Code);
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