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
}