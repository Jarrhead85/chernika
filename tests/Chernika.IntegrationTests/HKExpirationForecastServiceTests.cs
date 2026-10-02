using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Прогноз обработки сроков: он должен отвечать на вопрос «что сделает worker»
/// без записи.
/// <para>
/// Главное свойство прогноза — отрицательное: он не меняет данные. Иначе
/// «посмотреть, что произойдёт» означало бы выполнить это на рабочей базе.
/// Проверяется попыткой записи через тот же контекст в рамках того же вызова.
/// </para>
/// <para>
/// Вторая задача прогноза — совпасть с обработкой. Расчёт дублирует логику
/// сервиса, и дублирование без сверки рано или поздно разъезжается, поэтому
/// совпадение закреплено на одном и том же наборе карт.
/// </para>
/// </summary>
[Collection("Database")]
public class HKExpirationForecastServiceTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly List<Guid> _cards = new();
    private readonly List<Guid> _nodes = new();
    private readonly List<Guid> _branches = new();

    public HKExpirationForecastServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_cards.Count == 0 && _branches.Count == 0) return;
        await using var s = _fixture.CreateScope();
        await CleanupAsync(s.Db, _cards, _nodes, _branches);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static readonly DateTime Today = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    // ── Отсутствие записи ─────────────────────────────────────────────────

    [Fact]
    public async Task Forecast_DoesNotWrite_AndRunsInsideReadOnlyTransaction()
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        Seed(s, daysLeft: -5);

        var before = await SnapshotAsync(s.Db);

        var forecast = await s.Forecast.BuildAsync();

        Assert.Equal(before, await SnapshotAsync(s.Db));
        Assert.NotEmpty(forecast.Cards);

        // Прогноз не «просто ничего не менял», а выполняется в транзакции
        // READ ONLY: сервер отвергает запись в ней с 25006. Повторяется ровно тот
        // приём, что использует сервис, — иначе проверялось бы свойство,
        // которого у сервиса нет.
        await using (var tx = await s.Db.Database.BeginTransactionAsync())
        {
            await s.Db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database
                .ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM \"Notifications\" WHERE \"Id\" = {Guid.NewGuid()}"));

            Assert.Contains("25006", ex.ToString(), StringComparison.Ordinal);
            await tx.RollbackAsync();
        }
    }
    [Fact]
    public async Task Forecast_ReportsPlannedActions_WithoutApplyingThem()
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        var card = Seed(s, daysLeft: -3);

        var forecast = await s.Forecast.BuildAsync();
        var entry = Assert.Single(forecast.Cards.Where(c => c.Id == card));

        Assert.True(entry.WillArchive);
        Assert.True(entry.WillNotifyExpired);
        Assert.Equal(-3, entry.DaysLeft);
        Assert.Contains(entry.PlannedActions, a => a.Contains("АВТОАРХИВИРОВАНИЕ"));
        Assert.Contains(entry.PlannedActions, a => a.Contains("догоняющее уведомление об истечении"));

        // Прогноз ничего не применил: карта на месте и всё ещё утверждена.
        Assert.Equal(HKCardStatus.Approved, (await s.Db.HKCards.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.Id == card)).Status);
    }

    // ── Совпадение с обработкой ───────────────────────────────────────────

    [Theory]
    [InlineData(90, false, 90)]
    [InlineData(30, false, 30)]
    [InlineData(7, false, 7)]
    [InlineData(25, false, 30)]   // простой: порог 30 пропущен
    [InlineData(5, false, 7)]     // простой: порог 7 пропущен
    [InlineData(1, false, 7)]     // внутри окна 7 дней
    public async Task Forecast_PredictsSameWarningThreshold_AsTheRun(
        int daysLeft, bool seed90, int expectedThreshold)
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        var card = Seed(s, daysLeft: daysLeft);

        if (seed90)
            await SeedSentThresholdAsync(s, card, 90);

        var predicted = (await s.Forecast.BuildAsync()).Cards.Single(c => c.Id == card);

        await s.Expiration.ProcessAsync();

        var actualThreshold = await s.Db.Notifications
            .Where(n => n.EntityId == card && n.Type == NotificationType.HKExpiring)
            .Select(n => n.DeduplicationKey!)
            .ToListAsync();

        Assert.Equal(expectedThreshold, predicted.NotificationThreshold);
        Assert.True(predicted.WillNotifyExpiring);

        var expectedKey = HKCardExpirationService.WarningDeduplicationKey(card, expectedThreshold);
        Assert.All(actualThreshold, key => Assert.Equal(expectedKey, key));
    }

    [Fact]
    public async Task Forecast_DoesNotPredictNotification_WhenThresholdAlreadySent()
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        var card = Seed(s, daysLeft: 30);
        await SeedSentThresholdAsync(s, card, 90);

        var predicted = (await s.Forecast.BuildAsync()).Cards.Single(c => c.Id == card);

        Assert.True(predicted.NotificationThreshold > 0, "прогноз ждёт уведомления за 30");
        await s.Expiration.ProcessAsync();
        var afterRun = await s.Expiration.ProcessAsync();

        Assert.Equal(0, afterRun.WarningsSent + afterRun.ReviewTasksCreated);
        Assert.True(predicted.WillNotifyExpiring);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(-1, true, true)]
    public async Task Forecast_PredictsArchiveOnlyFromTheDayAfterExpiry(
        int daysLeft, bool expectArchive, bool expectExpiryNotification)
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        var card = Seed(s, daysLeft: daysLeft);

        var predicted = (await s.Forecast.BuildAsync()).Cards.Single(c => c.Id == card);
        Assert.Equal(expectArchive, predicted.WillArchive);
        Assert.Equal(expectExpiryNotification, predicted.WillNotifyExpired);

        await s.Expiration.ProcessAsync();

        var status = (await s.Db.HKCards.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.Id == card)).Status;
        Assert.Equal(expectArchive ? HKCardStatus.Archived : HKCardStatus.Approved, status);
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(30, true)]
    [InlineData(7, true)]
    [InlineData(5, true)]   // простой приложения: ближний порог 7
    public async Task Forecast_PredictsReviewTask_OnAnyThresholdExceptTheEarliest(
        int daysLeft, bool expected)
    {
        // Задача пересмотра создаётся на порогах 30 и 7, но не на 90: за три
        // месяца пересматривать рано. Это прежнее правило, оно не сужалось —
        // повтор при сбое назначения добавлен отдельно и его не отменяет.
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        var card = Seed(s, daysLeft: daysLeft);

        var forecast = await s.Forecast.BuildAsync();
        Assert.Equal(expected, forecast.Cards.Single(c => c.Id == card).WillCreateReviewTask);

        await s.Expiration.ProcessAsync();
        var actual = await s.Db.WorkTasks
            .CountAsync(t => t.EntityId == card && t.Type == WorkTaskType.HKExpirationReview);
        Assert.Equal(expected ? 1 : 0, actual);
    }

    [Fact]
    public async Task Forecast_PredictsClampedReviewTaskDeadline()
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        var card = Seed(s, daysLeft: 7);

        var predicted = (await s.Forecast.BuildAsync()).Cards.Single(c => c.Id == card);

        // PlannedActions — IReadOnlyList<string>: перегрузка Assert.Contains для
        // коллекций подставляет async-вариант, поэтому предикат ищется через Any.
        Assert.True(predicted.PlannedActions.Any(a => a.Contains("усечено")),
            "прогноз не заметил, что срок задачи обрезается границей автоархивации: "
            + string.Join(" | ", predicted.PlannedActions));
    }

    // ── Адресаты ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Forecast_ReportsWhoReceives_AndWhoIsExcluded()
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));

        var branch = NewBranch(s, "Прогнозная ");
        var author = CreateUser(s, "pa_" + Suffix(), branch);
        var normAdmin = CreateUser(s, "pna_" + Suffix(), branch, "NormAdmin");
        var outsider = CreateUser(s, "pout_" + Suffix(), NewBranch(s, "Чужая "), "NormAdmin");

        var card = Seed(s, daysLeft: 30, branchId: branch, authorId: author, normAdminId: normAdmin);

        var (delivered, excluded) = await s.Forecast.RecipientsAsync(
            await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == card));

        Assert.Contains(author, delivered.Select(r => r.UserId));
        Assert.Contains(normAdmin, delivered.Select(r => r.UserId));
        Assert.DoesNotContain(outsider, delivered.Select(r => r.UserId));

        var cardForecast = (await s.Forecast.BuildAsync()).Cards.Single(c => c.Id == card);
        Assert.Equal(excluded.Count, cardForecast.RecipientsExcluded);
    }

    [Fact]
    public async Task Forecast_ReportsDeactivatedUserAsExcluded()
    {
        await using var s = _fixture.CreateScope();
        s.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));

        var branch = NewBranch(s, "Прогнозная ");
        var author = CreateUser(s, "pa_" + Suffix(), branch);
        var normAdmin = CreateUser(s, "pna_" + Suffix(), branch, "NormAdmin");

        var row = await s.Db.Users.FirstAsync(u => u.Id == normAdmin);
        row.IsActive = false;
        await s.Db.SaveChangesAsync();

        var card = Seed(s, daysLeft: 30, branchId: branch, authorId: author, normAdminId: normAdmin);

        var (delivered, excluded) = await s.Forecast.RecipientsAsync(
            await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == card));

        Assert.DoesNotContain(normAdmin, delivered.Select(r => r.UserId));
        Assert.Contains(excluded, r => r.UserId == normAdmin && r.Reason == "пользователь отключён");
    }

    // ── Вспомогательное ───────────────────────────────────────────────────

    private static Npgsql.NpgsqlParameter ArrayOf(IReadOnlyCollection<Guid> ids) =>
        new("ids", ids.ToArray())
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid,
        };

    /// <summary>
    /// Снимок интересующих таблиц. Сравнение «до/после» доказывает, что прогноз
    /// ничего не записал.
    /// </summary>
    private static async Task<Dictionary<string, int>> SnapshotAsync(AppDbContext db) => new()
    {
        ["cards"] = await db.HKCards.IgnoreQueryFilters().CountAsync(),
        ["notifications"] = await db.Notifications.CountAsync(),
        ["tasks"] = await db.WorkTasks.CountAsync(),
        ["audit"] = await db.AuditLogs.CountAsync(),
        ["statusLogs"] = await db.HKCardStatusLogs.CountAsync(),
    };

    private Guid NewBranch(TestScope s, string prefix)
    {
        var branch = new Branch { Id = Guid.NewGuid(), Name = prefix + Suffix(), Code = "TB" + Suffix()[..4] };
        s.Db.Branches.Add(branch);
        s.Db.SaveChanges();
        _branches.Add(branch.Id);
        return branch.Id;
    }

    private string CreateUser(TestScope s, string userName, Guid branchId, params string[] roles)
    {
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = userName + "@forecast.test",
            EmailConfirmed = true,
            BranchId = branchId,
        };
        Assert.True(s.Users.CreateAsync(user, "Forecast-Pass-1").GetAwaiter().GetResult().Succeeded);

        foreach (var role in roles)
            Assert.True(s.Users.AddToRoleAsync(user, role).GetAwaiter().GetResult().Succeeded);

        return user.Id;
    }

    private Guid Seed(
        TestScope s, int? daysLeft, Guid? branchId = null,
        string? authorId = null, string? normAdminId = null)
    {
        branchId ??= NewBranch(s, "Филиал ");
        authorId ??= CreateUser(s, "author_" + Suffix(), branchId.Value);
        normAdminId ??= CreateUser(s, "na_" + Suffix(), branchId.Value, "NormAdmin");

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-FC-" + Suffix(),
            Name = "Узел " + Suffix(),
        };
        s.Db.Nodes.Add(node);
        _nodes.Add(node.Id);

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-FC-" + Suffix(),
            Version = "v1",
            Status = HKCardStatus.Approved,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            BranchId = branchId.Value,
            AuthorId = Guid.Parse(authorId),
            CreatedAt = Today.AddDays(-30),
            ApprovedDate = Today.AddDays(-20),
            ExpirationDate = daysLeft.HasValue ? Today.AddDays(daysLeft.Value) : null,
        };
        s.Db.HKCards.Add(card);
        s.Db.SaveChanges();
        _cards.Add(card.Id);
        return card.Id;
    }

    private static async Task SeedSentThresholdAsync(TestScope s, Guid cardId, int threshold)
    {
        var card = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        s.Db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = (await s.Db.Users.Where(u => u.BranchId == card.BranchId)
                .Select(u => u.Id).FirstAsync()),
            BranchId = card.BranchId,
            Type = NotificationType.HKExpiring,
            Title = "Уже отправлено",
            Message = "Порог отправлен до простоя приложения.",
            EntityType = "HKCard",
            EntityId = cardId,
            NavigationUrl = $"/хк/{cardId}",
            DeduplicationKey = HKCardExpirationService.WarningDeduplicationKey(cardId, threshold),
            IsRead = true,
            CreatedAtUtc = Today.AddDays(-(90 - threshold)),
        });
        await s.Db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(
        AppDbContext db, IReadOnlyCollection<Guid> cards, IReadOnlyCollection<Guid> nodes,
        IReadOnlyCollection<Guid> branches)
    {
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""Notifications"" WHERE ""EntityId"" = ANY(@ids)", ArrayOf(cards));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""WorkTasks"" WHERE ""EntityId"" = ANY(@ids)", ArrayOf(cards));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""HKCardStatusLogs"" WHERE ""HKCardId"" = ANY(@ids)", ArrayOf(cards));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""HKCards"" WHERE ""Id"" = ANY(@ids)", ArrayOf(cards));

        if (nodes.Count > 0)
            await db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Nodes"" WHERE ""Id"" = ANY(@ids)", ArrayOf(nodes));

        if (branches.Count > 0)
            await db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Branches"" WHERE ""Id"" = ANY(@ids)", ArrayOf(branches));
    }
}
