using System.Text;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Ложный конфликт версий при сохранении черновика / отправке после операции
/// с PDF-вложением.
/// <para>
/// Операция с вложением при неудаче очищает change tracker общего контекста
/// (<c>ChangeTracker.Clear()</c> в catch-блоке). Отображаемый объект карты
/// страницы при этом становится detached: первое последующее сохранение
/// проходит, но EF записывает новую версию строки у своего отслеживаемого
/// экземпляра, а detached-объект отображения отстаёт. Следующее сохранение
/// через <c>UpdateAsync(card)</c> читает его устаревший RowVersion и получает
/// ложный конфликт «Карточка была изменена другим пользователем», хотя карту
/// никем, кроме самой страницы, не меняли.
/// </para>
/// </summary>
[Collection("Database")]
public class HkPdfVersionConflictReproTests
{
    private readonly TestDatabaseFixture _fixture;
    public HkPdfVersionConflictReproTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope(string userId)
    {
        var scope = _fixture.CreateScope();
        scope.User.CurrentUserId = Guid.Parse(userId);
        scope.Permissions.InvalidateCache(userId);
        return scope;
    }

    private static Stream PdfStream(string marker) =>
        new MemoryStream(Encoding.ASCII.GetBytes($"%PDF-1.4 {marker}"));

    /// <summary>
    /// Симптом дефекта: после очистки change tracker (неудачная операция с
    /// вложением) объект карты страницы отсоединяется от контекста. Первое
    /// сохранение проходит, но EF записывает новую версию у СВОЕГО
    /// отслеживаемого экземпляра, а отсоединённый объект отображения отстаёт.
    /// Следующее сохранение падает ложным конфликтом, хотя карту никем, кроме
    /// самой страницы, не меняли.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_AfterChangeTrackerClear_DetachedCard_ResyncsVersion_NoFalseConflict()
    {
        var s = Scope(_fixture.NormAdminA.Id);

        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        // Загрузка карты страницей: tracked-объект с версией N.
        var card = await s.HK.GetByIdAsync(cardId);

        // Неудачная операция с вложением очищает трекер общего контекста
        // (catch-блок SaveAttachmentAsync/DeleteAttachmentAsync). Объект карты
        // страницы отсоединяется.
        s.Db.ChangeTracker.Clear();

        // Первое сохранение черновика после сбоя: версия объекта совпадает с
        // базой, команда проходит; строка переписывается, версия растёт.
        card.Notes = "правка 1";
        await s.HK.UpdateAsync(card);

        // Второе сохранение без перезагрузки страницы: без привязки объекта к
        // свежей версии оно падало ложным конфликтом.
        card.Notes = "правка 2";
        await s.HK.UpdateAsync(card);

        var dbVersion = (await s.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == cardId)).RowVersion;
        Assert.Equal(dbVersion, card.RowVersion);
    }

    /// <summary>
    /// Сервис возвращает актуальную версию строки карты после операции с
    /// вложением: страница привязывается к ней, не выполняя лишнего чтения.
    /// </summary>
    [Fact]
    public async Task SaveAttachmentAsync_ReturnsFreshCardRowVersion()
    {
        var s = Scope(_fixture.NormAdminA.Id);

        var cardId = await HKApprovalScenario.CreateCardWithApprovedDetailsAsync(
            s, _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id);
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        s.Db.ChangeTracker.Clear();
        var card = await s.HK.GetByIdAsync(cardId);

        var result = await s.HK.SaveAttachmentAsync(
            cardId, PdfStream("b"), "scan.pdf", "application/pdf", 100, card.RowVersion);
        Assert.True(result.ApprovalInvalidated);

        var dbVersion = (await s.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == cardId)).RowVersion;
        Assert.Equal(dbVersion, result.CardRowVersion);

        // Отправка на проверку после операции с PDF проходит с версией из
        // результата (или со свежим чтением) и не даёт ложного конфликта.
        await s.Approvals.SubmitDetailsAsync(
            cardId, _fixture.NormAdminA2.Id, result.CardRowVersion);

        var stage = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Details);
        Assert.Equal(HKApprovalStageState.OnReview, stage.State);
    }
}
