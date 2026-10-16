using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Окончательное принятие состава: одна команда связывает решение согласующего,
/// снимок структуры, указатель, состояние этапа, статус карты, задачи, аудит и
/// уведомление.
/// </summary>
[Collection("Database")]
public class HKCompositionApprovalIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCompositionApprovalIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<TestScope> AsAsync(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    private Task<TestScope> ApproverAsync() => AsAsync(_fixture.NormAdminA2.Id);

    /// <summary>Карта, прошедшая приём реквизитов и отправленная на состав.</summary>
    private async Task<Guid> CardReadyForCompositionAsync(TestScope s)
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        await HKApprovalScenario.SubmitDetailsAsync(
            await AsAsync(_fixture.NormAdminA.Id), cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(
            await ApproverAsync(), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(
            await AsAsync(_fixture.NormAdminA.Id), cardId);
        return cardId;
    }

    /// <summary>
    /// Доводит подготовленную карту до состояния «состав на рассмотрении».
    /// Отличается от CardReadyForCompositionAsync тем, что карта уже создана и
    /// может нести собственное дерево и материалы.
    /// </summary>
    private async Task BringToCompositionReviewAsync(Guid cardId)
    {
        await HKApprovalScenario.SubmitDetailsAsync(
            await AsAsync(_fixture.NormAdminA.Id), cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(await ApproverAsync(), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(
            await AsAsync(_fixture.NormAdminA.Id), cardId);
    }

    private async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();
    }

    // ── Успех ──

    [Fact]
    public async Task ApproveComposition_LinksEverythingAtomically()
    {
        Guid cardId;
        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);
        }

        Guid revisionId;
        await using (var approver = await ApproverAsync())
        {
            var version = await VersionAsync(approver, cardId);
            await approver.Approvals.ApproveCompositionAsync(cardId, "состав согласован", version);

            var revision = await approver.Db.HKCardRevisions.AsNoTracking()
                .OrderByDescending(r => r.RevisionNumber).FirstAsync(r => r.HKCardId == cardId);
            revisionId = revision.Id;
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);

        // Указатель ведёт на новую редакцию СВОЕЙ карты.
        var card = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, card.Status);
        Assert.Equal(revisionId, card.CurrentAcceptedRevisionId);
        Assert.NotNull(await check.Db.HKCardRevisions
            .FirstOrDefaultAsync(r => r.Id == card.CurrentAcceptedRevisionId && r.HKCardId == cardId));

        // Оба этапа приняты, дата решения состава записана.
        var details = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Details);
        var composition = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition);
        Assert.Equal(HKApprovalStageState.Approved, details.State);
        Assert.Equal(HKApprovalStageState.Approved, composition.State);
        Assert.NotNull(composition.ApprovedAtUtc);
        Assert.NotNull(details.ApprovedAtUtc);

        // Снимок несёт данные решения, а не техническое время фиксации.
        var revisionEntity = await check.Db.HKCardRevisions.AsNoTracking()
            .FirstAsync(r => r.Id == revisionId);
        Assert.NotNull(revisionEntity.AcceptedAtUtc);
        Assert.Equal(_fixture.NormAdminA2.Id, revisionEntity.AcceptedByUserId);
        Assert.Equal(composition.ApprovedAtUtc, revisionEntity.AcceptedAtUtc);
    }

    /// <summary>Снимок соответствует сохранённой рабочей структуре.</summary>
    [Fact]
    public async Task ApproveComposition_SnapshotMatchesWorkingStructure()
    {
        Guid cardId;
        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CreateCardWithNodeAsync(s);
        }
        await BringToCompositionReviewAsync(cardId);

        await using (var approver = await ApproverAsync())
        {
            var version = await VersionAsync(approver, cardId);
            await approver.Approvals.ApproveCompositionAsync(cardId, null, version);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        var accepted = await check.Revisions.GetAcceptedAsync(cardId);

        Assert.NotNull(accepted);
        Assert.Single(accepted!.Nodes);
        Assert.Single(accepted.Items);
        Assert.True(accepted.Items[0].Materials.Count > 0);
        Assert.NotNull(accepted.AcceptedAtUtc);
    }

    private async Task<Guid> CreateCardWithNodeAsync(TestScope s)
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        var node = new HKCardNode
        {
            Id = Guid.NewGuid(),
            HKCardId = cardId,
            Level = HKObjectLevel.Node,
            Name = "Узел " + Suffix(),
            Quantity = 1,
            SortOrder = 1
        };
        s.Db.HKCardNodes.Add(node);

        var item = await s.Db.HKCardItems.FirstAsync(i => i.HKCardId == cardId);
        item.HKCardNodeId = node.Id;

        var gsm = new GsmMaterial { Id = Guid.NewGuid(), Name = "М-" + Suffix(), Nd = "ТУ" };
        s.Db.GsmMaterials.Add(gsm);
        await s.Db.SaveChangesAsync();
        s.Db.HKCardItemMaterials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(), HKCardItemId = item.Id,
            GsmMaterialId = gsm.Id, Category = GsmCategory.Primary
        });
        await s.Db.SaveChangesAsync();

        // Старшая верхняя позиция — узел: уровень без явного выбора не
        // определён, и отправка состава была бы недоступна.
        await HKApprovalScenario.ChooseNodeRootLevelAsync(
            s, cardId, HKObjectLevel.Aggregate);

        return cardId;
    }

    /// <summary>Сроки действия и дата принятия реквизитов не меняются.</summary>
    [Fact]
    public async Task ApproveComposition_KeepsDetailsDateAndValidityPeriods()
    {
        Guid cardId;
        DateTime? effectiveBefore;
        DateTime? expirationBefore;
        DateTime detailsApprovedBefore;

        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);

            var card = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
            effectiveBefore = card.EffectiveDate;
            expirationBefore = card.ExpirationDate;

            var details = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
            detailsApprovedBefore = details.ApprovedAtUtc!.Value;
        }

        await using (var approver = await ApproverAsync())
        {
            var version = await VersionAsync(approver, cardId);
            await approver.Approvals.ApproveCompositionAsync(cardId, null, version);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        var after = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var detailsAfter = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Details);

        Assert.Equal(effectiveBefore, after.EffectiveDate);
        Assert.Equal(expirationBefore, after.ExpirationDate);

        // Дата принятия реквизитов не переписывается вторым этапом.
        Assert.Equal(detailsApprovedBefore, detailsAfter.ApprovedAtUtc!.Value);
    }

    /// <summary>Задача рассмотрения закрыта, уведомление автору создано ровно одно.</summary>
    [Fact]
    public async Task ApproveComposition_ClosesTaskAndNotifiesAuthor()
    {
        Guid cardId;
        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);
        }

        await using (var approver = await ApproverAsync())
        {
            var version = await VersionAsync(approver, cardId);
            await approver.Approvals.ApproveCompositionAsync(cardId, null, version);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);

        // Открытой задачи рассмотрения состава не осталось.
        Assert.Equal(0, await check.Db.WorkTasks.CountAsync(t =>
            !t.IsDeleted && t.EntityId == cardId
            && t.HKStage == HKApprovalStage.Composition
            && (t.Status == WorkTaskStatus.Open
                || t.Status == WorkTaskStatus.InProgress
                || t.Status == WorkTaskStatus.Overdue)));

        // Задача рассмотрения реквизитов тоже закрыта, но её история сохранена.
        Assert.True(await check.Db.WorkTasks.AnyAsync(t => t.EntityId == cardId && !t.IsDeleted));

        // Одно уведомление автору.
        var notifications = await check.Db.Notifications
            .AsNoTracking()
            .Where(n => n.EntityId == cardId && n.Type == NotificationType.HKApproved)
            .ToListAsync();
        Assert.Single(notifications);

        // Журнал и аудит решения записаны.
        Assert.True(await check.Db.HKCardStatusLogs.AsNoTracking()
            .AnyAsync(l => l.HKCardId == cardId && l.ToStatus == HKCardStatus.Approved));
        Assert.True(await check.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityId == cardId.ToString() && a.Action == "CompositionApproved"));
    }

    // ── Отказы по правам ──

    [Fact]
    public async Task ApproveComposition_WithoutApprovePermission_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyForCompositionAsync(s);

        // Назначенный согласующий без действующего HK.Approve.
        var reader = await CreateApproverWithoutApproveAsync();
        var version = await VersionAsync(reader, cardId);

        try
        {
            // Назначение меняется ТОЛЬКО у этой карты и восстанавливается.
            await AssignCompositionApproverAsync(cardId, reader.User.CurrentUserId!.Value.ToString());

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => reader.Approvals.ApproveCompositionAsync(cardId, null, version));
        }
        finally
        {
            await AssignCompositionApproverAsync(cardId, _fixture.NormAdminA2.Id);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        Assert.Null(await check.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync());
    }

    [Fact]
    public async Task ApproveComposition_ByOtherUser_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyForCompositionAsync(s);

        // HK.Approve есть, но назначен согласующий другой.
        var stranger = await AsAsync(_fixture.NormAdminA.Id);
        var version = await VersionAsync(stranger, cardId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stranger.Approvals.ApproveCompositionAsync(cardId, null, version));
    }

    [Fact]
    public async Task ApproveComposition_OtherBranch_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyForCompositionAsync(s);

        await using var outsider = await AsAsync(_fixture.NormAdminB.Id);
        var version = await VersionAsync(outsider, cardId);

        await Assert.ThrowsAsync<HkCardAccessDeniedException>(
            () => outsider.Approvals.ApproveCompositionAsync(cardId, null, version));
    }

    [Fact]
    public async Task ApproveComposition_DeactivatedApprover_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyForCompositionAsync(s);

        var approver = await ApproverAsync();
        var version = await VersionAsync(approver, cardId);

        try
        {
            await SetUserActiveAsync(_fixture.NormAdminA2.Id, false);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));
        }
        finally
        {
            await SetUserActiveAsync(_fixture.NormAdminA2.Id, true);
        }
    }

    // ── Отказы по состоянию ──

    [Fact]
    public async Task ApproveComposition_BeforeDetailsApproved_IsRefused()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AsAsync(_fixture.NormAdminA.Id), _fixture.NormAdminA.Id);

        await using var approver = await ApproverAsync();
        var version = await VersionAsync(approver, cardId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));
    }

    [Fact]
    public async Task ApproveComposition_WhenCompositionNotOnReview_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyForCompositionAsync(s);

        await using var approver = await ApproverAsync();
        var version = await VersionAsync(approver, cardId);

        // Возврат состава уводит его с рассмотрения.
        await approver.Approvals.ReturnCompositionAsync(cardId, null, version);
        var afterReturn = await VersionAsync(approver, cardId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, afterReturn));
    }

    [Fact]
    public async Task ApproveComposition_WithStaleRowVersion_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyForCompositionAsync(s);

        await using var approver = await ApproverAsync();
        var stale = await VersionAsync(approver, cardId);

        // Карта изменяется после чтения версии.
        var card = await approver.Db.HKCards.FirstAsync(c => c.Id == cardId);
        card.UpdatedAt = DateTime.UtcNow;
        await approver.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<HkCardVersionConflictException>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, stale));
    }

    /// <summary>Повторный запрос после успеха не создаёт следующую редакцию.</summary>
    [Fact]
    public async Task ApproveComposition_RepeatedAfterSuccess_CreatesNoNewRevision()
    {
        Guid cardId;
        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);
        }

        await using (var approver = await ApproverAsync())
        {
            var version = await VersionAsync(approver, cardId);
            await approver.Approvals.ApproveCompositionAsync(cardId, null, version);
        }

        await using (var again = await ApproverAsync())
        {
            var version = await VersionAsync(again, cardId);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => again.Approvals.ApproveCompositionAsync(cardId, null, version));
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        Assert.Equal(1, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Equal(1, await check.Db.Notifications.AsNoTracking()
            .CountAsync(n => n.EntityId == cardId && n.Type == NotificationType.HKApproved));
    }

    /// <summary>Общий переключатель статуса не утверждает карту в обход согласования.</summary>
    [Fact]
    public async Task ChangeStatus_CannotApproveCard()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyForCompositionAsync(s);

        var result = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved, "через общий переключатель");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        Assert.NotEqual(HKCardStatus.Approved,
            (await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).Status);
    }

    // ── Защита принятой карты от правки ──

    [Fact]
    public async Task AcceptedCard_BlocksAllWritePaths()
    {
        Guid cardId;
        Guid itemId;
        Guid nodeId;

        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CreateCardWithNodeAsync(s);
            itemId = await s.Db.HKCardItems.AsNoTracking()
                .Where(i => i.HKCardId == cardId).Select(i => i.Id).FirstAsync();
            nodeId = await s.Db.HKCardNodes.AsNoTracking()
                .Where(n => n.HKCardId == cardId).Select(n => n.Id).FirstAsync();
        }

        await BringToCompositionReviewAsync(cardId);

        await using (var approver = await ApproverAsync())
        {
            var version = await VersionAsync(approver, cardId);
            await approver.Approvals.ApproveCompositionAsync(cardId, null, version);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);

        // UpdateAsync.
        var card = await check.Db.HKCards.FirstAsync(c => c.Id == cardId);
        card.Notes = "ПРАВКА";
        await Assert.ThrowsAsync<InvalidOperationException>(() => check.HK.UpdateAsync(card));

        // SaveTreeAsync. Версия читается ДО вызова: await внутри не-async лямбды
        // недопустим.
        var nodeVersion = await VersionAsync(check, cardId);
        var tree = new List<HKCardNodeWrite>
        {
            new(nodeId, null, HKObjectLevel.Node, "ПРАВКА", 1, 1)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => check.HK.SaveTreeAsync(cardId, tree, nodeVersion));

        // Замена вложения.
        var bytes = "%PDF-1.4\n%% test\n%%EOF\n".Select(ch => (byte)ch).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            check.HK.SaveAttachmentAsync(cardId, new MemoryStream(bytes), "a.pdf", "application/pdf", bytes.Length));
    }

    // ── Конкуренция ──

    /// <summary>
    /// Настоящее одновременное принятие в двух DbContext: ровно одно решение
    /// успешно, создана ровно одна редакция, аудит успешного принятия один.
    /// </summary>
    [Fact]
    public async Task TwoSimultaneousApprovals_ExactlyOneWins()
    {
        Guid cardId;
        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);
        }

        uint version;
        await using (var seed = await ApproverAsync())
        {
            version = await VersionAsync(seed, cardId);
        }

        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForBothAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await gate.Task;
        }

        var outcomes = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var s = await ApproverAsync();
                await WaitForBothAsync();
                try { await s.Approvals.ApproveCompositionAsync(cardId, null, version); return "ok"; }
                catch (Exception ex) { return ex.GetType().Name; }
            }),
            Task.Run(async () =>
            {
                await using var s = await ApproverAsync();
                await WaitForBothAsync();
                try { await s.Approvals.ApproveCompositionAsync(cardId, null, version); return "ok"; }
                catch (Exception ex) { return ex.GetType().Name; }
            }));

        Assert.NotEmpty(outcomes);
        Assert.Equal(1, outcomes.Count(o => o == "ok"));

        var refused = outcomes.Single(o => o != "ok");
        // Конфликт версии либо нарушение уникальности номера редакции: оба
        // контролируемые и откатывают операцию целиком.
        Assert.True(refused is nameof(HkCardVersionConflictException)
                    or nameof(InvalidOperationException)
                    or nameof(DbUpdateException)
                    or "PostgresException"
                    or nameof(OperationCanceledException),
            "Неожиданный исход одновременного принятия: " + refused);

        await using var check = await AsAsync(_fixture.NormAdminA.Id);

        // Ровно одна принятая редакция и один актуальный указатель.
        Assert.Equal(1, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        var cardAfter = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, cardAfter.Status);
        Assert.NotNull(cardAfter.CurrentAcceptedRevisionId);

        // Аудит успешного принятия и уведомление — по одному.
        Assert.Equal(1, await check.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityId == cardId.ToString() && a.Action == "CompositionApproved"));
        Assert.Equal(1, await check.Db.Notifications.AsNoTracking()
            .CountAsync(n => n.EntityId == cardId && n.Type == NotificationType.HKApproved));
    }

    /// <summary>Принятие одновременно с возвратом: одно из решений побеждает.</summary>
    [Fact]
    public async Task ApprovalAndReturnSimultaneously_OneWins()
    {
        Guid cardId;
        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);
        }

        uint version;
        await using (var seed = await ApproverAsync())
        {
            version = await VersionAsync(seed, cardId);
        }

        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForBothAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await gate.Task;
        }

        var outcomes = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var s = await ApproverAsync();
                await WaitForBothAsync();
                try { await s.Approvals.ApproveCompositionAsync(cardId, null, version); return "approve"; }
                catch (Exception ex) { return "approve:" + ex.GetType().Name; }
            }),
            Task.Run(async () =>
            {
                await using var s = await ApproverAsync();
                await WaitForBothAsync();
                try { await s.Approvals.ReturnCompositionAsync(cardId, null, version); return "return"; }
                catch (Exception ex) { return "return:" + ex.GetType().Name; }
            }));

        Assert.Equal(1, outcomes.Count(o => o is "approve" or "return"));
        foreach (var loser in outcomes.Where(o => o.Contains(':')))
        {
            var type = loser.Split(':')[1];
            Assert.True(type is nameof(HkCardVersionConflictException)
                        or nameof(InvalidOperationException)
                        or nameof(DbUpdateException)
                        or "PostgresException"
                        or nameof(OperationCanceledException),
                "Неожиданный исход одновременных решений: " + loser);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        var composition = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition);

        // Итоговое состояние одно и соответствует победившему решению.
        if (outcomes.Contains("approve"))
        {
            Assert.Equal(HKApprovalStageState.Approved, composition.State);
            Assert.Equal(1, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        }
        else
        {
            Assert.Equal(HKApprovalStageState.RevisionRequired, composition.State);
            Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        }
    }

    // ── Атомарный откат ──

    /// <summary>
    /// Ошибка при записи снимковых строк откатывает ВСЮ операцию: указатель прежний,
    /// этап не принят, карта не Approved, задачи и уведомления не изменены.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_WhenSnapshotWriteFails_RollsBackEverything()
    {
        Guid cardId;
        int revisionsBefore;
        int tasksBefore;
        int notificationsBefore;

        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);
            revisionsBefore = await s.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId);
            tasksBefore = await s.Db.WorkTasks.CountAsync(t => t.EntityId == cardId && !t.IsDeleted);
            notificationsBefore = await s.Db.Notifications.CountAsync(n => n.EntityId == cardId);
        }

        // Строки есть у любой карты, поэтому точка внедрения достигается всегда.
        FailingCommandInterceptor.ArmAt("\"HKCardRevisionItems\"", 1);
        try
        {
            await using var approver = await ApproverAsync();
            var version = await VersionAsync(approver, cardId);
            await Assert.ThrowsAnyAsync<Exception>(
                () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));

            Assert.True(FailingCommandInterceptor.Fired,
                "Ошибка не была вставлена в нужную точку: откат не проверялся.");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);

        Assert.Equal(revisionsBefore, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Null(await check.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync());
        Assert.NotEqual(HKCardStatus.Approved,
            (await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).Status);

        var composition = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition);
        Assert.Equal(HKApprovalStageState.OnReview, composition.State);

        Assert.Equal(tasksBefore, await check.Db.WorkTasks.CountAsync(t => t.EntityId == cardId && !t.IsDeleted));
        Assert.Equal(notificationsBefore, await check.Db.Notifications.CountAsync(n => n.EntityId == cardId));
        Assert.Equal(0, await check.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityId == cardId.ToString() && a.Action == "CompositionApproved"));
    }

    /// <summary>
    /// Ошибка при закрытии задачи откатывает уже созданную редакцию и указатель:
    /// частично принятая карта возникнуть не может.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_WhenTaskWriteFails_RollsBackRevisionAndPointer()
    {
        Guid cardId;
        int revisionsBefore;

        await using (var s = await AsAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyForCompositionAsync(s);
            revisionsBefore = await s.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId);
        }

        // Ошибка на обновлении задач — после создания редакции.
        FailingCommandInterceptor.ArmAt("\"WorkTasks\"", 1);
        try
        {
            await using var approver = await ApproverAsync();
            var version = await VersionAsync(approver, cardId);
            await Assert.ThrowsAnyAsync<Exception>(
                () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);

        Assert.Equal(revisionsBefore, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Null(await check.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync());
        Assert.NotEqual(HKCardStatus.Approved,
            (await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).Status);
    }

    // ── Вспомогательное ──

    private async Task SetUserActiveAsync(string userId, bool active)
    {
        await using var s = _fixture.CreateScope();
        var user = await s.Db.Users.SingleAsync(u => u.Id == userId);
        user.IsActive = active;
        await s.Db.SaveChangesAsync();
    }

    private async Task<TestScope> CreateApproverWithoutApproveAsync()
    {
        await using var setup = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "noapprove_" + Suffix(),
            FullName = "Без права " + Suffix(),
            BranchId = _fixture.BranchA,
            IsActive = true
        };
        var result = await setup.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));

        await setup.Users.AddToRoleAsync(user, nameof(UserRole.Operator));

        // Назначаем его согласующим состава, но отнимаем HK.Approve.
        setup.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionCode = PermissionCodes.HKApprove,
            IsGranted = false,
            GrantedByUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await setup.Db.SaveChangesAsync();
        setup.Permissions.InvalidateCache(user.Id);

        var scope = await AsAsync(user.Id);
        return scope;
    }

    /// <summary>
    /// Назначает пользователя согласующим состава ТОЛЬКО у указанной карты.
    /// <para>
    /// Раньше обновлялись все этапы Composition общей тестовой базы, и назначение
    /// согласующего у чужих карт менялось вместе с этим тестом. Общая база живёт
    /// между тестами, поэтому такое изменение заражало последующие прогоны.
    /// </para>
    /// </summary>
    private async Task AssignCompositionApproverAsync(Guid cardId, string userId)
    {
        await using var assign = _fixture.CreateScope();
        await assign.Db.HKCardApprovals
            .Where(a => a.HKCardId == cardId && a.Stage == HKApprovalStage.Composition)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.AssignedApproverUserId, userId));
    }
}
