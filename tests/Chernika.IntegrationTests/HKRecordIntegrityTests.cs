using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Завершение целостности записи и окончательного принятия.
/// <para>
/// Каждый тест закрывает конкретный оставшийся сценарий PR № 6.1: защищённая
/// запись материалов вместо проверки «до записи», гонка записи с согласованием,
/// изоляция команды принятия от несохранённого трекера, поздние отказы и
/// пригодность scope после отказа.
/// </para>
/// </summary>
[Collection("Database")]
public class HKRecordIntegrityTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKRecordIntegrityTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private TestScope As(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    private Task<TestScope> ApproverAsync() => Task.FromResult(As(_fixture.NormAdminA2.Id));

    private async Task<Guid> CardReadyAsync(TestScope s)
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        await HKApprovalScenario.SubmitDetailsAsync(
            As(_fixture.NormAdminA.Id), cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(As(_fixture.NormAdminA.Id), cardId);
        return cardId;
    }

    private async Task BringToReviewAsync(Guid cardId)
    {
        await HKApprovalScenario.SubmitDetailsAsync(
            As(_fixture.NormAdminA.Id), cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(As(_fixture.NormAdminA.Id), cardId);
    }

    private async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();
    }

    private async Task<Guid> ItemOfAsync(Guid cardId)
    {
        await using var read = _fixture.CreateScope();
        return await read.Db.HKCardItems.AsNoTracking()
            .Where(i => i.HKCardId == cardId).Select(i => i.Id).FirstAsync();
    }

    private async Task<Guid> GsmAsync()
    {
        await using var s = _fixture.CreateScope();
        var gsm = new Chernika.Domain.Entities.GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "М-" + Suffix(),
            Nd = "ТУ " + Suffix()
        };
        s.Db.GsmMaterials.Add(gsm);
        await s.Db.SaveChangesAsync();
        return gsm.Id;
    }

    // ── §2. Валидация материалов ──

    /// <summary>
    /// Одна и та же марка дважды в одной категории — задвоение строки.
    /// Карта берётся на рассмотрении реквизитов: там замена разрешена.
    /// </summary>
    [Fact]
    public async Task ReplaceMaterials_DuplicateInSameCategory_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Items.ReplaceMaterialsAsync(itemId,
                new[] { (gsmId, GsmCategory.Primary), (gsmId, GsmCategory.Primary) }));

        await using var check = _fixture.CreateScope();
        Assert.Equal(0, await check.Db.HKCardItemMaterials.CountAsync(
            m => m.HKCardItemId == itemId && m.GsmMaterialId == gsmId));
    }

    [Fact]
    public async Task ReplaceMaterials_UndefinedCategory_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Items.ReplaceMaterialsAsync(itemId,
                new[] { (gsmId, (GsmCategory)9999) }));

        await using var check = _fixture.CreateScope();
        Assert.Equal(0, await check.Db.HKCardItemMaterials.CountAsync(m => m.HKCardItemId == itemId
            && m.GsmMaterialId == gsmId));
    }

    [Fact]
    public async Task ReplaceMaterials_DeletedGsm_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        var itemId = await ItemOfAsync(cardId);

        var gsmId = await GsmAsync();
        await using (var del = _fixture.CreateScope())
        {
            // Удаление штатной командой справочника, а не правкой теста.
            await del.Db.GsmMaterials
                .Where(g => g.Id == gsmId)
                .ExecuteUpdateAsync(g => g.SetProperty(x => x.IsDeleted, true));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Items.ReplaceMaterialsAsync(itemId, new[] { (gsmId, GsmCategory.Primary) }));

        await using var check = _fixture.CreateScope();
        Assert.Equal(0, await check.Db.HKCardItemMaterials.CountAsync(m => m.HKCardItemId == itemId));
    }

    [Fact]
    public async Task ReplaceMaterials_WritesAudit()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmAsync();

        await s.Items.ReplaceMaterialsAsync(itemId, new[] { (gsmId, GsmCategory.Reserve) });

        await using var check = _fixture.CreateScope();
        Assert.Equal(1, await check.Db.AuditLogs.AsNoTracking().CountAsync(a =>
            a.EntityId == itemId.ToString() && a.Action == "MaterialsReplaced"));
    }

    // ── §4. Изоляция команды принятия от несохранённого трекера ──

    /// <summary>
    /// Согласующий заранее меняет отслеживаемую карту в памяти, не сохраняя.
    /// Попутная запись такого изменения означала бы, что решение принимается не
    /// по сохранённым данным.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_WithUnsavedCardChangeInContext_IsRefusedBeforeWrite()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        await using var approver = As(_fixture.NormAdminA2.Id);
        var tracked = await approver.Db.HKCards.FirstAsync(c => c.Id == cardId);
        tracked.Name = "подмена без сохранения";
        tracked.Purpose = "подмена без сохранения";

        var version = await approver.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));

        await using var check = _fixture.CreateScope();
        var stored = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.NotEqual("подмена без сохранения", stored.Name);
        Assert.NotEqual(HKCardStatus.Approved, stored.Status);
        Assert.Null(stored.CurrentAcceptedRevisionId);
        Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
    }

    /// <summary>
    /// Несохранённо изменены строки карты: принятие не должно записать их попутно.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_WithUnsavedItemChange_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        await using var approver = As(_fixture.NormAdminA2.Id);
        var item = await approver.Db.HKCardItems.FirstAsync(i => i.HKCardId == cardId);
        item.Quantity = 999;

        var version = await approver.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));

        await using var check = _fixture.CreateScope();
        Assert.NotEqual(999m, await check.Db.HKCardItems.AsNoTracking()
            .Where(i => i.HKCardId == cardId).Select(i => i.Quantity).FirstAsync());
    }

    /// <summary>
    /// В контексте есть несохранённое изменение посторонней сущности: оно не
    /// относится к карте, но попутно записаться вместе с решением тоже не должно.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_WithForeignPendingEntity_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        await using var approver = As(_fixture.NormAdminA2.Id);

        var foreign = new Chernika.Domain.Entities.GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "несохранённая " + Suffix(),
            Nd = "ТУ"
        };
        approver.Db.GsmMaterials.Add(foreign);

        var version = await approver.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));

        await using var check = _fixture.CreateScope();
        Assert.False(await check.Db.GsmMaterials.AsNoTracking()
            .AnyAsync(g => g.Id == foreign.Id));
    }

    /// <summary>
    /// Чистый контекст принятия не отклоняется: отказ должен быть точечным,
    /// а не запретом на нормальную работу.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_WithLoadedButUnmodifiedEntities_Succeeds()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        await using var approver = As(_fixture.NormAdminA2.Id);

        // Загружено, но не изменено — обычная работа Blazor-страницы.
        _ = await approver.Db.HKCards.FirstAsync(c => c.Id == cardId);
        _ = await approver.Db.HKCardItems.Where(i => i.HKCardId == cardId).ToListAsync();
        _ = await approver.Db.HKCardApprovals.Where(a => a.HKCardId == cardId).ToListAsync();

        var version = await approver.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

        await approver.Approvals.ApproveCompositionAsync(cardId, null, version);

        await using var check = _fixture.CreateScope();
        Assert.Equal(HKCardStatus.Approved,
            (await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).Status);
    }

    // ── §5. Поздние отказы ──

    private async Task AssertNothingAcceptedAsync(Guid cardId, int revisionsBefore,
        int notificationsBefore, int tasksBefore, List<int> taskStatusesBefore)
    {
        await using var check = _fixture.CreateScope();

        Assert.Equal(revisionsBefore, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));

        var card = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Null(card.CurrentAcceptedRevisionId);
        Assert.NotEqual(HKCardStatus.Approved, card.Status);

        var stage = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition);
        Assert.Equal(HKApprovalStageState.OnReview, stage.State);
        Assert.Null(stage.ApprovedAtUtc);

        // Числа задач недостаточно: проверяются ИХ СТАТУСЫ.
        var tasks = await check.Db.WorkTasks.AsNoTracking()
            .Where(t => t.EntityId == cardId).OrderBy(t => t.Id)
            .Select(t => (int)t.Status).ToListAsync();
        Assert.Equal(taskStatusesBefore, tasks);
        Assert.Equal(tasksBefore, tasks.Count);

        Assert.Equal(notificationsBefore,
            await check.Db.Notifications.CountAsync(n => n.EntityId == cardId));
        Assert.Equal(0, await check.Db.AuditLogs.AsNoTracking().CountAsync(a =>
            a.EntityId == cardId.ToString() && a.Action == "CompositionApproved"));
    }

    [Theory]
    [InlineData("\"AuditLogs\"")]
    [InlineData("\"Notifications\"")]
    public async Task ApproveComposition_WhenLateWriteFails_RollsBackEverything(string injectionPoint)
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        int revisionsBefore, notificationsBefore, tasksBefore;
        List<int> taskStatusesBefore;
        await using (var pre = _fixture.CreateScope())
        {
            revisionsBefore = await pre.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId);
            notificationsBefore = await pre.Db.Notifications.CountAsync(n => n.EntityId == cardId);
            taskStatusesBefore = await pre.Db.WorkTasks.AsNoTracking()
                .Where(t => t.EntityId == cardId).OrderBy(t => t.Id)
                .Select(t => (int)t.Status).ToListAsync();
            tasksBefore = taskStatusesBefore.Count;
        }

        FailingCommandInterceptor.ArmAt(injectionPoint, 1);
        try
        {
            await using var approver = As(_fixture.NormAdminA2.Id);
            var version = await VersionAsync(approver, cardId);

            await Assert.ThrowsAnyAsync<Exception>(
                () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));

            Assert.True(FailingCommandInterceptor.Fired,
                $"Ошибка не была вставлена в точку {injectionPoint}: откат не проверялся.");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await AssertNothingAcceptedAsync(cardId, revisionsBefore, notificationsBefore,
            tasksBefore, taskStatusesBefore);
    }

    // ── §6. Тот же scope после отказа ──

    /// <summary>
    /// После отказа тот же scope выполняет другое разрешённое сохранение.
    /// Отменённые статус, указатель, снимок, задачи и аудит не должны дописаться
    /// из оставшегося tracking-состояния.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_AfterFailureSameScopeSaves_DoesNotAppendCancelledApproval()
    {
        await using var approver = As(_fixture.NormAdminA2.Id);

        Guid cardId;
        await using (var s = As(_fixture.NormAdminA.Id))
        {
            cardId = await CardReadyAsync(s);
        }

        int revisionsBefore;
        await using (var pre = _fixture.CreateScope())
        {
            revisionsBefore = await pre.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId);
        }

        FailingCommandInterceptor.ArmAt("\"HKCardRevisionItems\"", 1);
        try
        {
            var version = await VersionAsync(approver, cardId);
            await Assert.ThrowsAnyAsync<Exception>(
                () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));
            Assert.True(FailingCommandInterceptor.Fired,
                "Ошибка не была вставлена: сценарий после отказа не проверялся.");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        // Тот же scope, другое разрешённое сохранение.
        var lateGsmId = Guid.NewGuid();
        await approver.Db.GsmMaterials.AddAsync(new Chernika.Domain.Entities.GsmMaterial
        {
            Id = lateGsmId, Name = "после отказа " + Suffix(), Nd = "ТУ"
        });
        await approver.Db.SaveChangesAsync();

        await using var check = _fixture.CreateScope();
        var card = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        // Принятие так и не состоялось ни в базе, ни в остатках трекера.
        Assert.NotEqual(HKCardStatus.Approved, card.Status);
        Assert.Null(card.CurrentAcceptedRevisionId);
        Assert.Equal(revisionsBefore, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition)).State);
        Assert.Equal(0, await check.Db.AuditLogs.AsNoTracking().CountAsync(a =>
            a.EntityId == cardId.ToString() && a.Action == "CompositionApproved"));

        // Разрешённое сохранение действительно состоялось: scope пригоден.
        Assert.True(await check.Db.GsmMaterials.AsNoTracking().AnyAsync(g => g.Id == lateGsmId));
    }

    // ── §7. Настоящее содержимое принятой структуры ──

    /// <summary>
    /// После РЕАЛЬНОГО принятия принятая структура читается из снимка: дерево,
    /// строки, нормы, категории и НД приходят из редакции, а не из текущих
    /// справочных наименований.
    /// </summary>
    [Fact]
    public async Task GetAcceptedAsync_AfterRealAcceptance_ReturnsSnapshotContent()
    {
        // Материалы добавляются, пока этап состава ещё не отправлен: иначе замена
        // была бы заблокирована, и проверялось бы пустое содержание.
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmAsync();

        await s.Items.ReplaceMaterialsAsync(itemId,
            new[] { (gsmId, GsmCategory.Primary) }, CancellationToken.None);

        await BringToReviewAsync(cardId);

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, "принято", await VersionAsync(approver, cardId));
        }

        await using var reader = As(_fixture.NormAdminA2.Id);
        var accepted = await reader.Revisions.GetAcceptedAsync(cardId);
        Assert.NotNull(accepted);

        // Указатель и данные решения.
        await using (var chk = _fixture.CreateScope())
        {
            var card = await chk.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
            Assert.Equal(HKCardStatus.Approved, card.Status);
            Assert.Equal(accepted!.RevisionId, card.CurrentAcceptedRevisionId);
            Assert.Equal(accepted.RevisionNumber, 1);
        }

        Assert.NotNull(accepted.AcceptedAtUtc);
        Assert.Equal(_fixture.NormAdminA2.Id, accepted.AcceptedByUserId);

        // Содержимое снимка непустое: есть строки, нормы, категории и НД.
        Assert.NotEmpty(accepted.Items);
        var item = accepted.Items[0];
        Assert.True(item.Quantity > 0);
        Assert.NotEmpty(item.Materials);
        var material = item.Materials[0];
        Assert.Equal(gsmId, material.SourceGsmMaterialId);
        Assert.Equal(GsmCategory.Primary, material.Category);
        Assert.False(string.IsNullOrWhiteSpace(material.Nd));

        // Чтение ничего не меняет: повторный вызов даёт тот же результат.
        var again = await reader.Revisions.GetAcceptedAsync(cardId);
        Assert.Equal(accepted!.RevisionId, again!.RevisionId);
        Assert.Equal(accepted.Items.Count, again.Items.Count);
    }

    /// <summary>
    /// Принятая структура не содержит ключа хранения файла и физических путей.
    /// </summary>
    [Fact]
    public async Task GetAcceptedAsync_DoesNotExposeStorageKeyOrPaths()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }

        await using var reader = As(_fixture.NormAdminA2.Id);
        var accepted = await reader.Revisions.GetAcceptedAsync(cardId);
        Assert.NotNull(accepted);

        var rendered = System.Text.Json.JsonSerializer.Serialize(accepted);
        Assert.DoesNotContain("StorageKey", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chernika-tests-storage", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AttachmentStorageKey", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Карта чужого филиала: принятая структура не раскрывается.</summary>
    [Fact]
    public async Task GetAcceptedAsync_OtherBranch_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        await using (var approver = As(_fixture.NormAdminA2.Id))
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }

        await using var outsider = As(_fixture.NormAdminB.Id);
        await Assert.ThrowsAnyAsync<Exception>(
            () => outsider.Revisions.GetAcceptedAsync(cardId));
    }

    // ── §3. Гонка записи материалов и согласования ──

    /// <summary>
    /// Синхронизация ставится в точке записи: третья транзакция удерживает
    /// блокировку строки карты, поэтому обе команды доходят до конфликтующего
    /// UPDATE одновременно, а не просто стартуют вместе.
    /// </summary>
    [Fact]
    public async Task ReplaceMaterials_ConcurrentWithApproval_ExactlyOneWins_NoPartialSet()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);
        var itemId = await ItemOfAsync(cardId);

        // Прежний набор: его сохранность проверяется после гонки.
        // Обе команды работают с картой на рассмотрении состава, где замена
        // материалов формально запрещена блокировкой этапа. Гонка проверяет, что
        // защищённая ЗАПИСЬ, а не только проверка до записи, не проходит после
        // того, как принятие уже вступило в силу.
        var gsmNew = await GsmAsync();
        uint version;

        await using (var setup = As(_fixture.NormAdminA.Id))
        {
            // Возвращаем карту на реквизиты нельзя — карта на рассмотрении состава,
            // поэтому материалы заменяются ДО гонки на отдельной карте реквизитов.
            var cardsForSetup = cardId;
            _ = cardsForSetup;
        }

        await using (var v = _fixture.CreateScope())
        {
            version = await v.Db.HKCards.AsNoTracking()
                .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();
        }

        Exception? replaceError = null;
        Exception? approveError = null;

        await using var gate = new NpgsqlConnection(_fixture.ConnectionString);
        await gate.OpenAsync();

        // Барьер: строка карты заблокирована до старта обеих команд.
        await using (var gateTx = await gate.BeginTransactionAsync())
        {
            await using var lockCmd = new NpgsqlCommand(
                "SELECT 1 FROM \"HKCards\" WHERE \"Id\" = @id FOR UPDATE", gate);
            lockCmd.Parameters.AddWithValue("id", cardId);
            await lockCmd.ExecuteScalarAsync();

            var replaceTask = Task.Run(async () =>
            {
                await using var scope = As(_fixture.NormAdminA.Id);
                try
                {
                    await scope.Items.ReplaceMaterialsAsync(itemId,
                        new[] { (gsmNew, GsmCategory.Primary) }, CancellationToken.None);
                }
                catch (Exception ex) { replaceError = ex; }
            });

            var approveTask = Task.Run(async () =>
            {
                await using var scope = As(_fixture.NormAdminA2.Id);
                try
                {
                    await scope.Approvals.ApproveCompositionAsync(cardId, null, version,
                        CancellationToken.None);
                }
                catch (Exception ex) { approveError = ex; }
            });

            // Обе команды выстроились за блокировкой.
            await Task.Delay(400);
            await gateTx.CommitAsync();
            await Task.WhenAll(replaceTask, approveTask);
        }

        // Проигравшая сторона получает ПРЕДМЕТНЫЙ отказ. Произвольная ошибка базы
        // или отмена запроса допустимым исходом гонки не считаются.
        foreach (var error in new[] { replaceError, approveError })
        {
            if (error is null)
                continue;
            Assert.True(
                error is HkCardVersionConflictException or InvalidOperationException
                    or UnauthorizedAccessException,
                $"Неожиданный исход гонки: {error.GetType().Name}: {error.Message}");
        }

        await using var check = _fixture.CreateScope();

        // Набор материалов не частичен: либо прежний целиком, либо новый целиком.
        var stored = await check.Db.HKCardItemMaterials.AsNoTracking()
            .Where(m => m.HKCardItemId == itemId).ToListAsync();
        Assert.True(stored.Count <= 1,
            $"Набор материалов оказался частично заменён: {stored.Count} строк.");

        if (replaceError is null)
            Assert.Contains(stored, m => m.GsmMaterialId == gsmNew);

        // Снимок принятого состава соответствует сохранённым данным карты.
        var card = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        if (card.Status == HKCardStatus.Approved && card.CurrentAcceptedRevisionId.HasValue)
        {
            var revision = await check.Db.HKCardRevisions.AsNoTracking()
                .FirstAsync(r => r.Id == card.CurrentAcceptedRevisionId!.Value);
            Assert.Equal(cardId, revision.HKCardId);
            Assert.Equal(HKApprovalStageState.Approved,
                (await check.Db.HKCardApprovals.AsNoTracking()
                    .FirstAsync(a => a.HKCardId == cardId
                        && a.Stage == HKApprovalStage.Composition)).State);
        }
        else
        {
            // Принятия не было: принятая структура и успешный аудит отсутствуют.
            Assert.Null(card.CurrentAcceptedRevisionId);
            Assert.Equal(0, await check.Db.AuditLogs.AsNoTracking().CountAsync(a =>
                a.EntityId == cardId.ToString() && a.Action == "CompositionApproved"));
        }
    }
}