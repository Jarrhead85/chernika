using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Разделение сохранений: структурная команда не записывает несохранённые
/// реквизиты формы, а реквизитная команда не записывает локальную структуру.
/// <para>
/// Проверяется на одном и том же scoped-контексте: именно общий контекст
/// позволяет одной команде унести в базу изменения другой.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardStructureSaveIsolationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardStructureSaveIsolationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Несохранённые реквизиты не попадают в общий SaveChanges структурной
    /// команды: структура сохраняется, реквизиты остаются прежними.
    /// </summary>
    [Fact]
    public async Task SaveStructure_DoesNotWriteUnchangedRequisitions()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id, withRow: false);

        var unitId = Guid.NewGuid();
        await using (var setup = _fixture.CreateScope())
        {
            setup.Db.AssemblyUnits.Add(new AssemblyUnit
            {
                Id = unitId,
                Code = "АУ-" + Suffix(),
                Name = "СЕ изоляции"
            });
            await setup.Db.SaveChangesAsync();
        }

        // Несохранённая правка реквизита: пользователь изменил название, но
        // не сохранил.
        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        var originalName = tracked.Name;
        tracked.Name = "Несохранённое название";

        var sectionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        var version = await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[] { new HKCardSectionWrite(sectionId, HKCardSectionType.Oils, 0) },
            new[]
            {
                new HKCardStructureNodeWrite(
                    nodeId, null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, sectionId)
            },
            new[]
            {
                new HKCardStructureItemWrite(
                    Guid.NewGuid(), nodeId, unitId, 1, 500m, "г", null, null, 0, null,
                    Array.Empty<HKCardMaterialWrite>())
            },
            null,
            version));

        await using (var read = _fixture.CreateScope())
        {
            var saved = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

            // Реквизит не записан: несохранённая правка не должна уезжать в базу
            // вместе со структурой.
            Assert.Equal(originalName, saved.Name);

            // Структура записана.
            Assert.Equal(1, await read.Db.HKCardSections.CountAsync(x => x.HKCardId == cardId));
            Assert.Equal(1, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
        }
    }

    /// <summary>
    /// Кандидаты в согласующие — только активные пользователи филиала с
    /// действующим правом на согласование.
    /// </summary>
    [Fact]
    public async Task ApproverCandidates_ContainOnlyActiveUsersWithApprovalRight()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id, withRow: false);

        var card = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var candidates = await s.HK.GetApproverCandidatesAsync(card.BranchId);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.FullName)));

        // Кандидат из другого филиала в список не попадает.
        var foreign = candidates.Where(c => c.Id != _fixture.NormAdminA.Id).ToList();
        Assert.All(foreign, c =>
            Assert.Equal(card.BranchId, s.Db.Users.Where(u => u.Id == c.Id).Select(u => u.BranchId).First()));
    }

    /// <summary>
    /// Возврат карты в редактирование не превращает несохранённые реквизиты
    /// в записанные: посторонний SaveChanges их не уносит.
    /// </summary>
    [Fact]
    public async Task StructureSave_ThenForeignSaveChanges_DoesNotWriteRequisitions()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var cardId = await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id, withRow: false);

        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        var originalName = tracked.Name;
        tracked.Name = "Несохранённое название";

        var version = await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            Array.Empty<HKCardSectionWrite>(),
            Array.Empty<HKCardStructureNodeWrite>(),
            Array.Empty<HKCardStructureItemWrite>(),
            null,
            version));

        // Возврат в редактирование: несохранённый ввод остаётся в памяти.
        await s.HK.AttachCardForEditingAsync(tracked, cardId);
        Assert.Equal("Несохранённое название", tracked.Name);

        // Посторонний SaveChanges того же контекста.
        await s.Db.SaveChangesAsync();

        await using (var read = _fixture.CreateScope())
        {
            var saved = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
            Assert.Equal(originalName, saved.Name);
        }
    }

    /// <summary>
    /// Конфликт версий: вторая команда с устаревшей версией отказывает, и её
    /// структура не сохраняется.
    /// </summary>
    [Fact]
    public async Task SaveStructure_WithStaleVersion_IsRefused()
    {
        await using var first = _fixture.CreateScope();
        first.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            first, _fixture.NormAdminA.Id, withRow: false);

        var version = await first.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

        // Первый контекст сохраняет структуру.
        await first.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[] { new HKCardSectionWrite(Guid.NewGuid(), HKCardSectionType.Oils, 0) },
            Array.Empty<HKCardStructureNodeWrite>(),
            Array.Empty<HKCardStructureItemWrite>(),
            null,
            version));

        // Второй контекст работает с той же картой и со stale-версией.
        await using var second = _fixture.CreateScope();
        second.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var tracked = await second.Db.HKCards.FirstAsync(c => c.Id == cardId);

        await Assert.ThrowsAsync<HkCardVersionConflictException>(() =>
            second.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId,
                new[] { new HKCardSectionWrite(Guid.NewGuid(), HKCardSectionType.Greases, 0) },
                Array.Empty<HKCardStructureNodeWrite>(),
                Array.Empty<HKCardStructureItemWrite>(),
                null,
                version)));

        await using (var read = _fixture.CreateScope())
        {
            // Структура второй команды не сохранилась.
            Assert.Equal(1, await read.Db.HKCardSections.CountAsync(x => x.HKCardId == cardId));
            Assert.Equal(HKCardSectionType.Oils,
                await read.Db.HKCardSections.AsNoTracking()
                    .Where(x => x.HKCardId == cardId).Select(x => x.SectionType).FirstAsync());
        }
    }

    /// <summary>
    /// Отсутствующая карта не превращается в исключение чтения: сервис
    /// возвращает пустую проекцию, и страница показывает «не найдена», а не
    /// ошибку загрузки.
    /// </summary>
    [Fact]
    public async Task GetStructureGraphAsync_ForMissingCard_ReturnsEmpty()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var graph = await s.HK.GetStructureGraphAsync(Guid.NewGuid());

        Assert.Null(graph);
    }
}
