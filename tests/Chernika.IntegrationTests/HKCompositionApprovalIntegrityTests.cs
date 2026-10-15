using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Целостность и безопасность окончательного принятия.
/// <para>
/// Каждый тест закрывает конкретное замечание к PR № 6: решения принимаются по
/// СОХРАНЁННОМУ состоянию, этапы не создаются попутно, принимается только
/// полноценный состав, проверено основание, конфликты контролируемы.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCompositionApprovalIntegrityTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCompositionApprovalIntegrityTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<TestScope> AsAsync(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    private Task<TestScope> ApproverAsync() => AsAsync(_fixture.NormAdminA2.Id);

    private async Task<Guid> CardReadyAsync(TestScope s)
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        await BringToReviewAsync(cardId);
        return cardId;
    }

    /// <summary>
    /// Доводит уже подготовленную карту до состояния «состав на рассмотрении».
    /// </summary>
    private async Task BringToReviewAsync(Guid cardId)
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

    // ── Решение по сохранённому состоянию, а не по изменённым объектам ──

    /// <summary>
    /// Этап заранее загружен в контекст и ИЗМЕНЁН в памяти до команды. Принятие
    /// обязано опираться на сохранённое состояние, а не на подменённый экземпляр.
    /// </summary>
        /// <summary>
    /// Отслеживаемый экземпляр этапа подменён ДО вызова команды: посторонний
    /// пользователь назначает себя согласующим в памяти.
    /// <para>
    /// Команда обязана отказать: решение принимает согласующий ПО СОХРАНЁННОГО
    /// состояния, а не по изменённому в памяти объекту.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ApproveComposition_IgnoresTamperedTrackedStage()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        // Отдельный входной scope: этап загружен в его контекст.
        await using var caller = await AsAsync(_fixture.NormAdminA.Id);
        var tracked = await caller.Db.HKCardApprovals
            .FirstAsync(a => a.HKCardId == cardId && a.Stage == HKApprovalStage.Composition);

        var cardVersion = await VersionAsync(caller, cardId);

        // Подмена в памяти: себя согласующим и этап принятым.
        tracked.AssignedApproverUserId = _fixture.NormAdminA.Id;
        tracked.State = HKApprovalStageState.Approved;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => caller.Approvals.ApproveCompositionAsync(cardId, null, cardVersion));

        // Подмена не записалась: проверка идёт через отдельное соединение, а не
        // через очистку трекера.
        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        var stored = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition);
        Assert.Equal(HKApprovalStageState.OnReview, stored.State);
        Assert.Equal(_fixture.NormAdminA2.Id, stored.AssignedApproverUserId);
        Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
    }

    /// <summary>
    /// Этап изменён ДРУГИМ DbContext между предварительной проверкой и транзакционным
    /// чтением: команда обязана увидеть сохранённое изменение.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_SeesChangeMadeByAnotherContext()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        // Предварительное чтение версии.
        await using var caller = await ApproverAsync();
        var version = await VersionAsync(caller, cardId);

        // Другой контекст возвращает состав с рассмотрения ДО вызова команды.
        await using var other = await ApproverAsync();
        await other.Approvals.ReturnCompositionAsync(cardId, null, await VersionAsync(other, cardId));

        // Конкурентное изменение карты даёт предметный конфликт версии, а не
        // случайную ошибку базы.
        await Assert.ThrowsAsync<HkCardVersionConflictException>(
            () => caller.Approvals.ApproveCompositionAsync(cardId, null, version));

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        Assert.Equal(HKApprovalStageState.RevisionRequired,
            (await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition)).State);
        Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
    }

    // ── Этапы не создаются попутно с принятием ──

    [Fact]
    public async Task ApproveComposition_WithoutStages_CreatesNothing()
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            await AsAsync(_fixture.NormAdminA.Id), _fixture.NormAdminA.Id);

        // Этапы удалены: состояние «нет этапов» недопустимо для принятия.
        await using (var setup = _fixture.CreateScope())
        {
            await setup.Db.HKCardApprovals.Where(a => a.HKCardId == cardId).ExecuteDeleteAsync();
        }

        await using var approver = await ApproverAsync();
        var version = await VersionAsync(approver, cardId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));

        await using var check = await AsAsync(_fixture.NormAdminA.Id);

        // Ничего не создано и не сохранено.
        Assert.Equal(0, await check.Db.HKCardApprovals.CountAsync(a => a.HKCardId == cardId));
        Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Equal(0, await check.Db.WorkTasks.CountAsync(t => t.EntityId == cardId && !t.IsDeleted));
        Assert.Equal(0, await check.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityId == cardId.ToString() && a.Action == "CompositionApproved"));
        Assert.Equal(HKCardStatus.Draft,
            (await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).Status);
    }

    // ── Полноценная проверка принимаемого состава ──

    /// <summary>
    /// Сохранённый состав без конечных строк не принимается. ValidateDraftAsync
    /// этот дефект не видел: он проверяет реквизиты, а не наполнение.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_SavedCompositionWithoutRows_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);

        // Повреждённое сохранённое состояние: строки удалены в обход сервиса.
        // Ограничение FK не запрещает удаление строки при отсутствии ссылок на неё.
        await using (var damage = _fixture.CreateScope())
        {
            await damage.Db.HKCardItems.Where(i => i.HKCardId == cardId).ExecuteDeleteAsync();
        }

        await using var approver = await ApproverAsync();
        var version = await VersionAsync(approver, cardId);

        await Assert.ThrowsAnyAsync<Exception>(
            () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition)).State);
        Assert.NotEqual(HKCardStatus.Approved,
            (await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).Status);
        Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
    }

    // ── Основание проверяется в самой команде ──

    [Fact]
    public async Task ApproveComposition_WithMissingPdfFile_IsRefused()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n%% original\n%%EOF\n");

        // Основание прикрепляется ДО отправки реквизитов: после принятия этапа
        // PDF заблокирован вместе с реквизитами, и это правильное поведение.
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        await s.HK.SaveAttachmentAsync(
            cardId, new MemoryStream(bytes), "scan.pdf", "application/pdf", bytes.Length);

        await BringToReviewAsync(cardId);

        // Файл основания удаляется с диска: запись вложения остаётся, содержимого нет.
        string storageKey;
        await using (var read = _fixture.CreateScope())
        {
            storageKey = await read.Db.HKCardAttachments.AsNoTracking()
                .Where(a => a.HKCardId == cardId).Select(a => a.StorageKey).FirstAsync();
        }

        var path = Path.Combine(Path.Combine(Path.GetTempPath(), "chernika-tests-storage"),
            storageKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path));
        File.Delete(path);

        try
        {
            await using var approver = await ApproverAsync();
            var version = await VersionAsync(approver, cardId);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));
        }
        finally
        {
            // Файл восстанавливается: общий временный каталог используют другие тесты.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Composition)).State);
        Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Null(await check.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync());
    }

    /// <summary>
    /// Основание с изменённым содержимым отвергается: контрольная сумма
    /// не совпадает с сохранённой.
    /// </summary>
    [Fact]
    public async Task ApproveComposition_WithCorruptedPdfFile_IsRefused()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%% good\n%%EOF\n");

        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);
        await s.HK.SaveAttachmentAsync(
            cardId, new MemoryStream(bytes), "scan.pdf", "application/pdf", bytes.Length);

        await BringToReviewAsync(cardId);

        string storageKey;
        await using (var read = _fixture.CreateScope())
        {
            storageKey = await read.Db.HKCardAttachments.AsNoTracking()
                .Where(a => a.HKCardId == cardId).Select(a => a.StorageKey).FirstAsync();
        }

        var path = Path.Combine(Path.Combine(Path.GetTempPath(), "chernika-tests-storage"),
            storageKey.Replace('/', Path.DirectorySeparatorChar));
        var original = await File.ReadAllBytesAsync(path);

        try
        {
            // Содержимое подменено при том же размере: спасает только контрольная сумма.
            await File.WriteAllBytesAsync(path, System.Text.Encoding.ASCII.GetBytes(
                "%PDF-1.4\n%% BAD!\n%%EOF\n"));

            await using var approver = await ApproverAsync();
            var version = await VersionAsync(approver, cardId);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => approver.Approvals.ApproveCompositionAsync(cardId, null, version));
        }
        finally
        {
            await File.WriteAllBytesAsync(path, original);
        }

        await using var check = await AsAsync(_fixture.NormAdminA.Id);
        Assert.Equal(0, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
    }

    // ── ReplaceMaterialsAsync ──

    private async Task<Guid> ItemOfAsync(Guid cardId)
    {
        await using var read = _fixture.CreateScope();
        return await read.Db.HKCardItems.AsNoTracking()
            .Where(i => i.HKCardId == cardId).Select(i => i.Id).FirstAsync();
    }

    private async Task<Guid> GsmIdAsync()
    {
        await using var s = _fixture.CreateScope();
        var gsm = new GsmMaterial { Id = Guid.NewGuid(), Name = "М-" + Suffix(), Nd = "ТУ" };
        s.Db.GsmMaterials.Add(gsm);
        await s.Db.SaveChangesAsync();
        return gsm.Id;
    }

    private async Task<(int Total, int Open, int Closed)> TaskStateAsync(Guid cardId)
    {
        await using var s = _fixture.CreateScope();
        var tasks = await s.Db.WorkTasks.AsNoTracking().Where(t => t.EntityId == cardId).ToListAsync();
        var open = tasks.Count(t => t.Status is WorkTaskStatus.Open
            or WorkTaskStatus.InProgress or WorkTaskStatus.Overdue);
        return (tasks.Count, open, tasks.Count - open);
    }

    /// <summary>На рассмотрении состава замена материалов запрещена.</summary>
    [Fact]
    public async Task ReplaceMaterials_WhileCompositionOnReview_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);
        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmIdAsync();

        await using var author = await AsAsync(_fixture.NormAdminA.Id);
        var existing = await author.Db.HKCardItemMaterials.AsNoTracking()
            .Where(m => m.HKCardItemId == itemId).Select(m => m.Id).ToListAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => author.Items.ReplaceMaterialsAsync(itemId, new[] { (gsmId, GsmCategory.Primary) }));

        // Прежние материалы сохранились: отказ до изменения набора.
        await using var check = _fixture.CreateScope();
        Assert.Equal(existing,
            await check.Db.HKCardItemMaterials.AsNoTracking()
                .Where(m => m.HKCardItemId == itemId).Select(m => m.Id).ToListAsync());
    }

    /// <summary>
    /// Во время рассмотрения РЕКВИЗИТОВ автор по-прежнему может заполнять
    /// разрешённые строки и материалы: блокирует этап состава, а не общий статус.
    /// </summary>
    [Fact]
    public async Task ReplaceMaterials_WhileDetailsOnReview_IsAllowed()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        await HKApprovalScenario.SubmitDetailsAsync(
            await AsAsync(_fixture.NormAdminA.Id), cardId, _fixture.NormAdminA2.Id);

        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmIdAsync();

        await using var author = await AsAsync(_fixture.NormAdminA.Id);
        await author.Items.ReplaceMaterialsAsync(itemId, new[] { (gsmId, GsmCategory.Reserve) });
        await author.Db.SaveChangesAsync();

        await using var check = _fixture.CreateScope();
        var stored = await check.Db.HKCardItemMaterials.AsNoTracking()
            .Where(m => m.HKCardItemId == itemId).ToListAsync();
        Assert.Contains(stored, m => m.GsmMaterialId == gsmId && m.Category == GsmCategory.Reserve);
    }

    [Fact]
    public async Task ReplaceMaterials_AfterAcceptance_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);
        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmIdAsync();

        await using (var approver = await ApproverAsync())
        {
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await VersionAsync(approver, cardId));
        }

        await using var author = await AsAsync(_fixture.NormAdminA.Id);
        var before = await author.Db.HKCardItemMaterials.AsNoTracking()
            .Where(m => m.HKCardItemId == itemId).Select(m => m.Id).ToListAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => author.Items.ReplaceMaterialsAsync(itemId, new[] { (gsmId, GsmCategory.Primary) }));

        await using var check = _fixture.CreateScope();
        Assert.Equal(before, await check.Db.HKCardItemMaterials.AsNoTracking()
            .Where(m => m.HKCardItemId == itemId).Select(m => m.Id).ToListAsync());
    }

    [Fact]
    public async Task ReplaceMaterials_ByOtherBranch_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);
        var itemId = await ItemOfAsync(cardId);
        var gsmId = await GsmIdAsync();

        await using var outsider = await AsAsync(_fixture.NormAdminB.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => outsider.Items.ReplaceMaterialsAsync(itemId, new[] { (gsmId, GsmCategory.Primary) }));
    }

    [Fact]
    public async Task ReplaceMaterials_WithUnavailableGsm_IsRefused()
    {
        await using var s = await AsAsync(_fixture.NormAdminA.Id);
        var cardId = await CardReadyAsync(s);
        var itemId = await ItemOfAsync(cardId);

        await using var author = await AsAsync(_fixture.NormAdminA.Id);
        var before = await author.Db.HKCardItemMaterials.AsNoTracking()
            .Where(m => m.HKCardItemId == itemId).Select(m => m.Id).ToListAsync();

        // Ссылка на марку, которой нет: FK её отверг бы при записи, но проверка
        // обязана отказать ДО изменения набора.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => author.Items.ReplaceMaterialsAsync(
                itemId, new[] { (Guid.NewGuid(), GsmCategory.Primary) }));

        await using var check = _fixture.CreateScope();
        Assert.Equal(before, await check.Db.HKCardItemMaterials.AsNoTracking()
            .Where(m => m.HKCardItemId == itemId).Select(m => m.Id).ToListAsync());
    }
}
