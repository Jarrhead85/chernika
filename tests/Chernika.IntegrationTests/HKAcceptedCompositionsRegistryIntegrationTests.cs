using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Метаданные реестра принятых составов.
/// <para>
/// Реестр показывает и ищет снимковые значения принятой редакции: рабочая
/// карта после принятия могла быть переименована, и подстановка её данных
/// показывала бы не то, что принято.
/// </para>
/// </summary>
[Collection("Database")]
public class HKAcceptedCompositionsRegistryIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKAcceptedCompositionsRegistryIntegrationTests(TestDatabaseFixture fixture) =>
        _fixture = fixture;

    private TestScope As(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    /// <summary>Карта с принятой редакцией состава.</summary>
    private static async Task<Guid> PrepareApprovedCardAsync(
        TestScope s, string actorId, string approverId)
    {
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, actorId, withRow: false);

        var version = await HKApprovalScenario.VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            Array.Empty<HKCardSectionWrite>(),
            new[]
            {
                new HKCardStructureNodeWrite(
                    Guid.NewGuid(), null, HKObjectLevel.Aggregate,
                    "Агрегат насоса " + HKApprovalScenario.Suffix(), 2, 0, null)
            },
            Array.Empty<HKCardStructureItemWrite>(), null, version));

        await HKApprovalScenario.ApplyDerivedLevelAsync(s, cardId);

        await HKApprovalScenario.SubmitDetailsAsync(s, cardId, approverId);

        s.User.CurrentUserId = Guid.Parse(approverId);
        await HKApprovalScenario.ApproveDetailsAsync(s, cardId);

        s.User.CurrentUserId = Guid.Parse(actorId);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        s.User.CurrentUserId = Guid.Parse(approverId);
        await s.Approvals.ApproveCompositionAsync(
            cardId, null, await HKApprovalScenario.VersionAsync(s, cardId));

        return cardId;
    }

    [Fact]
    public async Task Registry_ShowsSnapshotMetadata_NotWorkingCard()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await PrepareApprovedCardAsync(s, _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id);

        await using var read = _fixture.CreateScope();
        read.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var revisionId = card.CurrentAcceptedRevisionId!.Value;
        var revision = await read.Db.HKCardRevisions.AsNoTracking().FirstAsync(r => r.Id == revisionId);

        // Рабочая карта переименована и переверсионирована после принятия.
        var working = await read.Db.HKCards.FirstAsync(c => c.Id == cardId);
        var workingName = "Рабочее имя " + HKApprovalScenario.Suffix();
        working.Name = workingName;
        working.Version = "v9999";
        await read.Db.SaveChangesAsync();

        var registry = await read.Revisions.GetAcceptedCompositionsAsync(
            null, null, 1, 200);

        var row = Assert.Single(registry.Items.Where(i => i.CardId == cardId));

        // В реестре — снимковые значения.
        Assert.Equal(revision.Name, row.Name);
        Assert.Equal(revision.Version, row.Version);
        Assert.Equal(revision.Code, row.Code);
        Assert.Equal(revision.RevisionNumber, row.RevisionNumber);
        Assert.NotEqual(workingName, row.Name);
    }

    [Fact]
    public async Task Registry_SearchesBySnapshotValues()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await PrepareApprovedCardAsync(s, _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id);

        await using var read = _fixture.CreateScope();
        read.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var revision = await read.Db.HKCardRevisions.AsNoTracking()
            .FirstAsync(r => r.Id == card.CurrentAcceptedRevisionId!.Value);

        var working = await read.Db.HKCards.FirstAsync(c => c.Id == cardId);
        var workingName = "Рабочее имя " + HKApprovalScenario.Suffix();
        working.Name = workingName;
        await read.Db.SaveChangesAsync();

        // Поиск по снимковому названию находит карту.
        var bySnapshot = await read.Revisions.GetAcceptedCompositionsAsync(
            revision.Name, null, 1, 200);
        Assert.Contains(bySnapshot.Items, i => i.CardId == cardId);

        // Поиск по рабочему названию карту не находит: реестр показывает и
        // ищет снимковые значения.
        var byWorking = await read.Revisions.GetAcceptedCompositionsAsync(
            workingName, null, 1, 200);
        Assert.DoesNotContain(byWorking.Items, i => i.CardId == cardId);
    }

    [Fact]
    public async Task Registry_DoesNotReplaceMissingAcceptedDateWithAnotherDate()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await PrepareApprovedCardAsync(s, _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id);

        await using var read = _fixture.CreateScope();
        read.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var revisionId = card.CurrentAcceptedRevisionId!.Value;

        // Дата принятия не зафиксирована. Снимок неизменяем, поэтому правка
        // выполняется прямым SQL: прикладная защита снимков его не пропускает.
        await read.Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"HKCardRevisions\" SET \"AcceptedAtUtc\" = NULL WHERE \"Id\" = {0}",
            revisionId);

        var registry = await read.Revisions.GetAcceptedCompositionsAsync(null, null, 1, 200);
        var row = Assert.Single(registry.Items.Where(i => i.CardId == cardId));

        // Дата не подменяется датой создания или фиксации снимка.
        Assert.Null(row.AcceptedAtUtc);
    }

    [Fact]
    public async Task Registry_MissingSnapshot_IsIntegrityError()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await PrepareApprovedCardAsync(s, _fixture.NormAdminA.Id, _fixture.NormAdminA2.Id);

        await using var read = _fixture.CreateScope();
        read.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var revisionId = card.CurrentAcceptedRevisionId!.Value;

        // Снимок удалён, а указатель остался: это нарушение целостности.
        // Ссылочная целостность на время сценария отключается: иначе база
        // просто не даст создать такое состояние. Сценарий выполняется в
        // транзакции с откатом: состояние базы после него не меняется.
        await using var tx = await read.Db.Database.BeginTransactionAsync();

        await read.Db.Database.ExecuteSqlRawAsync(
            "SET LOCAL session_replication_role = replica");

        await read.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"HKCardRevisionItemMaterials\" WHERE \"RevisionItemId\" IN "
            + "(SELECT \"Id\" FROM \"HKCardRevisionItems\" WHERE \"RevisionId\" = {0})",
            revisionId);
        await read.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"HKCardRevisionItems\" WHERE \"RevisionId\" = {0}", revisionId);
        await read.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"HKCardRevisionNodes\" WHERE \"RevisionId\" = {0}", revisionId);
        await read.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"HKCardRevisionSections\" WHERE \"RevisionId\" = {0}", revisionId);
        await read.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"HKCardRevisions\" WHERE \"Id\" = {0}", revisionId);

        // Отсутствующий снимок при заполненном указателе — нарушение
        // целостности, а не «редакция № 0» с рабочими данными.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            read.Revisions.GetAcceptedCompositionsAsync(null, null, 1, 200));

        await tx.RollbackAsync();
    }
}
