using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Раздельное согласование автономной карты по двум этапам.
/// <para>
/// Этапы проверяются через сервис. Ключевые свойства: этапы переключаются
/// независимо, блокировки относятся к части карты, а не к карте целиком, а
/// решение принимает назначенный согласующий.
/// </para>
/// </summary>
[Collection("Database")]
public class HKApprovalStageIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKApprovalStageIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private async Task<TestScope> ScopeAsync(string userId)
    {
        var scope = _fixture.CreateScope();
        scope.User.CurrentUserId = Guid.Parse(userId);
        scope.Permissions.InvalidateCache(userId);
        return scope;
    }

    private Task<TestScope> AuthorAsync() => ScopeAsync(_fixture.NormAdminA.Id);
    private Task<TestScope> ApproverAsync() => ScopeAsync(_fixture.NormAdminA2.Id);

    private static HKCard Draft(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsAutonomous = true,
        ObjectLevel = HKObjectLevel.Undefined,
        Status = HKCardStatus.Draft,
        Purpose = "Проверка этапов",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return (await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).RowVersion;
    }

    private async Task<Guid> CreateCardAsync(string name)
    {
        await using var s = await AuthorAsync();
        var created = await s.HK.CreateAsync(Draft(name));
        return created.Id;
    }

    private async Task SendToReviewAsync(Guid cardId, string approverId)
    {
        await using var s = await AuthorAsync();
        await s.Approvals.SubmitDetailsAsync(
            cardId, approverId, await VersionAsync(s, cardId));
    }

    private async Task<HKCardApproval> StageAsync(Guid cardId, HKApprovalStage stage)
    {
        await using var s = await AuthorAsync();
        return await s.Db.HKCardApprovals.AsNoTracking()
            .FirstAsync(a => a.HKCardId == cardId && a.Stage == stage);
    }

    // ── Первый этап ──

    [Fact]
    public async Task CreateAsync_CardGetsBothStagesInDraftWithoutFakeApprovalDates()
    {
        await using var s = await AuthorAsync();
        var created = await s.HK.CreateAsync(Draft("Этапы " + Suffix()));

        var stages = await s.Db.HKCardApprovals.AsNoTracking()
            .Where(a => a.HKCardId == created.Id)
            .ToListAsync();

        Assert.Equal(2, stages.Count);
        Assert.All(stages, a =>
        {
            Assert.Equal(HKApprovalStageState.Draft, a.State);
            Assert.Null(a.ApprovedAtUtc);
            Assert.Null(a.AssignedApproverUserId);
        });
    }

    [Fact]
    public async Task SubmitDetails_EmptyTreeAndUndefinedLevel_Succeeds()
    {
        var cardId = await CreateCardAsync("Пустое дерево " + Suffix());

        // Структура не заполнена и уровень не определён — отправка реквизитов
        // этого не требует.
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        var details = await StageAsync(cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.OnReview, details.State);
        Assert.Equal(_fixture.NormAdminA2.Id, details.AssignedApproverUserId);
    }

    [Fact]
    public async Task SubmitDetails_WithoutApprover_IsRefused()
    {
        var cardId = await CreateCardAsync("Без согласующего " + Suffix());

        await using var s = await AuthorAsync();
        var version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Approvals.SubmitDetailsAsync(cardId, "", version));
    }

    [Fact]
    public async Task SubmitDetails_ApproverWithoutPermission_IsRefused()
    {
        var cardId = await CreateCardAsync("Без прав " + Suffix());

        // Guest имеет недостаточно прав на согласование.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendToReviewAsync(cardId, _fixture.GuestA.Id));
    }

    [Fact]
    public async Task SubmitDetails_ApproverFromAnotherBranch_IsRefused()
    {
        var cardId = await CreateCardAsync("Чужой филиал " + Suffix());

        var otherBranchUser = await CreateUserInBranchAsync(_fixture.BranchB, nameof(UserRole.NormAdmin),
            PermissionCodes.HKApprove);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendToReviewAsync(cardId, otherBranchUser));
    }

    [Fact]
    public async Task ApproveDetails_OnlyAssignedApprover_Decides()
    {
        var cardId = await CreateCardAsync("Назначение " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        // Автор, отправивший реквизиты, принять их не может даже при наличии
        // полномочий: решение принимает назначенный согласующий.
        await using (var author = await AuthorAsync())
        {
            var version = await VersionAsync(author, cardId);
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => author.Approvals.ApproveDetailsAsync(cardId, null, version));
            Assert.Contains("назначенный", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using var approver = await ApproverAsync();
        await approver.Approvals.ApproveDetailsAsync(
            cardId, "принято", await VersionAsync(approver, cardId));

        var details = await StageAsync(cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.Approved, details.State);
        Assert.NotNull(details.ApprovedAtUtc);
    }

    [Fact]
    public async Task DetailsOnReview_DetailsAndPdfBlocked_CompositionStillEditable()
    {
        var cardId = await CreateCardAsync("Блокировки " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using var s = await AuthorAsync();

        var stages = await s.Approvals.GetStagesAsync(cardId);
        var caps = HKApprovalService.DeriveEditingCapabilities(
            stages, HKCardStatus.OnReview, hasWorkPermission: true);

        // Общий статус у карты один и тот же, а возможности разные.
        Assert.False(caps.CanEditDetails);
        Assert.False(caps.CanEditPdf);
        Assert.True(caps.CanEditComposition);

        // Реквизиты правкой отклонить.
        var spoofed = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        spoofed.Purpose = "Правка на рассмотрении";
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(spoofed));

        // Структуру на этом шаге заполнять можно.
        await s.HK.SaveTreeAsync(cardId, new[]
        {
            new HKCardNodeWrite(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие", 1, 0)
        });

        var nodes = await s.Db.HKCardNodes.AsNoTracking()
            .CountAsync(n => n.HKCardId == cardId);
        Assert.Equal(1, nodes);
    }

    [Fact]
    public async Task DetailsOnReview_AttachmentEdit_IsRefused()
    {
        var cardId = await CreateCardAsync("PDF " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using var s = await AuthorAsync();

        // PDF — часть основания и блокируется вместе с реквизитами.
        using var content = new MemoryStream([0x25, 0x50, 0x44, 0x46]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.SaveAttachmentAsync(
            cardId, content, "основание.pdf", "application/pdf", 4));

        Assert.Contains("Реквизиты", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnDetails_KeepsStructureAndRows()
    {
        var cardId = await CreateCardAsync("Возврат " + Suffix());
        var auId = await CreateAssemblyUnitAsync();
        await using (var s = await AuthorAsync())
        {
            await s.HK.SaveTreeAsync(cardId, new[]
            {
                new HKCardNodeWrite(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие", 1, 0)
            });
        }
        await AttachItemAsync(cardId, auId);
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using var approver = await ApproverAsync();
        await approver.Approvals.ReturnDetailsAsync(
            cardId, null, await VersionAsync(approver, cardId));

        var details = await StageAsync(cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.RevisionRequired, details.State);

        // Данные при возврате сохранены.
        await using var check = await AuthorAsync();
        Assert.Equal(1, await check.Db.HKCardNodes.AsNoTracking().CountAsync(n => n.HKCardId == cardId));
        Assert.Equal(1, await check.Db.HKCardItems.AsNoTracking().CountAsync(i => i.HKCardId == cardId));
    }

    [Fact]
    public async Task ReturnDetails_CommentIsOptional()
    {
        var cardId = await CreateCardAsync("Без комментария " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using var approver = await ApproverAsync();
        await approver.Approvals.ReturnDetailsAsync(
            cardId, null, await VersionAsync(approver, cardId));

        Assert.Equal(HKApprovalStageState.RevisionRequired,
            (await StageAsync(cardId, HKApprovalStage.Details)).State);
    }

    // ── Второй этап ──

    [Fact]
    public async Task SubmitComposition_BeforeDetailsApproved_IsRefused()
    {
        var cardId = await CreateCardAsync("Раньше времени " + Suffix());
        await FillStructureAsync(cardId);

        await using var s = await AuthorAsync();
        var version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Approvals.SubmitCompositionAsync(cardId, version));
    }

    [Fact]
    public async Task SubmitComposition_AfterDetailsApproved_AllowedAndKeepsSameApprover()
    {
        var cardId = await CreateCardAsync("Второй этап " + Suffix());
        await FillStructureAsync(cardId);
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ApproveDetailsAsync(cardId, null, await VersionAsync(approver, cardId));
        }

        await using var s = await AuthorAsync();
        await s.Approvals.SubmitCompositionAsync(cardId, await VersionAsync(s, cardId));

        var composition = await StageAsync(cardId, HKApprovalStage.Composition);
        Assert.Equal(HKApprovalStageState.OnReview, composition.State);
        Assert.Equal(_fixture.NormAdminA2.Id, composition.AssignedApproverUserId);
    }

    [Fact]
    public async Task SubmitComposition_OnlyRowsUnderRoot_IsAccepted()
    {
        var cardId = await CreateCardAsync("Только строки " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);
        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ApproveDetailsAsync(cardId, null, await VersionAsync(approver, cardId));
        }

        // Карта с конечными строками без дерева допустима для этого этапа.
        var auId = await CreateAssemblyUnitAsync();
        await AttachItemAsync(cardId, auId, null);

        await using var s = await AuthorAsync();

        // Строка добавлена напрямую, поэтому рабочий уровень приводится к
        // согласованному со структурой: уровень выводится из неё, и без этого
        // отправка состава правомерно отказала бы в несоответствии.
        await HKApprovalScenario.ApplyDerivedLevelAsync(s, cardId);

        await s.Approvals.SubmitCompositionAsync(cardId, await VersionAsync(s, cardId));

        Assert.Equal(HKApprovalStageState.OnReview,
            (await StageAsync(cardId, HKApprovalStage.Composition)).State);
    }

    [Fact]
    public async Task CompositionOnReview_TreeRowsAndMaterialsBlocked()
    {
        var cardId = await PrepareCompositionOnReviewAsync();

        await using var s = await AuthorAsync();

        // Дерево заблокировано.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveTreeAsync(cardId, new[]
            {
                new HKCardNodeWrite(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, 0)
            }));

        var stages = await s.Approvals.GetStagesAsync(cardId);
        var caps = HKApprovalService.DeriveEditingCapabilities(
            stages, HKCardStatus.OnReview, hasWorkPermission: true);

        // Реквизиты приняты, поэтому заморожены до возврата этапа; PDF следует
        // за ними.
        Assert.False(caps.CanEditDetails);
        Assert.False(caps.CanEditComposition);
        Assert.False(caps.CanEditPdf);
    }

    [Fact]
    public async Task UpdateAsync_CannotBypassCompositionLockWithChangedRows()
    {
        // Состав заблокирован, когда его этап на рассмотрении. В нормальном
        // ходе это неразрывно связано с принятыми реквизитами, поэтому состояние
        // собирается напрямую: блокировка состава — защита от несогласованных
        // данных, и проверять её нужно изолированно.
        var cardId = await CreateCardAsync("Блокировка состава " + Suffix());
        var auId = await CreateAssemblyUnitAsync();

        await using var s = await AuthorAsync();

        await AttachItemAsync(cardId, auId);

        await s.Db.HKCardApprovals
            .Where(a => a.HKCardId == cardId && a.Stage == HKApprovalStage.Composition)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, HKApprovalStageState.OnReview));

        var version = await VersionAsync(s, cardId);
        var card = await s.Db.HKCards.AsNoTracking()
            .Include(c => c.Items).ThenInclude(i => i.Materials)
            .FirstAsync(c => c.Id == cardId);

        // Разрешённая правка реквизитов...
        card.Purpose = "Разрешённая правка";
        card.RowVersion = version;

        // ...не должна протаскивать изменение состава.
        var quantityBefore = card.Items.First().Quantity;
        card.Items.First().Quantity = 999;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(card));
        Assert.Contains("Состав", ex.Message, StringComparison.OrdinalIgnoreCase);

        // И реквизиты не применены: отказ целиком, а не частичное сохранение.
        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.NotEqual("Разрешённая правка", stored.Purpose);
        Assert.Equal(quantityBefore, await s.Db.HKCardItems.AsNoTracking()
            .Where(i => i.HKCardId == cardId).Select(i => i.Quantity).FirstAsync());
    }

    [Fact]
    public async Task ReturnComposition_KeepsDataAndAllowsResubmit()
    {
        var cardId = await PrepareCompositionOnReviewAsync();

        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ReturnCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }

        Assert.Equal(HKApprovalStageState.RevisionRequired,
            (await StageAsync(cardId, HKApprovalStage.Composition)).State);

        // Данные не удалены.
        await using var check = await AuthorAsync();
        Assert.Equal(1, await check.Db.HKCardItems.AsNoTracking().CountAsync(i => i.HKCardId == cardId));

        // Повторная отправка допустима.
        await using var s = await AuthorAsync();
        await s.Approvals.SubmitCompositionAsync(cardId, await VersionAsync(s, cardId));
        Assert.Equal(HKApprovalStageState.OnReview,
            (await StageAsync(cardId, HKApprovalStage.Composition)).State);
    }

    /// <summary>
    /// Хранилище принятой структуры появилось, поэтому окончательное принятие
    /// больше не закрыто. Проверяется, что команду выполняет назначенный
    /// согласующий, и что она связывает редакцию, указатель и статус.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_ByAssignedApprover_PublishesRevision()
    {
        var cardId = await PrepareCompositionOnReviewAsync();

        await using var approver = await ApproverAsync();
        var version = await VersionAsync(approver, cardId);
        await approver.Approvals.ApproveCompositionAsync(cardId, null, version);

        await using var check = await AuthorAsync();
        var card = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, card.Status);
        Assert.NotNull(card.CurrentAcceptedRevisionId);
        Assert.Equal(1, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
    }

    /// <summary>Посторонний пользователь принять состав не может.</summary>
    [Fact]
    public async Task ApproveComposition_ByStranger_IsRefused()
    {
        var cardId = await PrepareCompositionOnReviewAsync();

        await using var stranger = await AuthorAsync();
        var version = await VersionAsync(stranger, cardId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stranger.Approvals.ApproveCompositionAsync(cardId, null, version));

        await using var check = await AuthorAsync();
        var card = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.NotEqual(HKCardStatus.Approved, card.Status);
        Assert.Null(card.CurrentAcceptedRevisionId);
    }

    // ── Общий статус ──

    [Fact]
    public async Task OverallStatus_FollowsStages_AndNeverReturnsArchivedOrDeleted()
    {
        var cardId = await CreateCardAsync("Статусы " + Suffix());

        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);
        Assert.Equal(HKCardStatus.OnReview, await StatusOfAsync(cardId));

        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ReturnDetailsAsync(cardId, null, await VersionAsync(approver, cardId));
        }
        Assert.Equal(HKCardStatus.RevisionRequired, await StatusOfAsync(cardId));

        // Принятые реквизиты при возвращённом составе: этапы независимы.
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);
        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ApproveDetailsAsync(cardId, null, await VersionAsync(approver, cardId));
        }
        Assert.Equal(HKCardStatus.Draft, await StatusOfAsync(cardId));

        // Архивное и удалённое состояние вычислением не возвращаются.
        var active = new List<HKCardApproval>
        {
            new() { Stage = HKApprovalStage.Details, State = HKApprovalStageState.Draft },
            new() { Stage = HKApprovalStage.Composition, State = HKApprovalStageState.Draft }
        };
        Assert.Equal(HKCardStatus.Draft, HKApprovalService.DeriveOverallStatus(active));
    }

    [Fact]
    public async Task ChangeStatusAsync_CannotBypassTwoApprovals()
    {
        var cardId = await CreateCardAsync("Обход " + Suffix());

        await using var s = await AuthorAsync();
        foreach (var status in new[]
                 {
                     HKCardStatus.OnReview, HKCardStatus.RevisionRequired, HKCardStatus.Approved
                 })
        {
            var (ok, error) = await s.HK.ChangeStatusAsync(cardId, status, null, default);
            Assert.False(ok);
            // Отказ контролируемый и предметный. Формулировка зависит от того,
        // какое условие сработало раньше: карта может быть ещё на рассмотрении
        // этапа, а утверждение закрыто в любом случае.
        Assert.True(
            error!.Contains("командами этапов согласования", StringComparison.OrdinalIgnoreCase)
            || error!.Contains("назначенным согласующим", StringComparison.OrdinalIgnoreCase),
            "Неожиданный отказ общего переключателя статуса: " + error);

        }
    }

    // ── Права ──

    [Fact]
    public async Task Submit_WithoutHkSubmit_IsRefused()
    {
        var cardId = await CreateCardAsync("Без HK.Submit " + Suffix());

        await using var s = await ScopeAsync(_fixture.GuestA.Id);
        var version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, version));
    }

    [Fact]
    public async Task Return_WithoutHkReview_IsRefused()
    {
        var cardId = await CreateCardAsync("Без HK.Review " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using var s = await ScopeAsync(_fixture.OperatorA.Id);
        s.Permissions.InvalidateCache(_fixture.OperatorA.Id);
        var version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Approvals.ReturnDetailsAsync(cardId, null, version));
    }

    [Fact]
    public async Task HkWork_AloneDoesNotGrantDecisions()
    {
        var cardId = await CreateCardAsync("Только HK.Work " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        // Пользователь с правом редактирования, но без HK.Approve/HK.Review.
        var worker = await CreateUserInBranchAsync(
            _fixture.BranchA, nameof(UserRole.Operator), PermissionCodes.HKWork);

        await using var s = await ScopeAsync(worker);

        var version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Approvals.ApproveDetailsAsync(cardId, null, version));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Approvals.ReturnDetailsAsync(cardId, null, version));
    }

    // ── Конкуренция и задачи ──

    [Fact]
    public async Task TwoSimultaneousDecisions_OneWins_OtherIsRefused()
    {
        var cardId = await CreateCardAsync("Гонка решений " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        var staleVersion = await VersionOfAsync(cardId);

        await using var first = await ApproverAsync();
        await first.Approvals.ApproveDetailsAsync(cardId, null, await VersionAsync(first, cardId));

        // Второе решение с той же прочитанной версией отклоняется: первое уже
        // изменило карту, и без версии оно сочло бы себя первым.
        await using var second = await ApproverAsync();
        var stale = staleVersion;
        await Assert.ThrowsAsync<HkCardVersionConflictException>(
            () => second.Approvals.ReturnDetailsAsync(cardId, null, stale));
    }

    [Fact]
    public async Task Resubmit_DoesNotDuplicateOpenReviewTasks()
    {
        var cardId = await CreateCardAsync("Без дублей " + Suffix());

        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);
        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ReturnDetailsAsync(cardId, null, await VersionAsync(approver, cardId));
        }
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using var s = await AuthorAsync();
        var openReviews = await s.Db.WorkTasks.AsNoTracking()
            .CountAsync(t => !t.IsDeleted && t.EntityId == cardId
                             && t.Type == WorkTaskType.HKDetailsReview
                             && (t.Status == WorkTaskStatus.Open
                                 || t.Status == WorkTaskStatus.InProgress
                                 || t.Status == WorkTaskStatus.Overdue));

        Assert.Equal(1, openReviews);
    }

    [Fact]
    public async Task Return_CreatesAddressedTaskForAuthor_AndNoIkTasks()
    {
        var cardId = await CreateCardAsync("Задача автору " + Suffix());
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ReturnDetailsAsync(cardId, null, await VersionAsync(approver, cardId));
        }

        await using var s = await AuthorAsync();

        var revision = await s.Db.WorkTasks.AsNoTracking()
            .Where(t => !t.IsDeleted && t.EntityId == cardId && t.Type == WorkTaskType.HKRevision)
            .FirstAsync();

        Assert.Equal(_fixture.NormAdminA.Id, revision.AssignedToUserId);
        Assert.Null(revision.AssignedRole);

        // Задачи пересчёта ИК не создаются.
        Assert.False(await s.Db.WorkTasks.AsNoTracking().AnyAsync(t => !t.IsDeleted
            && t.EntityId == cardId
            && (t.Type == WorkTaskType.HKExpirationReview
                || t.Type == WorkTaskType.CompositionReadiness)));
    }

    // ── Вспомогательное ──

    private async Task<HKCardStatus> StatusOfAsync(Guid cardId)
    {
        await using var s = await AuthorAsync();
        return (await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).Status;
    }

    private async Task<uint> VersionOfAsync(Guid cardId)
    {
        await using var s = await AuthorAsync();
        return await VersionAsync(s, cardId);
    }

    private async Task FillStructureAsync(Guid cardId)
    {
        var auId = await CreateAssemblyUnitAsync();
        await using var s = await AuthorAsync();
        await s.HK.SaveTreeAsync(cardId, new[]
        {
            new HKCardNodeWrite(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие", 1, 0)
        });
        await AttachItemAsync(cardId, auId);
    }

    private async Task<Guid> PrepareCompositionOnReviewAsync()
    {
        var cardId = await CreateCardAsync("Состав на рассмотрении " + Suffix());
        await FillStructureAsync(cardId);
        await SendToReviewAsync(cardId, _fixture.NormAdminA2.Id);

        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ApproveDetailsAsync(cardId, null, await VersionAsync(approver, cardId));
        }

        await using var s = await AuthorAsync();
        await s.Approvals.SubmitCompositionAsync(cardId, await VersionAsync(s, cardId));
        return cardId;
    }

    private async Task<Guid> CreateAssemblyUnitAsync()
    {
        await using var s = await AuthorAsync();
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix() };
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();
        return au.Id;
    }

    private async Task AttachItemAsync(Guid cardId, Guid assemblyUnitId, Guid? nodeId = null)
    {
        await using var s = await AuthorAsync();

        Guid? targetNode = nodeId;
        if (targetNode is null)
        {
            targetNode = await s.Db.HKCardNodes.AsNoTracking()
                .Where(n => n.HKCardId == cardId)
                .Select(n => (Guid?)n.Id)
                .FirstOrDefaultAsync();
        }

        s.Db.HKCardItems.Add(new HKCardItem
        {
            Id = Guid.NewGuid(),
            HKCardId = cardId,
            HKCardNodeId = targetNode,
            AssemblyUnitId = assemblyUnitId,
            Quantity = 1,
            Volume = 1m,
            SortOrder = 0
        });
        await s.Db.SaveChangesAsync();
    }

    private async Task<string> CreateUserInBranchAsync(Guid branchId, string role, params string[] permissions)
    {
        var login = "appr_" + Suffix().ToLowerInvariant();
        await using var setup = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = true
        };

        var result = await setup.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));

        await setup.Users.AddToRoleAsync(user, role);
        login = user.Id;

        foreach (var code in permissions)
        {
            setup.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = login,
                PermissionCode = code,
                IsGranted = true,
                GrantedByUserId = login,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await setup.Db.SaveChangesAsync();
        return login;
    }
}