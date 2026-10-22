using System.Text;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Смена PDF-основания как согласованная бизнес-операция (PR № 10).
/// <para>
/// Проверяется главное: действующее основание и этапы согласования не могут
/// разойтись. Замена или допустимое удаление файла при ПРИНЯТЫХ реквизитах
/// обязано сбросить прежнее согласование (оно относилось к другому файлу) и
/// не может продолжить рассмотрение по прежнему основанию. Обратная сторона:
/// под текущим рассмотрением файл не подменяется, принятая карта не
/// разблокируется, а файл не уничтожается при неудачной записи.
/// </para>
/// <para>
/// Все проверки идут через сервисы: правила живут в сервисах, а тест лишь
/// исполняет команды и наблюдает состояние.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardAttachmentBasisChangeIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardAttachmentBasisChangeIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope(string userId)
    {
        var scope = _fixture.CreateScope();
        scope.User.CurrentUserId = Guid.Parse(userId);
        scope.Permissions.InvalidateCache(userId);
        return scope;
    }

    private TestScope Author() => Scope(_fixture.NormAdminA.Id);
    private TestScope Approver() => Scope(_fixture.NormAdminA2.Id);

    private static Stream PdfStream(string marker) =>
        new MemoryStream(Encoding.ASCII.GetBytes($"%PDF-1.4 {marker}"));

    /// <summary>Корень файлового хранилища тестовой фикстуры.</summary>
    private static string StorageRoot =>
        Path.Combine(Path.GetTempPath(), "chernika-tests-storage");

    private static string CardStorageDir(Guid cardId) =>
        Path.Combine(StorageRoot, "hk", cardId.ToString());

    /// <summary>
    /// Карта с загруженным основанием и принятыми реквизитами: состояние, из
    /// которого допустима смена основания.
    /// </summary>
    private async Task<Guid> CardWithApprovedDetailsAsync(TestScope s, string fileName = "scan.pdf")
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("first"), fileName, "application/pdf", 100, version);

        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
        await using (var approver = Approver())
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);

        return cardId;
    }

    /// <summary>
    /// Карта с загруженным основанием, но без принятых реквизитов: состояние,
    /// из которого меняется только файл.
    /// </summary>
    private async Task<Guid> CardWithPdfAsync(TestScope s, string fileName = "scan.pdf")
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("first"), fileName, "application/pdf", 100, version);
        return cardId;
    }

    // ── Первая загрузка: задач согласования не создаётся ──────────────────

    [Fact]
    public async Task FirstUpload_IntoDraft_CreatesNoApprovalTasks()
    {
        await using var s = Author();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        var result = await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("first"), "scan.pdf", "application/pdf", 100, version);

        Assert.False(result.ApprovalInvalidated);
        Assert.Equal(HKApprovalStageState.Draft,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details)).State);
        Assert.Equal(HKApprovalStageState.Draft,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);

        // Обычная загрузка файла не порождает ни задач согласования, ни
        // задач пересмотра: их создаёт только явная отправка пользователем.
        Assert.Equal(0, await s.Db.WorkTasks.CountAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId
            && (t.Type == WorkTaskType.HKReview
                || t.Type == WorkTaskType.HKCompositionReview
                || t.Type == WorkTaskType.HKExpirationReview)));
    }

    // ── Смена основания при принятых реквизитах ────────────────────────────

    [Fact]
    public async Task ReplacePdf_WhenDetailsApproved_ResetsDetailsStage_AndKeepsRowsAndHistory()
    {
        await using var s = Author();
        var cardId = await CardWithApprovedDetailsAsync(s);

        var detailsBefore = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.Approved, detailsBefore.State);
        Assert.NotNull(detailsBefore.ApprovedAtUtc);

        var nodesBefore = await s.Db.HKCardNodes.CountAsync(n => n.HKCardId == cardId);
        var itemsBefore = await s.Db.HKCardItems.CountAsync(i => i.HKCardId == cardId);
        Assert.True(itemsBefore > 0, "подготовка теста: у карты должна быть строка");

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        var result = await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, version);

        Assert.True(result.ApprovalInvalidated, "смена основания обязана сбрасывать согласование реквизитов");

        var detailsAfter = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.Draft, detailsAfter.State);
        // История решения сохраняется: принятым считается только актуальное
        // состояние этапа, а не прежняя отметка о принятии.
        Assert.Equal(detailsBefore.ApprovedAtUtc, detailsAfter.ApprovedAtUtc);
        Assert.Equal(HKApprovalStageState.Draft,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);

        // Строки и материалы не стираются.
        Assert.Equal(nodesBefore, await s.Db.HKCardNodes.CountAsync(n => n.HKCardId == cardId));
        Assert.Equal(itemsBefore, await s.Db.HKCardItems.CountAsync(i => i.HKCardId == cardId));

        // Новая задача рассмотрения до явной отправки не создаётся.
        Assert.Equal(0, await s.Db.WorkTasks.CountAsync(t =>
            t.EntityType == "HKCard" && t.EntityId == cardId
            && t.Status == WorkTaskStatus.Open
            && t.Type == WorkTaskType.HKReview));

        // Сброс зафиксирован в аудите, а основание — в аудите вложения.
        Assert.True(await s.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "HKCard" && a.EntityId == cardId.ToString()
            && a.Action == "DetailsInvalidated"));

        // Общий статус пересчитан: карта не осталась «на рассмотрении».
        var card = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.NotEqual(HKCardStatus.OnReview, card.Status);
    }

    [Fact]
    public async Task DeletePdf_WhenDetailsApproved_ResetsDetailsStage_Too()
    {
        await using var s = Author();
        var cardId = await CardWithApprovedDetailsAsync(s);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        var result = await s.HK.DeleteAttachmentAsync(cardId, version);

        Assert.True(result.ApprovalInvalidated);
        Assert.Null(await s.HK.GetAttachmentInfoAsync(cardId));
        Assert.Equal(HKApprovalStageState.Draft,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details)).State);
        Assert.True(await s.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "HKCard" && a.EntityId == cardId.ToString()
            && a.Action == "DetailsInvalidated"));
    }

    // ── Запреты по состоянию ──────────────────────────────────────────────

    [Fact]
    public async Task ReplacePdf_WhenDetailsOnReview_IsForbidden_AndFileUnchanged()
    {
        await using var s = Author();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("first"), "scan.pdf", "application/pdf", 100, version);
        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);

        // Файл под текущим рассмотрением реквизитов подменять нельзя.
        version = await HKApprovalScenario.VersionAsync(s, cardId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(
                cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, version));
        Assert.Contains("согласован", ex.Message, StringComparison.OrdinalIgnoreCase);

        var info = await s.HK.GetAttachmentInfoAsync(cardId);
        Assert.NotNull(info);
        Assert.Equal("scan.pdf", info!.OriginalFileName);
    }

    [Fact]
    public async Task ReplacePdf_WhenCompositionOnReview_IsForbidden()
    {
        await using var s = Author();
        var cardId = await CardWithApprovedDetailsAsync(s);

        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);
        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);

        // Состав согласуется по принятым реквизитам: подмена основания здесь
        // незаметно изменила бы предмет рассмотрения.
        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(
                cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, version));
        Assert.Contains("состав", ex.Message, StringComparison.OrdinalIgnoreCase);

        var info = await s.HK.GetAttachmentInfoAsync(cardId);
        Assert.Equal("scan.pdf", info!.OriginalFileName);
    }

    [Fact]
    public async Task AcceptedCard_PdfReplaceIsForbidden_AndAcceptedRevisionUntouched()
    {
        await using var s = Author();
        var cardId = await CardWithApprovedDetailsAsync(s);

        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);
        await using (var approver = Approver())
            await approver.Approvals.ApproveCompositionAsync(
                cardId, null, await HKApprovalScenario.VersionAsync(approver, cardId));

        var card = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, card.Status);
        Assert.NotNull(card.CurrentAcceptedRevisionId);
        var revisionId = card.CurrentAcceptedRevisionId;

        // Принятая карта не разблокируется обходным путём: новый запуск
        // доработки в этом PR не появился.
        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(
                cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, version));

        var after = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(HKCardStatus.Approved, after.Status);
        Assert.Equal(revisionId, after.CurrentAcceptedRevisionId);
    }

    // ── Конкуренция и целостность файла ───────────────────────────────────

    [Fact]
    public async Task ReplacePdf_WithStaleVersion_Conflicts_AndKeepsPreviousFile()
    {
        await using var s = Author();
        var cardId = await CardWithPdfAsync(s);

        var staleVersion = await HKApprovalScenario.VersionAsync(s, cardId);

        // Версия устарела: карта изменилась после её чтения. Состояние при
        // этом остаётся разрешающим — отказ вызван именно конфликтом версии.
        await using (var approver = Approver())
        {
            await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);
        }

        var filesBefore = Directory.GetFiles(CardStorageDir(cardId)).Length;

        await Assert.ThrowsAsync<HkCardVersionConflictException>(() =>
            s.HK.SaveAttachmentAsync(
                cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, staleVersion));

        // Прежнее вложение цело, новый файл не создавался.
        var info = await s.HK.GetAttachmentInfoAsync(cardId);
        Assert.NotNull(info);
        Assert.Equal("scan.pdf", info!.OriginalFileName);
        Assert.Equal(filesBefore, Directory.GetFiles(CardStorageDir(cardId)).Length);
    }

    [Fact]
    public async Task ReplacePdf_WhenDatabaseWriteFails_PreviousFileSurvives_AndNewFileRemoved()
    {
        await using var s = Author();
        var cardId = await CardWithApprovedDetailsAsync(s);

        var previous = await s.Db.HKCardAttachments.AsNoTracking()
            .SingleAsync(a => a.HKCardId == cardId);
        var filesBefore = Directory.GetFiles(CardStorageDir(cardId)).Length;

        // Сбой на границе записи: файл уже положен в хранилище, но запись в БД
        // не проходит. Прежний файл при этом не должен пострадать.
        FailingCommandInterceptor.ArmAt(@"INSERT INTO ""HKCardAttachments""");
        bool fired;
        Exception? failure = null;
        try
        {
            var version = await HKApprovalScenario.VersionAsync(s, cardId);
            failure = await Assert.ThrowsAnyAsync<Exception>(() => s.HK.SaveAttachmentAsync(
                cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, version));

            // Признак срабатывания читается ДО сброса armed-состояния: он
            // живёт в AsyncLocal, и обнуление перехватчика его стирает.
            fired = FailingCommandInterceptor.Fired;
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        Assert.True(fired,
            $"перехватчик не сработал — тест проверил бы не то; исключение: {failure}");

        var after = await s.Db.HKCardAttachments.AsNoTracking()
            .SingleAsync(a => a.HKCardId == cardId);
        Assert.Equal(previous.Id, after.Id);
        Assert.Equal(previous.Sha256, after.Sha256);
        Assert.Equal("scan.pdf", after.OriginalFileName);

        // Незафиксированный новый файл убран: в каталоге карты остался ровно
        // один файл — прежний.
        Assert.Equal(filesBefore, Directory.GetFiles(CardStorageDir(cardId)).Length);
    }

    // ── Рассмотрение не продолжается по прежнему основанию ────────────────

    [Fact]
    public async Task AfterBasisChange_CompositionCannotBeSubmittedUntilDetailsApprovedAgain()
    {
        await using var s = Author();
        var cardId = await CardWithApprovedDetailsAsync(s);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, version);

        // Реквизиты снова черновик: отправка состава по прежнему основанию
        // невозможна.
        version = await HKApprovalScenario.VersionAsync(s, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Approvals.SubmitCompositionAsync(cardId, version));

        // После повторного согласования реквизитов отправка состава доступна.
        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
        await using (var approver = Approver())
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);

        version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.Approvals.SubmitCompositionAsync(cardId, version);
        Assert.Equal(HKApprovalStageState.OnReview,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);
    }

    [Fact]
    public async Task ResubmitComposition_ActuallyRunsValidationOfSavedStructure()
    {
        await using var s = Author();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        // Структурная позиция: для автономной карты состав проверяется по дереву.
        await s.HK.SaveTreeAsync(cardId, new[]
        {
            new HKCardNodeWrite(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, 0)
        });
        await HKApprovalScenario.ApplyDerivedLevelAsync(s, cardId);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("first"), "scan.pdf", "application/pdf", 100, version);
        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
        await using (var approver = Approver())
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);

        // Смена основания сбрасывает реквизиты: рассмотрение по прежнему
        // основанию продолжаться не может.
        version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("second"), "scan2.pdf", "application/pdf", 100, version);

        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
        await using (var approver = Approver())
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);

        // Содержание испорчено правкой в базе: уровень позиции выставлен в
        // недопустимое значение. База такие значения пропускает (уровень — целое
        // число), а проверка состава — нет.
        var node = await s.Db.HKCardNodes.FirstAsync(n => n.HKCardId == cardId);
        node.Level = (HKObjectLevel)99;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var stored = await s.Db.HKCardNodes.AsNoTracking().FirstAsync(n => n.HKCardId == cardId);
        Assert.Equal(99, (int)stored.Level);

        // Повторная отправка действительно проверяет сохранённую структуру.
        var nodesNow = await s.Db.HKCardNodes.AsNoTracking()
            .Where(n => n.HKCardId == cardId).ToListAsync();
        Assert.NotEmpty(nodesNow);
        Assert.Equal(99, (int)nodesNow[0].Level);
        Assert.Equal(HKApprovalStageState.Draft,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);
        var cardState = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.True(cardState.IsAutonomous, "проверка структуры выполняется только для автономной карты");

        version = await HKApprovalScenario.VersionAsync(s, cardId);
        var probeCard = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var probe = await s.Validation.ValidateForReviewAsync(probeCard);
        Assert.False(probe.IsValid,
            $"валидатор не увидел испорченную позицию: {string.Join("; ", probe.Errors.Select(e => e.Message))} " +
            $"автономная={probeCard.IsAutonomous}, уровень={probeCard.ObjectLevel}");

        Exception? caught = null;
        try
        {
            await s.Approvals.SubmitCompositionAsync(cardId, version);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.True(caught is HKCardValidationException,
            $"ожидалась ошибка проверки содержания, получено: {caught?.GetType().Name ?? "без исключения"}: {caught?.Message}");

        // Этап состава остался черновиком: испорченное содержание на
        // рассмотрение не ушло.
        Assert.Equal(HKApprovalStageState.Draft,
            (await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition)).State);
    }

    // ── Три даты не смешиваются ────────────────────────────────────────────

    [Fact]
    public async Task ApprovingDetails_DoesNotSetEffectiveOrExpirationDates()
    {
        await using var s = Author();
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, _fixture.NormAdminA2.Id);
        await using (var approver = Approver())
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);

        var card = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        // Даты действия карты не подставляются принятием реквизитов: это
        // отдельные даты, задаваемые пользователем.
        Assert.Null(card.EffectiveDate);
        Assert.Null(card.ExpirationDate);

        // Дата принятия реквизитов живёт в этапе, а не в датах действия.
        var details = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.Approved, details.State);
        Assert.NotNull(details.ApprovedAtUtc);
    }
}
