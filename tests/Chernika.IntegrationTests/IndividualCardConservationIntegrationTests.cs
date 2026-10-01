using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Граница законсервированного модуля индивидуальных карт (PR-5 §2).
/// <para>
/// Проверяется ровно то, что требует владелец: прямой вызов отключённой операции
/// получает ОПРЕДЕЛЁННЫЙ контролируемый отказ и НЕ пишет в БД, а чтение
/// сохранённых документов продолжает работать. Скрытых кнопок недостаточно —
/// поэтому тесты бьют по сервису напрямую.
/// </para>
/// <para>
/// Отказ проверяется без записи в том же scope, что и вызов: это ловит опасный
/// класс дефектов — «отказались, но успели создать черновик».
/// </para>
/// </summary>
[Collection("Database")]
public class IndividualCardConservationIntegrationTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly List<Guid> _created = new();

    public IndividualCardConservationIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Общая тестовая БД накапливает данные, поэтому созданные здесь ИК удаляются
    /// после каждого теста: чужой тест не должен ловить «лишние» строки реестра
    /// или менять порядок выборки (xUnit создаёт новый экземпляр класса на тест).
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_created.Count == 0) return;
        await using var s = _fixture.CreateScope();
        foreach (var id in _created)
        {
            var card = await s.Db.IndividualCards.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id);
            if (card is not null)
                s.Db.IndividualCards.Remove(card);
        }

        await s.Db.SaveChangesAsync();
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsSystemAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

    [Fact]
    public void DefaultConfiguration_KeepsModuleDisabled()
    {
        // Боевые регистрации не включают раздел IndividualCards, поэтому
        // настройка по умолчанию — выключенный модуль. Проверяется на самой
        // настройке, чтобы случайное включение в Development не прошло unnoticed.
        Assert.False(new Chernika.Domain.IndividualCardModuleOptions().Enabled);
    }

    [Fact]
    public async Task DisabledModule_RefusesCreateDraft_WithoutWriting()
    {
        var nodeId = await SeedNodeAsync();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        s.IndividualCardModule.Disable();

        var before = await s.Db.IndividualCards.CountAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateDraftAsync(
                new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId)));

        Assert.Contains("законсервирован", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await s.Db.IndividualCards.CountAsync());
    }

    [Fact]
    public async Task DisabledModule_RefusesPreflight_WithoutWriting()
    {
        var nodeId = await SeedNodeAsync();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        s.IndividualCardModule.Disable();

        var before = await s.Db.IndividualCards.CountAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildPreflightAsync(
                new IndividualCardPreflightRequest(IndividualCardObjectLevel.Node, nodeId)));

        Assert.Equal(before, await s.Db.IndividualCards.CountAsync());
    }

    [Fact]
    public async Task DisabledModule_RefusesRecalculateFormAndVersion_WithoutWriting()
    {
        var cardId = await SeedFormedCardAsync();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        s.IndividualCardModule.Disable();

        var before = await s.Db.IndividualCards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        var auditBefore = await s.Db.AuditLogs.CountAsync(a => a.EntityId == cardId.ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(cardId, new List<Guid>())));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(cardId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.GetDraftCalculationAsync(cardId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.BuildNewVersionComparisonAsync(
                new IndividualCardVersionPreflightRequest(cardId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.CreateNewVersionAsync(new CreateIndividualCardVersionRequest(cardId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.ArchiveIndividualCardAsync(cardId));

        // Ни одна из операций не изменила документ и не оставила аудит.
        var after = await s.Db.IndividualCards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ArchivedAt, after.ArchivedAt);
        Assert.Equal(auditBefore, await s.Db.AuditLogs.CountAsync(a => a.EntityId == cardId.ToString()));
    }

    [Fact]
    public async Task DisabledModule_RefusesNotesUpdateAndDelete_WithoutWriting()
    {
        var cardId = await SeedFormedCardAsync();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        s.IndividualCardModule.Disable();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.UpdateNotesAsync(cardId, "Правка"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.DeleteCardAsync(cardId));

        Assert.True(await s.Db.IndividualCards.AnyAsync(c => c.Id == cardId));
    }

    [Fact]
    public async Task DisabledModule_RefusesExport_WithoutWritingAudit()
    {
        var cardId = await SeedFormedCardAsync();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        s.IndividualCardModule.Disable();

        // Граница модуля проверяется ПЕРВОЙ, поэтому отказ не зависит от полноты
        // сохранённых данных: export-read-model недоступна целиком.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.GetExportAsync(cardId));
        Assert.Contains("законсервирован", ex.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecordPdfExportAsync(cardId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecordXlsxExportAsync(cardId));

        // Экспорт не состоялся — аудит успешного экспорта не пишется.
        Assert.False(await s.Db.AuditLogs.AnyAsync(a => a.EntityId == cardId.ToString()));
    }

    [Fact]
    public async Task DisabledModule_StillServesHistoricalRead()
    {
        // Консервация не равна удалению: сохранённые документы остаются
        // доступными для чтения, иначе наработки потеряли бы смысл.
        var cardId = await SeedFormedCardAsync();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        s.IndividualCardModule.Disable();

        Assert.NotNull(await s.IndividualCards.GetCardAsync(cardId));
        Assert.NotNull(await s.IndividualCards.GetDetailAsync(cardId));
        Assert.NotNull(await s.IndividualCards.GetHistoryAsync(cardId));
        Assert.NotNull(await s.IndividualCards.GetIndividualCardActionHeaderAsync(cardId));

        var registry = await s.IndividualCards.GetRegistryAsync(
            new IndividualCardRegistryQuery(
                null, null, null, null, null, null, false, false, "CreatedAt", true, 1, 200));
        Assert.Contains(registry.Items, i => i.Id == cardId);
    }

    [Fact]
    public async Task EnabledModule_KeepsTheConservedCodeWorking()
    {
        // Обратная сторона границы: с явным включением сохранённый код и его
        // покрытие продолжают работать. Без этого «консервация» была бы
        // недоказуемой правкой, которую нельзя возобновить.
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);

        Assert.True(s.IndividualCardModule.IsEnabled);

        // Узел с утверждённой ХК: без нормативной цепочки черновик не создаётся
        // даже при включённом модуле, и тест проверял бы не то.
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-" + Suffix(),
            Name = "Узел " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.Nodes.Add(node);
        s.Db.HKCards.Add(new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = HKCardStatus.Approved,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            BranchId = _fixture.BranchA,
            ApprovedDate = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, node.Id));

        Assert.NotEqual(Guid.Empty, draft.Id);
        Assert.Equal(IndividualCardStatus.Draft, draft.Status);
        Assert.True(await s.Db.IndividualCards.AnyAsync(c => c.Id == draft.Id));
        _created.Add(draft.Id);
    }

    [Fact]
    public async Task CoefficientOperations_AreNotBlockedByConservation()
    {
        // Коэффициенты — действующий справочник, а не модуль ИК. Граница модуля их
        // закрывать не должна: иначе консервация ИК сломала бы действующий
        // раздел.
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        s.IndividualCardModule.Disable();

        Assert.NotNull(await s.IndividualCards.GetCoefficientTypesAsync());
        Assert.NotNull(await s.IndividualCards.GetAvailableCoefficientsAsync());
    }

    private async Task<Guid> SeedNodeAsync()
    {
        await using var s = _fixture.CreateScope();
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-" + Suffix(),
            Name = "Узел " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    /// <summary>
    /// Одна сформированная ИК. Создаётся напрямую в БД, а не через сервис: при
    /// выключенном модуле сервисные операции записи недоступны, а чтение нужно
    /// именно для проверки того, что данные целы.
    /// </summary>
    private async Task<Guid> SeedFormedCardAsync()
    {
        await using var s = _fixture.CreateScope();
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-" + Suffix(),
            Name = "Узел " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.Nodes.Add(node);

        var unit = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "AU-" + Suffix(),
            Name = "СЕ " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.AssemblyUnits.Add(unit);

        var card = new IndividualCard
        {
            Id = Guid.NewGuid(),
            Code = "ИК-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = IndividualCardStatus.Formed,
            ObjectLevel = IndividualCardObjectLevel.Node,
            NodeId = node.Id,
            BranchId = _fixture.BranchA,
            CreatedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
            // Статус «Сформирована» требует пары сформирования: ограничение
            // CK_IndividualCards_StatusMetadata в БД.
            FormedAt = DateTime.UtcNow,
            FormedByUserId = _fixture.SystemAdminUser.Id,
        };
        s.Db.IndividualCards.Add(card);
        await s.Db.SaveChangesAsync();
        _created.Add(card.Id);
        return card.Id;
    }
}
