using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Регрессионные проверки целостности согласования: эталон реквизитов,
/// инициализация этапов и вычисление редактируемости частей.
/// <para>
/// Каждая проверка закрывает подтверждённый дефект, а не общее поведение.
/// </para>
/// </summary>
[Collection("Database")]
public class HKApprovalPersistenceIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKApprovalPersistenceIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<TestScope> AuthorAsync() => await AuthorAsync(_fixture.NormAdminA.Id);

    private async Task<TestScope> AuthorAsync(string authorId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(authorId);
        return s;
    }

    private async Task<TestScope> ApproverAsync()
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        return s;
    }

    /// <summary>
    /// Приводит карту к состоянию «этапов нет».
    /// <para>
    /// При создании карты пара этапов заводится сразу, поэтому состояние без
    /// этапов нужно задать явно. Оно само по себе допустимо: это карта, которая
    /// ещё не проходила ни одной команды.
    /// </para>
    /// </summary>
    private async Task RemoveStagesAsync(Guid cardId)
    {
        await using var setup = _fixture.CreateScope();
        await setup.Db.HKCardApprovals.Where(a => a.HKCardId == cardId).ExecuteDeleteAsync();
    }

    // ── Эталон реквизитов (StoredDetails) ──

    /// <summary>
    /// Во время рассмотрения реквизитов меняются ТОЛЬКО конечные строки —
    /// команда проходит, реквизиты не трогаются.
    /// </summary>
    [Fact]
    public async Task Update_WhileDetailsOnReview_ChangesOnlyRows_IsAllowed()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(
            await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        await using var s = await AuthorAsync();
        var card = await s.Db.HKCards
            .Include(c => c.Items).ThenInclude(i => i.Materials)
            .FirstAsync(c => c.Id == cardId);

        var storedName = card.Name;
        card.Items.First().Quantity = 77;

        await s.HK.UpdateAsync(card);

        s.Db.ChangeTracker.Clear();
        var after = await s.Db.HKCards.AsNoTracking()
            .Include(c => c.Items)
            .FirstAsync(c => c.Id == cardId);

        Assert.Equal(77, after.Items.Single().Quantity);
        Assert.Equal(storedName, after.Name);
    }

    /// <summary>
    /// Подмена реквизитов в отслеживаемом экземпляре НЕ должна становиться
    /// невидимой для команды.
    /// <para>
    /// Именно этот случай раньше проходил: эталон брался из отслеживаемого
    /// объекта, который при эмошн-вызове является тем же экземпляром, что и
    /// входной. Раз сравнение шло «с самим собой», изменение реквизитов
    /// считалось отсутствующим, и смешанная команда сохраняла и реквизиты, и
    /// строки — в обход блокировки этапа.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Update_TrackedDetailsMutatedWithRows_IsRejectedWholesale()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(
            await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        string storedName;
        await using (var setup = await AuthorAsync())
        {
            storedName = (await setup.Db.HKCards.AsNoTracking()
                .FirstAsync(c => c.Id == cardId)).Name!;
        }

        await using var s = await AuthorAsync();

        // Отдельный входной экземпляр, отслеживаемый этим же контекстом:
        // именно так работает штатный вызов из формы.
        var card = await s.Db.HKCards
            .Include(c => c.Items).ThenInclude(i => i.Materials)
            .FirstAsync(c => c.Id == cardId);

        card.Name = "Подменённое название";
        card.Purpose = "Подменённое назначение";
        card.Items.First().Quantity = 55;

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(card));

        s.Db.ChangeTracker.Clear();
        var after = await s.Db.HKCards.AsNoTracking()
            .Include(c => c.Items)
            .FirstAsync(c => c.Id == cardId);

        // Ни реквизиты, ни строки не записаны: отказ целиком, без частичного
        // сохранения.
        Assert.Equal(storedName, after.Name);
        Assert.NotEqual("Подменённое назначение", after.Purpose);
        Assert.NotEqual(55, after.Items.Single().Quantity);
    }

    /// <summary>
    /// Тот же отказ на ОТДЕЛЬНОМ экземпляре: запрет не зависит от того,
    /// отслеживается ли входной объект.
    /// </summary>
    [Fact]
    public async Task Update_DetachedDetailsMutatedWithRows_IsRejectedWholesale()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(
            await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        var version = await HKApprovalScenario.VersionAsync(await AuthorAsync(), cardId);

        await using var s = await AuthorAsync();
        var detached = new HKCard
        {
            Id = cardId,
            RowVersion = version,
            Name = "Чужое название",
            Purpose = "Чужое назначение",
            Items = new List<HKCardItem>()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(detached));

        await using var check = await AuthorAsync();
        var after = await check.Db.HKCards.AsNoTracking()
            .Include(c => c.Items)
            .FirstAsync(c => c.Id == cardId);

        Assert.NotEqual("Чужое название", after.Name);
        Assert.NotEqual("Чужое назначение", after.Purpose);
    }

    // ── Инициализация этапов ──

    /// <summary>
    /// Этапы действительно сохраняются: прежний дефект оставлял их в памяти.
    /// </summary>
    [Fact]
    public async Task FirstCommand_PersistsBothStages()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await RemoveStagesAsync(cardId);

        await using (var setup = await AuthorAsync())
        {
            Assert.Empty(await setup.Db.HKCardApprovals
                .Where(a => a.HKCardId == cardId).ToListAsync());
        }

        await HKApprovalScenario.SubmitDetailsAsync(
            await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        await using var check = await AuthorAsync();
        var stages = await check.Db.HKCardApprovals.AsNoTracking()
            .Where(a => a.HKCardId == cardId).ToListAsync();

        Assert.Equal(2, stages.Count);
        Assert.Contains(stages, a => a.Stage == HKApprovalStage.Composition);
        Assert.Contains(stages, a => a.Stage == HKApprovalStage.Details);

        // Дата принятия не выдумывается.
        Assert.All(stages, a => Assert.Null(a.ApprovedAtUtc));
    }

    /// <summary>Повторная команда не создаёт дополнительных записей.</summary>
    [Fact]
    public async Task RepeatedCommands_DoNotDuplicateStages()
    {
        var cardId = await HKApprovalScenario.CreateCardWithApprovedDetailsAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id);

        await HKApprovalScenario.SubmitCompositionAsync(await AuthorAsync(), cardId);

        await using var check = await AuthorAsync();
        var stages = await check.Db.HKCardApprovals.AsNoTracking()
            .Where(a => a.HKCardId == cardId).ToListAsync();

        Assert.Equal(2, stages.Count);
    }

    /// <summary>
    /// Две параллельные команды на карте без этапов не создают дубликатов.
    /// <para>
    /// Это реальная гонка: два отдельных scope и DbContext, синхронный старт.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ParallelCommands_OnCardWithoutStages_DoNotDuplicate()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await RemoveStagesAsync(cardId);

        var version = await HKApprovalScenario.VersionAsync(await AuthorAsync(), cardId);

        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForBothAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2)
                gate.TrySetResult();
            await gate.Task;
        }

        var outcomes = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var s = await AuthorAsync();
                await WaitForBothAsync();
                try
                {
                    await s.Approvals.SubmitDetailsAsync(
                        cardId, _fixture.NormAdminA2.Id, version);
                    return "submitted";
                }
                catch (Exception ex)
                {
                    return ex.GetType().Name;
                }
            }),
            Task.Run(async () =>
            {
                await using var s = await AuthorAsync();
                await WaitForBothAsync();
                try
                {
                    // Отправка реквизитов: две одинаковые команды действительно
                    // могут состязаться. Отправку состава сюда ставить нельзя —
                    // она законно требует принятых реквизитов, и отказ был бы
                    // следствием порядка этапов, а не конкуренции.
                    await s.Approvals.SubmitDetailsAsync(
                        cardId, _fixture.NormAdminA2.Id, version);
                    return "submitted";
                }
                catch (Exception ex)
                {
                    return ex.GetType().Name;
                }
            }));

        // Ровно одна команда состоялась: карту версию одну изменить нельзя
        // обоим, вторая обязана получить контролируемый отказ.
        Assert.Equal(1, outcomes.Count(o => o == "submitted"));
        var refused = outcomes.Single(o => o != "submitted");
        Assert.Equal(nameof(HkCardVersionConflictException), refused);

        await using var check = await AuthorAsync();
        var stages = await check.Db.HKCardApprovals.AsNoTracking()
            .Where(a => a.HKCardId == cardId).ToListAsync();

        // Пара «карта + этап» уникальна: дублей быть не может ни при какой гонке.
        Assert.Equal(2, stages.Count);
        Assert.Equal(2, stages.Select(a => a.Stage).Distinct().Count());
    }

    /// <summary>
    /// Недопустимая команда не оставляет ни этапов, ни задач, ни аудита.
    /// <para>
    /// Инициализация выполняется ПОСЛЕ проверок доступа и внутри транзакции
    /// команды. Если бы она шла до проверок или вне транзакции, отказ по
    /// правам оставил бы в базе записи о согласовывании, которого не было.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RefusedCommand_LeavesNoStagesTasksOrAudit()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await RemoveStagesAsync(cardId);

        int auditBefore;
        int tasksBefore;
        await using (var setup = await AuthorAsync())
        {
            auditBefore = await setup.Db.AuditLogs.CountAsync(a => a.EntityId == cardId.ToString());
            tasksBefore = await setup.Db.WorkTasks
                .CountAsync(t => t.EntityId == cardId && !t.IsDeleted);
        }

        // Отказ по правам: пользователь чужого филиала.
        await using var outsider = _fixture.CreateScope();
        outsider.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var version = await HKApprovalScenario.VersionAsync(outsider, cardId);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            outsider.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, version));

        await using var check = await AuthorAsync();
        Assert.Empty(await check.Db.HKCardApprovals
            .Where(a => a.HKCardId == cardId).ToListAsync());
        Assert.Equal(auditBefore, await check.Db.AuditLogs.CountAsync(a => a.EntityId == cardId.ToString()));
        Assert.Equal(tasksBefore, await check.Db.WorkTasks
            .CountAsync(t => t.EntityId == cardId && !t.IsDeleted));
    }

    // ── Возможности редактирования частей ──

    [Fact]
    public async Task Capabilities_DraftWithoutStages_AllEditable()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);

        await using var s = await AuthorAsync();
        var caps = await s.Approvals.GetEditingCapabilitiesAsync(cardId);

        Assert.True(caps.CanEditDetails);
        Assert.True(caps.CanEditComposition);
        Assert.True(caps.CanEditPdf);
    }

    /// <summary>
    /// Отсутствие этапов у архивной карты не превращается в разрешение.
    /// <para>
    /// Раньше ветка «этапов нет» возвращала полную редактируемость ДО проверки
    /// статуса, и архивная карта показывалась редактируемой.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Capabilities_ArchivedWithoutStages_NothingEditable()
    {
        const HKCardStatus status = HKCardStatus.Archived;
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);

        await using (var setup = _fixture.CreateScope())
        {
            var card = await setup.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.Status = status;
            await setup.Db.SaveChangesAsync();
        }

        await using var s = await AuthorAsync();

        // Предусловие: сервис обязан видеть именно этот статус. Если он не
        // сохранился, проверка ниже ничего бы не доказывала.
        var readBack = await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => c.Status)
            .FirstAsync();
        Assert.Equal(status, readBack);

        var caps = await s.Approvals.GetEditingCapabilitiesAsync(cardId);

        Assert.False(caps.CanEditDetails);
        Assert.False(caps.CanEditComposition);
        Assert.False(caps.CanEditPdf);
    }

    /// <summary>
    /// Удалённая карта исключается фильтром запросов и не читается вовсе.
    /// Отказ — правильный исход; опасным был бы возврат редактируемости.
    /// </summary>
    [Fact]
    public async Task Capabilities_DeletedCard_IsRefusedNotEditable()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await RemoveStagesAsync(cardId);

        await using (var setup = _fixture.CreateScope())
        {
            var card = await setup.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.Status = HKCardStatus.Deleted;
            await setup.Db.SaveChangesAsync();
        }

        await using var s = await AuthorAsync();
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await s.Approvals.GetEditingCapabilitiesAsync(cardId));
    }

    [Fact]
    public async Task Capabilities_WithoutHkWorkPermission_NothingEditable()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);

        // Читатель без права работы.
        var readerId = await CreateReaderAsync(_fixture.BranchA);

        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(readerId);

        var caps = await s.Approvals.GetEditingCapabilitiesAsync(cardId);

        Assert.False(caps.CanEditDetails);
        Assert.False(caps.CanEditComposition);
        Assert.False(caps.CanEditPdf);
    }

    /// <summary>
    /// Возможности следуют состоянию КАЖДОГО этапа: на рассмотрении реквизитов
    /// реквизиты заперты, состав доступен.
    /// </summary>
    [Fact]
    public async Task Capabilities_DetailsOnReview_LocksDetailsOnly()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AuthorAsync(), _fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitDetailsAsync(
            await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        await using var s = await AuthorAsync();
        var caps = await s.Approvals.GetEditingCapabilitiesAsync(cardId);

        Assert.False(caps.CanEditDetails);
        Assert.False(caps.CanEditPdf);
        Assert.True(caps.CanEditComposition);
    }

    /// <summary>
    /// Возвращённые «не найдена» не должны превращаться в редактируемость.
    /// Отказ доступа — это отказ, а не «карта новая, всё разрешено».
    /// </summary>
    [Fact]
    public async Task Capabilities_UnknownCard_IsRefusedNotEditable()
    {
        await using var s = await AuthorAsync();
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await s.Approvals.GetEditingCapabilitiesAsync(Guid.NewGuid()));
    }

    private async Task<string> CreateReaderAsync(Guid branchId)
    {
        await using var setup = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "reader_" + Suffix(),
            FullName = "Читатель " + Suffix(),
            BranchId = branchId,
            IsActive = true
        };

        var result = await setup.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));

        await setup.Users.AddToRoleAsync(user, nameof(UserRole.Operator));

        // У роли Operator право HK.Work есть по шаблону, поэтому без явного
        // индивидуального запрета «пользователь без права работы» не получится.
        setup.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionCode = PermissionCodes.HKWork,
            IsGranted = false,
            GrantedByUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await setup.Db.SaveChangesAsync();
        setup.Permissions.InvalidateCache(user.Id);
        return user.Id;
    }
}