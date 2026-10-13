using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Решения о допуске к правке принимаются по СОХРАНЁННОМУ состоянию карты.
/// <para>
/// Ключевая опасность: в штатных вызовах входной объект команды — тот же
/// отслеживаемый экземпляр, что вернёт сервис из базы. Если решение читать из
/// него, вызывающий код может подменить статус, филиал, режим или номер до
/// проверки, и проверка пропустит именно то, что должна отклонить.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardPersistedStateTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardPersistedStateTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private async Task<TestScope> AuthorScopeAsync()
    {
        var scope = _fixture.CreateScope();
        scope.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        scope.Permissions.InvalidateCache(_fixture.NormAdminA.Id);
        return scope;
    }

    private static HKCard Draft(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsAutonomous = true,
        ObjectLevel = HKObjectLevel.Undefined,
        Status = HKCardStatus.Draft,
        Purpose = "Проверка сохранённого состояния",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // ── Решения читаются из БД, а не из входного экземпляра ──

    [Fact]
    public async Task UpdateAsync_ForeignBranchInInput_DoesNotBypassBranchBoundary()
    {
        Guid cardId;
        await using (var setup = await AuthorScopeAsync())
        {
            var created = await setup.HK.CreateAsync(Draft("Филиал " + Suffix()));
            created.BranchId = _fixture.BranchA;
            await setup.Db.SaveChangesAsync();
            cardId = created.Id;
        }

        // Отдельный экземпляр с подменённым филиалом: сервис обязан взять
        // филиал из базы, а не из аргумента.
        await using var s = await AuthorScopeAsync();
        var spoofed = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        spoofed.BranchId = _fixture.BranchA;
        spoofed.Purpose = "Подмена филиала";

        await s.HK.UpdateAsync(spoofed);

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal("Подмена филиала", stored.Purpose);
    }

    [Fact]
    public async Task UpdateAsync_ApprovedCard_BlockedEvenWhenInputSaysDraft()
    {
        Guid cardId;
        await using (var setup = await AuthorScopeAsync())
        {
            var created = await setup.HK.CreateAsync(Draft("Утверждена " + Suffix()));
            await setup.Db.SaveChangesAsync();

            var approved = await setup.Db.HKCards.FirstAsync(c => c.Id == created.Id);
            approved.Status = HKCardStatus.Approved;
            await setup.Db.SaveChangesAsync();
            cardId = created.Id;
        }

        // Входной объект утверждает, что карта — черновик. Сохранённый статус
        // обязан победить, и правка должна быть отклонена.
        await using var s = await AuthorScopeAsync();
        var spoofed = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        spoofed.Status = HKCardStatus.Draft;
        spoofed.Purpose = "Правка утверждённой";

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(spoofed));

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.NotEqual("Правка утверждённой", stored.Purpose);
    }

    [Fact]
    public async Task UpdateAsync_InputFieldsForCreationOnly_AreNotApplied()
    {
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(Draft("Неприкосновенные " + Suffix()));
        var numberBefore = created.Code;
        var versionBefore = created.Version;
        var authorBefore = created.AuthorId;
        var levelBefore = created.ObjectLevel;

        // Отдельный экземпляр: имитируем запрос, подменяющий поля, которые
        // принадлежат созданию карты.
        var spoofed = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        spoofed.Code = "ХК-ПОДЛОЖЕННЫЙ-2026";
        spoofed.Version = "v9999";
        spoofed.ObjectLevel = HKObjectLevel.Node;
        spoofed.NodeId = Guid.NewGuid();
        spoofed.Status = HKCardStatus.OnReview;
        spoofed.AuthorId = null;
        spoofed.Purpose = "Разрешённая правка";

        await s.HK.UpdateAsync(spoofed);

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.Equal(numberBefore, stored.Code);
        Assert.Equal(versionBefore, stored.Version);
        Assert.Equal(levelBefore, stored.ObjectLevel);
        Assert.Null(stored.NodeId);
        Assert.Equal(HKCardStatus.Draft, stored.Status);
        Assert.Equal(authorBefore, stored.AuthorId);
        Assert.Equal("Разрешённая правка", stored.Purpose);
    }

    [Fact]
    public async Task UpdateAsync_TrackedEntityMutatedByCaller_CannotBypassStatusCheck()
    {
        Guid cardId;
        await using (var setup = await AuthorScopeAsync())
        {
            var created = await setup.HK.CreateAsync(Draft("Отслеживаемая " + Suffix()));
            await setup.Db.SaveChangesAsync();

            var approved = await setup.Db.HKCards.FirstAsync(c => c.Id == created.Id);
            approved.Status = HKCardStatus.Approved;
            await setup.Db.SaveChangesAsync();
            cardId = created.Id;
        }

        // Штатный сценарий Blazor и API: входной объект — тот же отслеживаемый
        // экземпляр, и вызывающий код уже присвоил ему статус черновика.
        await using var s = await AuthorScopeAsync();
        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        tracked.Status = HKCardStatus.Draft;
        tracked.Purpose = "Обход через отслеживаемый объект";

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(tracked));

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.NotEqual("Обход через отслеживаемый объект", stored.Purpose);
        Assert.Equal(HKCardStatus.Approved, stored.Status);
    }

    [Fact]
    public async Task UpdateAsync_StaleRowVersion_IsRefused()
    {
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(Draft("Версия " + Suffix()));

        var stale = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        stale.RowVersion = created.RowVersion - 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(stale));
    }

    // ── Настоящая конкуренция создания ──

    [Fact]
    public async Task CreateAsync_ParallelSameName_NoDuplicateNumberAndNoAuditForFailedCard()
    {
        // Два независимых scope, то есть два DbContext и две транзакции.
        // Подбор суффикса у обоих читает одно и то же состояние базы, поэтому
        // номер может совпасть. Проверяем поведение при реальном конфликте,
        // а не при смоделированном.
        var name = "Гонка " + Suffix();

        // Экземпляр карты создаётся заранее и передаётся сервису: сервис сам
        // присваивает ему Id, поэтому после отказа известно, какой документ
        // НЕ должен был остаться.
        var attempts = Enumerable.Range(0, 6).Select(_ => new Attempt(Draft(name))).ToList();

        await Task.WhenAll(attempts.Select(async a =>
        {
            await using var scope = await AuthorScopeAsync();
            try
            {
                a.Card = await scope.HK.CreateAsync(a.Instance);
            }
            catch (Exception ex)
            {
                a.Error = ex.Message;
            }
        }));

        var succeeded = attempts.Where(a => a.Card != null).Select(a => a.Card!).ToList();
        var refused = attempts.Where(a => a.Card == null).ToList();

        Assert.NotEmpty(succeeded);
        Assert.NotEmpty(refused);

        // Номер уникален: дубликатов нет ни среди успешных, ни в базе.
        var numbers = succeeded.Select(c => c.Code).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct(StringComparer.Ordinal).Count());

        await using var check = await AuthorScopeAsync();

        var stored = await check.Db.HKCards.AsNoTracking()
            .Where(c => c.Name == name)
            .Select(c => new { c.Id, c.Code })
            .ToListAsync();

        Assert.Equal(succeeded.Count, stored.Count);
        Assert.Equal(stored.Count, stored.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count());

        foreach (var failure in refused)
        {
            // Отказ объясним, а не внутренняя ошибка сервера.
            Assert.False(string.IsNullOrWhiteSpace(failure.Error));

            // Ни частичной карты, ни записи аудита о несозданном документе.
            Assert.False(await check.Db.HKCards.AsNoTracking().AnyAsync(c => c.Id == failure.Instance.Id));
            Assert.False(await check.Db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.EntityId == failure.Instance.Id.ToString()));
        }
    }

    /// <summary>
    /// Одна попытка создания. Экземпляр карты виден и после отказа: сервис
    /// присваивает ему Id до записи, и по нему проверяется, что документ не
    /// остался в базе и не попал в аудит.
    /// </summary>
    private sealed class Attempt(HKCard instance)
    {
        public HKCard Instance { get; } = instance;
        public HKCard? Card { get; set; }
        public string? Error { get; set; }
    }

    [Fact]
    public async Task CreateAsync_AfterConflict_RetryCanTakeFreeNumber()
    {
        var name = "Повтор " + Suffix();

        await using (var first = await AuthorScopeAsync())
        {
            await first.HK.CreateAsync(Draft(name));
        }

        // Повторная попытка после состоявшегося номера обязана получить свободный.
        await using var s = await AuthorScopeAsync();
        var retry = await s.HK.CreateAsync(Draft(name));

        var taken = await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Name == name)
            .Select(c => c.Code)
            .ToListAsync();

        Assert.Equal(2, taken.Count);
        Assert.Contains(retry.Code, taken);
        Assert.Equal(taken.Distinct(StringComparer.Ordinal).Count(), taken.Count);
    }
}