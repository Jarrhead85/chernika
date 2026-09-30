using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Chernika.IntegrationTests;

/// <summary>
/// Гонка «создание связи ↔ soft-delete марки» (PR-4, правило 9).
/// <para>
/// Инвариант: не должны оба завершиться успешно, оставив АКТИВНУЮ связь с
/// удалённой маркой. Допустимы только два исхода: связь создана и обе марки
/// активны, либо марка удалена и активной связи с ней нет.
/// </para>
/// <para>
/// Защита обеспечивается единым порядком блокировок строк марок по возрастанию
/// Guid в обоих путях плюс повторной проверкой состояния под блокировкой. Два
/// независимых <c>AnyAsync</c> без блокировки такой инвариант не дают.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmRelationConcurrencyIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;
    private readonly ITestOutputHelper _out;

    public GsmRelationConcurrencyIntegrationTests(
        TestDatabaseFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _out = output;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    [Fact]
    public async Task CreateRelation_And_DeleteMaterial_InParallel_NeverLeaveActiveRelationWithDeletedMaterial()
    {
        // Несколько пар на случай, если поведение зависит от порядка блокировок.
        var outcomes = new List<string>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var a = await CreateMaterialAsync("Гонка A " + Suffix());
            var b = await CreateMaterialAsync("Гонка B " + Suffix());

            // Два независимых scope = два DbContext = два соединения, как в
            // двух параллельных HTTP-запроса.
            await using var creatorScope = _fixture.CreateScope();
            await using var deleterScope = _fixture.CreateScope();
            AsNormAdmin(creatorScope);
            AsNormAdmin(deleterScope);

            using var gate = new SemaphoreSlim(0, 2);
            var started = new CountdownEvent(2);

            var createTask = Task.Run(async () =>
            {
                started.Signal();
                await gate.WaitAsync();
                try
                {
                    await creatorScope.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
                    {
                        PrimaryGsmMaterialId = a,
                        RelatedGsmMaterialId = b,
                        RelationType = GsmRelationType.Duplicate,
                    });
                    return "created";
                }
                catch (Exception ex) { return "create-failed:" + ex.GetType().Name; }
            });

            var deleteTask = Task.Run(async () =>
            {
                started.Signal();
                await gate.WaitAsync();
                try
                {
                    var ok = await deleterScope.GsmMaterials.DeleteAsync(a);
                    return ok ? "deleted" : "delete-noop";
                }
                catch (Exception ex) { return "delete-failed:" + ex.GetType().Name; }
            });

            started.Wait();
            gate.Release(2);
            var results = await Task.WhenAll(createTask, deleteTask);
            outcomes.Add(string.Join(" / ", results));

            await AssertInvariantAsync(a, b, attempt);
        }

        foreach (var o in outcomes) _out.WriteLine("исход: " + o);
    }

    /// <summary>
    /// Проверяет инвариант независимо от исхода: активной связи с
    /// soft-deleted маркой не осталось.
    /// </summary>
    private async Task AssertInvariantAsync(Guid primaryId, Guid relatedId, int attempt)
    {
        await using var s = _fixture.CreateScope();

        var relationActive = await s.Db.GsmMaterialRelations
            .AsNoTracking()
            .AnyAsync(r => !r.IsDeleted
                && r.PrimaryGsmMaterialId == primaryId
                && r.RelatedGsmMaterialId == relatedId);

        if (!relationActive)
        {
            _out.WriteLine($"попытка {attempt}: активной связи нет — инвариант соблюдён");
            return;
        }

        var deletedCount = await s.Db.GsmMaterials
            .IgnoreQueryFilters()
            .Where(m => (m.Id == primaryId || m.Id == relatedId) && m.IsDeleted)
            .CountAsync();

        Assert.True(deletedCount == 0,
            $"попытка {attempt}: осталась активная связь, а удалено марок: {deletedCount}");
        _out.WriteLine($"попытка {attempt}: активная связь, обе марки активны");
    }

    [Fact]
    public async Task ConcurrentCreationOfSamePair_LeavesAtMostOneActiveRelation()
    {
        var a = await CreateMaterialAsync("Пара A " + Suffix());
        var b = await CreateMaterialAsync("Пара B " + Suffix());

        await using var scopeA = _fixture.CreateScope();
        await using var scopeB = _fixture.CreateScope();
        AsNormAdmin(scopeA);
        AsNormAdmin(scopeB);

        using var gate = new SemaphoreSlim(0, 2);
        var started = new CountdownEvent(2);

        async Task<string> Create(TestScope s)
        {
            started.Signal();
            await gate.WaitAsync();
            try
            {
                await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
                {
                    PrimaryGsmMaterialId = a,
                    RelatedGsmMaterialId = b,
                    RelationType = GsmRelationType.Duplicate,
                });
                return "created";
            }
            catch (Exception ex) { return "failed:" + ex.GetType().Name; }
        }

        var t1 = Task.Run(() => Create(scopeA));
        var t2 = Task.Run(() => Create(scopeB));
        started.Wait();
        gate.Release(2);
        var results = await Task.WhenAll(t1, t2);

        _out.WriteLine("исходы: " + string.Join(" / ", results));
        Assert.Contains(results, r => r == "created");

        await using var check = _fixture.CreateScope();
        var active = await check.Db.GsmMaterialRelations
            .AsNoTracking()
            .CountAsync(r => !r.IsDeleted
                && r.PrimaryGsmMaterialId == a
                && r.RelatedGsmMaterialId == b);

        Assert.Equal(1, active);
    }

    private async Task<Guid> CreateMaterialAsync(string name)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name,
            GroupName = "Группа " + Suffix(),
            SubgroupNames = new List<string> { "Подгруппа" },
        });
        return view.Id;
    }
}
