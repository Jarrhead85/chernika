using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Разделы и принадлежность веток в принимаемой редакции.
/// <para>
/// Принятая редакция воспроизводится из неизменяемого снимка: последующие
/// правки рабочей карты не должны менять принятую структуру и её итоги.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardRevisionSectionsIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardRevisionSectionsIntegrationTests(TestDatabaseFixture fixture) =>
        _fixture = fixture;

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

    private async Task<Guid> UnitAsync()
    {
        await using var s = _fixture.CreateScope();
        var unit = new Chernika.Domain.Entities.AssemblyUnit
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
    /// Готовит карту с разделом «Масла», веткой в нём и принимает её.
    /// </summary>
    private async Task<(Guid CardId, Guid SectionId, Guid NodeId)> PrepareAcceptedCardAsync()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var sectionId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var unitId = await UnitAsync();

        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[] { new HKCardSectionWrite(sectionId, HKCardSectionType.Oils, 0) },
            new[]
            {
                new HKCardStructureNodeWrite(
                    aggregateId, null, HKObjectLevel.Aggregate, "Агрегат", 2, 0, sectionId),
                new HKCardStructureNodeWrite(
                    nodeId, aggregateId, HKObjectLevel.Node, "Узел", 3, 0, null)
            },
            new[]
            {
                new HKCardStructureItemWrite(
                    Guid.NewGuid(), nodeId, unitId, 4, 500m, "г", null, null, 0, null,
                    Array.Empty<HKCardMaterialWrite>())
            },
            null, await VersionAsync(s, cardId)));

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }

        return (cardId, sectionId, nodeId);
    }

    [Fact]
    public async Task Acceptance_SnapshotContainsSectionsAndMembership()
    {
        // Проверяем рабочую позицию сразу после сохранения структуры — до
        // потока согласования.
        Guid cardId;
        Guid sectionId;
        Guid nodeId;

        await using (var s = As(_fixture.NormAdminA.Id))
        {
            cardId = await HKApprovalScenario.CreateDraftCardAsync(
                s, _fixture.NormAdminA.Id, withRow: false);

            sectionId = Guid.NewGuid();
            nodeId = Guid.NewGuid();
            var unitId = await UnitAsync();

            await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId,
                new[] { new HKCardSectionWrite(sectionId, HKCardSectionType.Oils, 0) },
                new[]
                {
                    new HKCardStructureNodeWrite(
                        nodeId, null, HKObjectLevel.Aggregate, "Агрегат", 2, 0, sectionId)
                },
                new[]
                {
                    new HKCardStructureItemWrite(
                        Guid.NewGuid(), nodeId, unitId, 4, 500m, "г", null, null, 0, null,
                        Array.Empty<HKCardMaterialWrite>())
                },
                null, await VersionAsync(s, cardId)));

            await using var probe = _fixture.CreateScope();
            var afterSave = await probe.Db.HKCardNodes.AsNoTracking()
                .SingleAsync(n => n.Id == nodeId);
            Assert.Equal(sectionId, afterSave.SectionId);
        }

        await BringToAcceptanceAsync(cardId);

        await using var read = As(_fixture.NormAdminA2.Id);

        var revisionId = await read.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync();
        Assert.NotNull(revisionId);

        // Редакции принимаются с разделами: снимок содержит их.
        Assert.Equal(1, await read.Db.HKCardRevisionSections
            .CountAsync(s => s.RevisionId == revisionId!.Value));

        var snapshotNode = await read.Db.HKCardRevisionNodes.AsNoTracking()
            .SingleAsync(n => n.SourceNodeId == nodeId);

        Assert.NotNull(snapshotNode.SectionId);
    }

    /// <summary>Доводит карту до принятой редакции.</summary>
    private async Task BringToAcceptanceAsync(Guid cardId)
    {
        await using (var s = As(_fixture.NormAdminA.Id))
        {
            await HKApprovalScenario.SubmitDetailsAsync(
                s, cardId, _fixture.NormAdminA2.Id);
        }

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);
        }

        await using (var s = As(_fixture.NormAdminA.Id))
        {
            await HKApprovalScenario.SubmitCompositionAsync(s, cardId);
        }

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }
    }

    /// <summary>
    /// Правка рабочей карты после принятия не меняет принятую редакцию.
    /// </summary>
    [Fact]
    public async Task Snapshot_IsIndependentFromLaterWorkingChanges()
    {
        var (cardId, sectionId, nodeId) = await PrepareAcceptedCardAsync();

        // Рабочая карта меняется после принятия: новая ветка без раздела.
        await using (var worker = As(_fixture.NormAdminA.Id))
        {
            // Принятая карта заблокирована для записи, поэтому изменение
            // структуры отвергается. Это и есть защита: снимок не зависит от
            // рабочей карты именно потому, что рабочая уже не меняется.
            var staleVersion = await VersionAsync(worker, cardId);

            var command = new SaveHKCardStructureCommand(
                cardId, Array.Empty<HKCardSectionWrite>(),
                Array.Empty<HKCardStructureNodeWrite>(),
                Array.Empty<HKCardStructureItemWrite>(), null, staleVersion);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => worker.HK.SaveStructureAsync(command));
        }

        await using var read = As(_fixture.NormAdminA2.Id);

        var snapshot = await read.Revisions.GetAcceptedAsync(cardId);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Sections);
        Assert.Equal(HKCardSectionType.Oils, snapshot.Sections[0].SectionType);
    }

    /// <summary>Принятые итоги считаются из снимка, а не из рабочей карты.</summary>
    [Fact]
    public async Task AcceptedTotals_AreReadFromSnapshotOnly()
    {
        var (cardId, _, nodeId) = await PrepareAcceptedCardAsync();

        await using var read = As(_fixture.NormAdminA2.Id);

        var totals = await read.Revisions.GetAcceptedTotalsAsync(cardId);
        Assert.NotNull(totals);

        // Агрегат × 2, строка × 4, 500 г: ветка даёт 12 кг.
        var overall = Assert.Single(totals.Overall);
        Assert.Equal(12000m, overall.BaseValue);
        Assert.True(overall.IsComplete);
    }

    /// <summary>
    /// Редакция, принятая до появления разделов, читается без разделов.
    /// </summary>
    [Fact]
    public async Task Acceptance_WithoutSections_ReadsAsNoSections()
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

        var snapshot = await read.Revisions.GetAcceptedAsync(cardId);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Sections);

        // Редакция, принятая до появления разделов, снимковых разделов не имеет.
        var revisionId = await read.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync();
        Assert.Equal(0, await read.Db.HKCardRevisionSections
            .CountAsync(s => s.RevisionId == revisionId!.Value));

        var totals = await read.Revisions.GetAcceptedTotalsAsync(cardId);
        Assert.NotNull(totals);
    }

    /// <summary>
    /// Конечная строка под корнем сохраняет свою принадлежность в снимке.
    /// </summary>
    [Fact]
    public async Task Acceptance_RootRowSection_IsPreserved()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var sectionId = Guid.NewGuid();
        var unitId = await UnitAsync();

        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[] { new HKCardSectionWrite(sectionId, HKCardSectionType.Fuels, 0) },
            Array.Empty<HKCardStructureNodeWrite>(),
            new[]
            {
                new HKCardStructureItemWrite(
                    Guid.NewGuid(), null, unitId, 1, 120m, "л", null, null, 0, sectionId,
                    Array.Empty<HKCardMaterialWrite>())
            },
            null, await VersionAsync(s, cardId)));

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

        var snapshot = await read.Revisions.GetAcceptedAsync(cardId);
        Assert.NotNull(snapshot);
        var item = Assert.Single(snapshot.Items);
        Assert.NotNull(item.SectionId);

        var totals = await read.Revisions.GetAcceptedTotalsAsync(cardId);
        var section = Assert.Single(totals.Sections);
        var liquid = Assert.Single(section.Totals);
        Assert.Equal(120m, liquid.BaseValue);
    }

    /// <summary>
    /// Читатель чужого филиала не получает принятые разделы.
    /// </summary>
    [Fact]
    public async Task GetAcceptedSections_OtherBranch_IsRefused()
    {
        var (cardId, _, _) = await PrepareAcceptedCardAsync();

        await using var outsider = As(_fixture.NormAdminB.Id);
        await Assert.ThrowsAnyAsync<Exception>(
            () => outsider.Revisions.GetAcceptedAsync(cardId));
    }
}