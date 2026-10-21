using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Регистрация объектов принятой структуры в библиотеке.
/// <para>
/// При окончательном принятии состава объекты из строк ХК и сама карта
/// попадают в классификаторы библиотеки (изделия, комплексы, агрегаты, узлы).
/// Повторная регистрация не должна дублировать объекты.
/// </para>
/// </summary>
[Collection("Database")]
public class HKLibraryRegistrationIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKLibraryRegistrationIntegrationTests(TestDatabaseFixture fixture) =>
        _fixture = fixture;

    private TestScope As(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    /// <summary>
    /// Готовит автономный черновик со структурой: агрегат с узлом внутри.
    /// </summary>
    private static async Task<(Guid CardId, string CardName, string AggregateName, string NodeName)>
        PrepareCardAsync(TestScope s, string actorId)
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, actorId, withRow: false);
        var cardName = await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => c.Name!)
            .FirstAsync();

        var aggregateName = "Агрегат насоса " + HKApprovalScenario.Suffix();
        var nodeName = "Узел редуктора " + HKApprovalScenario.Suffix();

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            Array.Empty<HKCardSectionWrite>(),
            new[]
            {
                new HKCardStructureNodeWrite(
                    Guid.NewGuid(), null, HKObjectLevel.Aggregate, aggregateName, 2, 0, null),
                new HKCardStructureNodeWrite(
                    Guid.NewGuid(), null, HKObjectLevel.Node, nodeName, 3, 1, null)
            },
            Array.Empty<HKCardStructureItemWrite>(), null, version));

        await HKApprovalScenario.ApplyDerivedLevelAsync(s, cardId);

        return (cardId, cardName, aggregateName, nodeName);
    }

    [Fact]
    public async Task ApproveComposition_RegistersStructureObjectsAndCardInLibrary()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var prepared = await PrepareCardAsync(s, _fixture.NormAdminA.Id);

        await HKApprovalScenario.SubmitDetailsAsync(s, prepared.CardId, _fixture.NormAdminA2.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(s, prepared.CardId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitCompositionAsync(s, prepared.CardId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await s.Approvals.ApproveCompositionAsync(
            prepared.CardId, null, await HKApprovalScenario.VersionAsync(s, prepared.CardId));

        await using var read = _fixture.CreateScope();

        // Объекты строк принятой структуры попали в библиотеку.
        Assert.True(await read.Db.Aggregates.AnyAsync(a => a.Name == prepared.AggregateName));
        Assert.True(await read.Db.Nodes.AnyAsync(n => n.Name == prepared.NodeName));

        // Сама карта зарегистрирована объектом своего уровня: верхняя позиция
        // карты — агрегат, значит карта уровня изделия.
        var model = await read.Db.EquipmentModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Name == prepared.CardName);
        Assert.NotNull(model);
        Assert.False(string.IsNullOrWhiteSpace(model!.Index));
    }

    [Fact]
    public async Task LibraryRegistration_IsIdempotent()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var prepared = await PrepareCardAsync(s, _fixture.NormAdminA.Id);

        await HKApprovalScenario.SubmitDetailsAsync(s, prepared.CardId, _fixture.NormAdminA2.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(s, prepared.CardId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await HKApprovalScenario.SubmitCompositionAsync(s, prepared.CardId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA2.Id);
        await s.Approvals.ApproveCompositionAsync(
            prepared.CardId, null, await HKApprovalScenario.VersionAsync(s, prepared.CardId));

        await using var read = _fixture.CreateScope();
        var aggregatesBefore = await read.Db.Aggregates.CountAsync(a => a.Name == prepared.AggregateName);
        var modelsBefore = await read.Db.EquipmentModels.CountAsync(m => m.Name == prepared.CardName);

        // Повторная регистрация той же принятой редакции не дублирует объекты.
        var card = await read.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == prepared.CardId);
        var revisionId = card.CurrentAcceptedRevisionId!.Value;

        var library = new HKLibraryRegistrationService(read.Db);
        var result = await library.RegisterAcceptedAsync(prepared.CardId, revisionId);
        await read.Db.SaveChangesAsync();

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(aggregatesBefore, await read.Db.Aggregates.CountAsync(a => a.Name == prepared.AggregateName));
        Assert.Equal(modelsBefore, await read.Db.EquipmentModels.CountAsync(m => m.Name == prepared.CardName));
    }
}
