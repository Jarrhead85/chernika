using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Границы обработки сроков действия ХК.
/// <para>
/// Все проверки дат идут через управляемый <see cref="TimeProvider"/> фикстуры.
/// Без этого «день до срока», «день срока» и «день после» зависели бы от часа
/// запуска: набор, зелёный утром, падал бы вечером.
/// </para>
/// <para>
/// <b>Общая фикстура содержит чужие утверждённые карты с датами.</b> Обработка
/// по определению проходит по всей базе — так и должно быть в бою, — поэтому
/// счётчики результата не являются точными числами. Все утверждения сделаны по
/// состоянию СВОИХ карт в базе; счётчики результата проверяются только там, где
/// утверждение о них относительно (например, «упала ровно одна»).
/// </para>
/// <para>
/// Каждый тест убирает за собой только свои Guid, поэтому порядок выполнения на
/// результат не влияет.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardExpirationBoundaryTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly List<Guid> _cards = new();
    private readonly List<Guid> _nodes = new();
    private readonly List<Guid> _models = new();
    private readonly List<Guid> _branches = new();

    public HKCardExpirationBoundaryTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_cards.Count == 0 && _nodes.Count == 0 && _models.Count == 0 && _branches.Count == 0) return;
        await using var s = _fixture.CreateScope();
        await CleanupAsync(s.Db, _cards, _nodes, _models, _branches);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Опорный UTC-день: фиксирован в коде, тесты не зависят от даты запуска.</summary>
    private static readonly DateTime Today = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    private TestScope NewScope()
    {
        var scope = _fixture.CreateScope();
        scope.Clock.SetUtcNow(new DateTimeOffset(Today, TimeSpan.Zero));
        return scope;
    }

    // ── Сценарий ──────────────────────────────────────────────────────────

    private sealed class Seeded
    {
        public required HKCard Card { get; init; }
        public required TestScope Scope { get; init; }
        public required Guid BranchId { get; init; }
        public string NormAdminId { get; init; } = string.Empty;
        public string AuthorId { get; init; } = string.Empty;

        public Guid CardId => Card.Id;
        public string CardCode => Card.Code;
        public TestTimeProvider Clock => Scope.Clock;
        public AppDbContext Db => Scope.Db;
        public HKCardExpirationService Expiration => Scope.Expiration;
        public HKCardService HK => Scope.HK;
        public TaskService Tasks => Scope.Tasks;
        public NotificationService Notifications => Scope.Notifications;
        public UserManager<ApplicationUser> Users => Scope.Users;
    }

    /// <summary>
    /// Утверждённая карта с заданным остатком срока.
    /// <paramref name="branchId"/> по умолчанию — отдельная ветка теста: в общей
    /// ветке фикстуры есть NormAdmin, и проверки «адресата нет» были бы ложными.
    /// </summary>
    private Seeded Seed(
        TestScope s, int? daysLeft,
        Guid? branchId = null,
        HKCardStatus status = HKCardStatus.Approved,
        bool withNormAdmin = true,
        bool deactivateNormAdmin = false,
        bool secondCard = false)
    {
        var branch = branchId ?? NewBranch(s, withNormAdmin ? "Филиал " : "Филиал без NormAdmin ");
        var author = CreateUser(s, (secondCard ? "author2_" : "author_") + Suffix(), branch);
        var normAdmin = withNormAdmin ? CreateUser(s, "na_" + Suffix(), branch, "NormAdmin") : string.Empty;

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = (secondCard ? "HK-EXP2-" : "HK-EXP-") + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = status,
            ObjectLevel = HKObjectLevel.Node,
            // Уровень без объекта нарушает CK_HKCards_ExactlyOneObject.
            NodeId = CreateNode(s),
            BranchId = branch,
            AuthorId = Guid.Parse(author),
            CreatedAt = Today.AddDays(-30),
            ApprovedDate = Today.AddDays(-20),
            ExpirationDate = daysLeft.HasValue ? Today.AddDays(daysLeft.Value) : null,
        };
        s.Db.HKCards.Add(card);
        s.Db.SaveChanges();
        _cards.Add(card.Id);

        if (withNormAdmin && deactivateNormAdmin)
        {
            var row = s.Db.Users.First(u => u.Id == normAdmin);
            row.IsActive = false;
            s.Db.SaveChanges();
        }

        return new Seeded
        {
            Card = card,
            Scope = s,
            BranchId = branch,
            NormAdminId = normAdmin,
            AuthorId = author,
        };
    }

    private Guid NewBranch(TestScope s, string prefix)
    {
        var branch = new Branch { Id = Guid.NewGuid(), Name = prefix + Suffix(), Code = "TB" + Suffix()[..4] };
        s.Db.Branches.Add(branch);
        s.Db.SaveChanges();
        _branches.Add(branch.Id);
        return branch.Id;
    }

    private Guid CreateNode(TestScope s)
    {
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-EXP-" + Suffix(),
            Name = "Узел " + Suffix(),
        };
        s.Db.Nodes.Add(node);
        _nodes.Add(node.Id);
        return node.Id;
    }

    private Guid CreateEquipmentModel(TestScope s)
    {
        var model = new EquipmentModel
        {
            Id = Guid.NewGuid(),
            Index = "EM-EXP-" + Suffix(),
            Name = "Изделие " + Suffix(),
        };
        s.Db.EquipmentModels.Add(model);
        _models.Add(model.Id);
        return model.Id;
    }

    private string CreateUser(TestScope s, string userName, Guid branchId, params string[] roles)
    {
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = userName + "@exp.test",
            EmailConfirmed = true,
            BranchId = branchId,
        };
        var created = s.Users.CreateAsync(user, "Exp-Test-Pass-1").GetAwaiter().GetResult();
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        foreach (var role in roles)
        {
            var added = s.Users.AddToRoleAsync(user, role).GetAwaiter().GetResult();
            Assert.True(added.Succeeded, "роль не выдана: " + role);
        }

        return user.Id;
    }

    // ── Пороговые события ─────────────────────────────────────────────────

    [Theory]
    [InlineData(90)]
    [InlineData(30)]
    [InlineData(7)]
    public async Task Warning_IsSentOnThresholdDay(int threshold)
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: threshold);

        var result = await seeded.Expiration.ProcessAsync();

        Assert.True(result.AcquiredLock, "обработка не получила advisory lock");
        Assert.Equal(0, result.CardsArchived);

        // Уведомление получают оба адресата карты: автор и NormAdmin её ветки.
        // Ключ дедупликации общий, но уникальный индекс стоит на паре
        // (UserId, DeduplicationKey), поэтому по одному ключу на карту
        // приходится столько уведомлений, сколько адресатов.
        var notifications = await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n =>
            Assert.Equal(HKCardExpirationService.WarningDeduplicationKey(seeded.CardId, threshold), n.DeduplicationKey));
        Assert.Equal(
            new[] { seeded.AuthorId, seeded.NormAdminId }.OrderBy(x => x),
            notifications.Select(n => n.UserId).OrderBy(x => x));
    }

    [Fact]
    public async Task DayOfExpiration_NotifiesButDoesNotArchive()
    {
        // Явное правило: карта действует В ТЕЧЕНИЕ дня своего ExpirationDate.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 0);

        await seeded.Expiration.ProcessAsync();

        Assert.Equal(HKCardStatus.Approved, await StatusAsync(seeded));
        Assert.Equal(2, await s.Db.Notifications
            .CountAsync(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpired));
    }

    [Fact]
    public async Task DayAfterExpiration_Archives_AndKeepsHistory()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: -1);

        await seeded.Expiration.ProcessAsync();

        Assert.Equal(HKCardStatus.Archived, await StatusAsync(seeded));

        var log = Assert.Single(await s.Db.HKCardStatusLogs
            .Where(l => l.HKCardId == seeded.CardId && l.ToStatus == HKCardStatus.Archived)
            .ToListAsync());
        Assert.Equal(HKCardStatus.Approved, log.FromStatus);

        Assert.True(await s.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "HKCard" && a.EntityId == seeded.CardId.ToString()
            && a.Action == "HK.ExpiredArchived"));
    }

    [Fact]
    public async Task DayBeforeExpiration_DoesNothing()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 1);

        // Все пороги уже отправлены — иначе внутри окна 7 дней сервис правомерно
        // прислал бы догоняющее уведомление, и проверка была бы не про то.
        foreach (var threshold in new[] { 90, 30, 7 })
            await SeedSentThresholdAsync(s, seeded, threshold);

        // Считается ДО прогона: посеянные уведомления тоже имеют тип HKExpiring,
        // и утверждение «уведомлений нет» без этой поправки проверяло бы, что
        // тест ничего не посеял.
        var before = await s.Db.Notifications
            .CountAsync(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring);

        await seeded.Expiration.ProcessAsync();

        // Новых уведомлений нет: ближний порог 7 уже отправлен, а искать более
        // далёкий (30) сервис не станет — это и есть защита от лавины.
        Assert.Equal(HKCardStatus.Approved, await StatusAsync(seeded));
        Assert.Equal(before, await s.Db.Notifications
            .CountAsync(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring));

        // Задача пересмотра, наоборот, создаётся: карта внутри окна 7 дней.
        Assert.Single(await s.Db.WorkTasks
            .Where(t => t.EntityId == seeded.CardId && t.Type == WorkTaskType.HKExpirationReview)
            .ToListAsync());
    }

    // ── Идемпотентность ───────────────────────────────────────────────────

    [Fact]
    public async Task SecondRun_SameDay_CreatesNothingNew()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 7);

        await seeded.Expiration.ProcessAsync();
        var firstCount = await s.Db.Notifications
            .CountAsync(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring);
        var firstAudit = await s.Db.AuditLogs
            .CountAsync(a => a.Action == "HK.ExpirationWarningCreated"
                && a.EntityId == seeded.CardId.ToString());

        await seeded.Expiration.ProcessAsync();

        Assert.Equal(2, firstCount);
        Assert.Equal(1, firstAudit);
        Assert.Equal(firstCount, await s.Db.Notifications
            .CountAsync(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring));
        Assert.Equal(1, await s.Db.WorkTasks
            .CountAsync(t => t.EntityId == seeded.CardId && t.Type == WorkTaskType.HKExpirationReview));
        Assert.Equal(firstAudit, await s.Db.AuditLogs
            .CountAsync(a => a.Action == "HK.ExpirationWarningCreated"
                && a.EntityId == seeded.CardId.ToString()));
    }

    [Fact]
    public async Task RepeatedRun_AfterExpiration_DoesNotArchiveTwice()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: -3);

        await seeded.Expiration.ProcessAsync();
        await seeded.Expiration.ProcessAsync();

        Assert.Equal(HKCardStatus.Archived, await StatusAsync(seeded));
        Assert.Equal(1, await s.Db.HKCardStatusLogs
            .CountAsync(l => l.HKCardId == seeded.CardId && l.ToStatus == HKCardStatus.Archived));
        Assert.Equal(1, await s.Db.AuditLogs
            .CountAsync(a => a.EntityId == seeded.CardId.ToString() && a.Action == "HK.ExpiredArchived"));
        Assert.Equal(2, await s.Db.Notifications
            .CountAsync(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpired));
    }

    [Fact]
    public async Task ProcessAsync_SkipsRun_WhenLockHeldByAnotherInstance()
    {
        // Два экземпляра приложения на одной базе. Блокировку здесь держит
        // постороннее соединение, поэтому проверяется именно запрет второго
        // прогона, а не то, что два вызова случайно не совпали по времени.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: -1);

        await using var competitor = new Npgsql.NpgsqlConnection(
            TestDatabase.For(TestDatabase.IntegrationDbName));
        await competitor.OpenAsync();

        await using (var acquire = competitor.CreateCommand())
        {
            acquire.CommandText = "SELECT pg_advisory_lock(@key)";
            acquire.Parameters.Add(new Npgsql.NpgsqlParameter(
                "key", HKCardExpirationService.AdvisoryLockKey));
            await acquire.ExecuteNonQueryAsync();
        }

        var result = await seeded.Expiration.ProcessAsync();

        Assert.False(result.AcquiredLock, "прогон выполнился, хотя блокировку держал другой экземпляр");
        Assert.Equal(0, result.CardsProcessed);
        Assert.Equal(HKCardStatus.Approved, await StatusAsync(seeded));
    }

    [Fact]
    public async Task ProcessAsync_AcquiresLock_WhenNobodyHoldsIt()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: -1);

        var result = await seeded.Expiration.ProcessAsync();

        Assert.True(result.AcquiredLock, "прогон не получил блокировку и ничего не обработал");
        Assert.Equal(HKCardStatus.Archived, await StatusAsync(seeded));
    }
    [Fact]
    public async Task MissedThreshold_CatchesUp_WithExactlyOneNotification()
    {
        // Приложение простояло: порог 30 пропущен, осталось 25 дней.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 25);

        await seeded.Expiration.ProcessAsync();

        var notifications = await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n =>
            Assert.Equal(HKCardExpirationService.WarningDeduplicationKey(seeded.CardId, 30), n.DeduplicationKey));
    }

    [Fact]
    public async Task CatchUp_PicksNearestUnsentThreshold_NotEveryMissedOne()
    {
        // Порог 90 ушёл вовремя, 30 и 7 пропущены. При 5 днях остатка подходящие
        // незакрытые пороги — 30 и 7; отправляется один, ближайший, а не оба.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 5);
        await SeedSentThresholdAsync(s, seeded, 90);

        await seeded.Expiration.ProcessAsync();

        var keys = (await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring)
            .Select(n => n.DeduplicationKey!)
            .ToListAsync()).Distinct().ToList();

        Assert.Equal(2, keys.Count);
        Assert.Contains(HKCardExpirationService.WarningDeduplicationKey(seeded.CardId, 90), keys);
        Assert.Contains(HKCardExpirationService.WarningDeduplicationKey(seeded.CardId, 7), keys);
        Assert.DoesNotContain(HKCardExpirationService.WarningDeduplicationKey(seeded.CardId, 30), keys);
    }

    [Fact]
    public async Task StartupAfterExpiration_SendsMissedExpiryNotification_ThenArchives()
    {
        // Сценарий «первого запуска»: карта истекла, уведомление не отправлялось.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: -2);

        await seeded.Expiration.ProcessAsync();

        var notifications = await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpired)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n =>
            Assert.Equal(HKCardExpirationService.ExpiredDeduplicationKey(seeded.CardId), n.DeduplicationKey));
        Assert.Equal(HKCardStatus.Archived, await StatusAsync(seeded));
    }

    [Fact]
    public async Task InsideWindow_WithoutSentThresholds_GetsCatchUpNotification()
    {
        // День до срока, но порог 7 не отправлен (приложение простояло). Карта
        // внутри окна 7 дней — догоняющее уведомление правомерно и обязательно.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 1);

        await seeded.Expiration.ProcessAsync();

        var keys = (await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring)
            .Select(n => n.DeduplicationKey!)
            .ToListAsync()).Distinct().ToList();

        Assert.Equal(new[] { HKCardExpirationService.WarningDeduplicationKey(seeded.CardId, 7) }, keys);
        Assert.Equal(HKCardStatus.Approved, await StatusAsync(seeded));
    }

    // ── Карты, которые трогать нельзя ─────────────────────────────────────

    [Fact]
    public async Task CardWithoutExpirationDate_IsNeverArchived()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: null);

        await seeded.Expiration.ProcessAsync();

        // Прямой вызов метода тоже обязан отказать: карта бессрочная.
        Assert.False(await seeded.HK.ArchiveExpiredAsync(seeded.CardId));
        Assert.Equal(HKCardStatus.Approved, await StatusAsync(seeded));
        Assert.Equal(0, await s.Db.HKCardStatusLogs.CountAsync(l => l.HKCardId == seeded.CardId));
    }

    [Theory]
    [InlineData(HKCardStatus.Draft)]
    [InlineData(HKCardStatus.OnReview)]
    [InlineData(HKCardStatus.RevisionRequired)]
    [InlineData(HKCardStatus.Archived)]
    [InlineData(HKCardStatus.Deleted)]
    public async Task CardNotApproved_IsNeverArchived(HKCardStatus status)
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: -5, status: status);

        await seeded.Expiration.ProcessAsync();

        Assert.False(await seeded.HK.ArchiveExpiredAsync(seeded.CardId));
        Assert.Equal(status, await StatusAsync(seeded));
    }

    [Fact]
    public async Task ArchiveExpiredAsync_DirectCall_ChecksDateItself()
    {
        // Проверка срока перенесена В САМ МЕТОД: вызывающий сервис больше не
        // единственный, кто защищает карту.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 3);

        Assert.False(await seeded.HK.ArchiveExpiredAsync(seeded.CardId));
        Assert.Equal(HKCardStatus.Approved, await StatusAsync(seeded));
        // Считается по своей карте: общая фикстура содержит чужие карты, которые
        // другие тесты архивируют, и глобальный счётчик ловил бы их.
        Assert.Equal(0, await s.Db.HKCardStatusLogs
            .CountAsync(l => l.HKCardId == seeded.CardId));
        Assert.Equal(0, await s.Db.AuditLogs.CountAsync(a =>
            a.EntityId == seeded.CardId.ToString() && a.Action == "HK.ExpiredArchived"));
    }

    [Fact]
    public async Task ArchiveExpiredAsync_DirectCall_ArchivesOnlyAfterExpiry_AndOnlyOnce()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: -1);

        Assert.True(await seeded.HK.ArchiveExpiredAsync(seeded.CardId));

        // Второй вызов обязан вернуть false: карта уже Archived. Раньше метод не
        // сохранял изменения, и повторный вызов на той же сессии видел
        // несохранённый Approved и снова возвращал true.
        Assert.False(await seeded.HK.ArchiveExpiredAsync(seeded.CardId));
        Assert.Equal(HKCardStatus.Archived, await StatusAsync(seeded));
        Assert.Equal(1, await s.Db.HKCardStatusLogs
            .CountAsync(l => l.HKCardId == seeded.CardId && l.ToStatus == HKCardStatus.Archived));
    }

    // ── Адресат отсутствует ───────────────────────────────────────────────

    [Fact]
    public async Task NoNormAdmin_RecordsProblem_AndOtherCardsStillProcessed()
    {
        await using var s = NewScope();
        var first = Seed(s, daysLeft: 7, withNormAdmin: false);
        var second = Seed(s, daysLeft: 7, secondCard: true);

        var result = await first.Expiration.ProcessAsync();

        Assert.Equal(0, result.CardsFailed);

        Assert.Equal(0, await s.Db.WorkTasks.CountAsync(t =>
            t.Type == WorkTaskType.HKExpirationReview && t.EntityId == first.CardId));
        Assert.Equal(1, await s.Db.WorkTasks.CountAsync(t =>
            t.Type == WorkTaskType.HKExpirationReview && t.EntityId == second.CardId));

        // Проблема назначения зафиксирована в аудите, а не потеряна.
        Assert.True(await s.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "HKCard" && a.Action == "Workflow.NoAssignee"
            && a.Details!.Contains(first.CardCode)));
    }

    [Fact]
    public async Task NoNormAdmin_RetriesOnNextRun()
    {
        // Раньше задача создавалась только при ТОЧНОМ совпадении порога: если
        // назначение не удалось в день порога, на следующий день порог уже не
        // совпадал и задача не создавалась никогда.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 7, withNormAdmin: false);

        await seeded.Expiration.ProcessAsync();
        Assert.Equal(0, await s.Db.WorkTasks.CountAsync(t =>
            t.Type == WorkTaskType.HKExpirationReview && t.EntityId == seeded.CardId));

        // Ветка остаётся без NormAdmin, поэтому повтор тоже не назначит —
        // важно, что попытка ПОВТОРЯЕТСЯ (новая запись в аудите), а не теряется.
        seeded.Clock.SetUtcNow(new DateTimeOffset(Today.AddDays(1), TimeSpan.Zero));
        await seeded.Expiration.ProcessAsync();

        Assert.Equal(2, await s.Db.AuditLogs.CountAsync(a =>
            a.Action == "Workflow.NoAssignee" && a.Details!.Contains(seeded.CardCode)));
    }

    // ── Срок задачи пересмотра ────────────────────────────────────────────

    [Fact]
    public async Task ReviewTaskDueDate_IsClampedToArchiveBoundary()
    {
        // Порог 7 дней + ReviewTaskDueDays=14 дали бы срок на 7 дней ПОЗже
        // автоархивации: задачу сняли бы, не дав выполнить.
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 7);

        await seeded.Expiration.ProcessAsync();

        var task = Assert.Single(await s.Db.WorkTasks
            .Where(t => t.Type == WorkTaskType.HKExpirationReview && t.EntityId == seeded.CardId)
            .ToListAsync());

        var archiveDay = Today.AddDays(8);
        Assert.True(task.DueDateUtc!.Value <= archiveDay,
            $"срок задачи {task.DueDateUtc:yyyy-MM-dd} позже границы автоархивации {archiveDay:yyyy-MM-dd}");
    }

    [Fact]
    public void ReviewTaskDueDate_KeepsConfiguredValue_WhenItFitsInsideWindow()
    {
        // Обратный случай: если настройка укладывается в доступное окно, она
        // сохраняется, а не подменяется границей архивации.
        var card = new HKCard { ExpirationDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc) };
        var options = new HKExpirationOptions { ReviewTaskDueDays = 3 };
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddDays(3), HKCardExpirationService.ReviewTaskDueDate(card, options, now));
    }

    // ── Получатели: организация и активность ──────────────────────────────

    [Fact]
    public async Task Notification_ReachesBranchNormAdmin_NotOtherBranchUser()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 30);

        // NormAdmin другой организации — не адресат, даже если роль та же.
        var otherBranch = NewBranch(s, "Чужой ");
        var outsider = CreateUser(s, "outsider_" + Suffix(), otherBranch, "NormAdmin");

        await seeded.Expiration.ProcessAsync();

        var recipients = await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring)
            .Select(n => n.UserId)
            .ToListAsync();

        Assert.Contains(seeded.NormAdminId, recipients);
        Assert.Contains(seeded.AuthorId, recipients);
        Assert.DoesNotContain(outsider, recipients);
    }

    [Fact]
    public async Task Notification_IsNotSentToDeactivatedUser()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 30, deactivateNormAdmin: true);

        await seeded.Expiration.ProcessAsync();

        var recipients = await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring)
            .Select(n => n.UserId)
            .ToListAsync();

        // Автор карты — действующий адресат; отключённый NormAdmin — нет.
        Assert.DoesNotContain(seeded.NormAdminId, recipients);
        Assert.Contains(seeded.AuthorId, recipients);
    }

    [Fact]
    public async Task Author_OutsideBranch_DoesNotReceiveNotification()
    {
        // Подтверждённый на рабочей базе дефект: автор карты добавлялся в
        // получатели без проверки организации.
        await using var s = NewScope();
        var branch = NewBranch(s, "Филиал ");
        var cardBranch = NewBranch(s, "Карточки ");
        var outsiderAuthor = CreateUser(s, "outside_" + Suffix(), branch);

        var seeded = Seed(s, daysLeft: 30, branchId: cardBranch, withNormAdmin: false);
        var card = await s.Db.HKCards.FirstAsync(c => c.Id == seeded.CardId);
        card.AuthorId = Guid.Parse(outsiderAuthor);
        card.BranchId = cardBranch;
        await s.Db.SaveChangesAsync();

        var result = await seeded.Expiration.ProcessAsync();

        var recipients = await s.Db.Notifications
            .Where(n => n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring)
            .Select(n => n.UserId)
            .ToListAsync();

        Assert.DoesNotContain(outsiderAuthor, recipients);
        Assert.Equal(1, result.RecipientsSkipped);
    }

    // ── Изоляция ошибок ───────────────────────────────────────────────────

    /// <summary>
    /// Сбой при архивировании не прерывает прогон и не оставляет частичных записей.
    /// <para>
    /// Почему не «одна карта, остальные обработаны»: перехватчик команд глушит
    /// ВСЕ совпадения подряд, а в общей фикстуре есть чужие карты с открытыми
    /// задачами, поэтому точно выбрать одну карту для сбоя нельзя. Такая
    /// формулировка была бы нестабильной и проверяла бы удачу.
    /// </para>
    /// <para>
    /// Гарантия «сбойная карта не мешает остальным» проверяется отдельно в
    /// <c>NoNormAdmin_RecordsProblem_AndOtherCardsStillProcessed</c>, где неудача
    /// адресная по построению.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ArchiveFailure_DoesNotBreakTheRun_AndLeavesNoPartialWrites()
    {
        await using var s = NewScope();
        var first = Seed(s, daysLeft: -1);
        var second = Seed(s, daysLeft: -1, secondCard: true);

        FailingCommandInterceptor.ArmAt(@"UPDATE ""HKCards"" SET ""Status""");
        try
        {
            var failed = await first.Expiration.ProcessAsync();

            Assert.True(FailingCommandInterceptor.Fired, "перехватчик не сработал — тест проверил бы не то");

            // Прогон завершился, а не упал, и сбои учтены.
            Assert.True(failed.CardsFailed > 0, "сбой не учтён в результате прогона");
            Assert.True(failed.CardsProcessed >= failed.CardsFailed,
                $"прогон прервался на сбое: обработано {failed.CardsProcessed}, с ошибкой {failed.CardsFailed}");

            // Ни одна карта не изменена наполовину: ни статуса, ни журнала, ни аудита.
            Assert.Equal(HKCardStatus.Approved, await StatusAsync(first));
            Assert.Equal(HKCardStatus.Approved, await StatusAsync(second));
            Assert.Equal(0, await s.Db.HKCardStatusLogs
                .CountAsync(l => (l.HKCardId == first.CardId || l.HKCardId == second.CardId)
                    && l.ToStatus == HKCardStatus.Archived));
            Assert.Equal(0, await s.Db.AuditLogs.CountAsync(a =>
                (a.EntityId == first.CardId.ToString() || a.EntityId == second.CardId.ToString())
                && a.Action == "HK.ExpiredArchived"));

            // Ошибка попала в журнал и называет карту, а не «прогон упал».
            var codes = new[] { first.CardCode, second.CardCode };
            Assert.True(codes.Any(code => s.Logs.HasError(nameof(HKCardExpirationService), code)),
                "ошибка обработки карты не попала в журнал: "
                + string.Join(" | ", s.Logs.Snapshot().Select(e => e.Message)));
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        // Следующий прогон идёт в НОВОЙ сессии: так это и выглядит в жизни
        // (новый запрос или новый запуск хоста). В той же сессии остался бы
        // трекер EF с несохранённым состоянием, и проверка зависела бы от деталей
        // отката транзакции, а не от поведения обработки.
        await using (var next = NewScope())
        {
            var recovered = await next.Expiration.ProcessAsync();

            Assert.Equal(0, recovered.CardsFailed);
            Assert.Equal(HKCardStatus.Archived, await StatusAsync(first, next));
            Assert.Equal(HKCardStatus.Archived, await StatusAsync(second, next));
        }
    }
    [Fact]
    public async Task FullPath_ThresholdToNotificationAndTask_ToArchive()
    {
        await using var s = NewScope();
        var seeded = Seed(s, daysLeft: 7);

        // 1. Порог достигнут.
        await seeded.Expiration.ProcessAsync();

        var task = Assert.Single(await s.Db.WorkTasks
            .Where(t => t.Type == WorkTaskType.HKExpirationReview && t.EntityId == seeded.CardId)
            .ToListAsync());
        Assert.Equal(WorkTaskStatus.Open, task.Status);
        Assert.Equal(seeded.CardCode, task.EntityCodeSnapshot);
        Assert.Equal(seeded.NormAdminId, task.AssignedToUserId);

        // 2. Назначенный пользователь видит задачу в своём разделе задач, и она
        //    ведёт на нужную карту.
        s.User.CurrentUserId = Guid.Parse(seeded.NormAdminId);
        var myTasks = await s.Tasks.GetMyTasksAsync(new WorkTaskQuery());
        var listed = Assert.Single(myTasks.Items.Where(t => t.Id == task.Id));
        Assert.Equal(seeded.CardId, listed.EntityId);

        // 3. Тот же пользователь видит уведомление со ссылкой на карту.
        var feed = await s.Notifications.GetMyNotificationsAsync(new NotificationQuery());
        var notification = Assert.Single(feed.Items.Where(n =>
            n.EntityId == seeded.CardId && n.Type == NotificationType.HKExpiring));
        Assert.Equal($"/хк/{seeded.CardId}", notification.NavigationUrl);

        // 4. День срока: только уведомление, карта ещё утверждена.
        seeded.Clock.SetUtcNow(new DateTimeOffset(Today.AddDays(7), TimeSpan.Zero));
        await seeded.Expiration.ProcessAsync();
        Assert.Equal(HKCardStatus.Approved, await StatusAsync(seeded));

        // 5. Следующий день: автоархивация, задача закрыта, история сохранена.
        seeded.Clock.SetUtcNow(new DateTimeOffset(Today.AddDays(8), TimeSpan.Zero));
        await seeded.Expiration.ProcessAsync();

        Assert.Equal(HKCardStatus.Archived, await StatusAsync(seeded));

        var closed = await s.Db.WorkTasks.AsNoTracking()
            .FirstAsync(t => t.Id == task.Id);
        Assert.Equal(WorkTaskStatus.Cancelled, closed.Status);

        Assert.True(await s.Db.HKCardStatusLogs.AnyAsync(l => l.HKCardId == seeded.CardId));
        Assert.True(await s.Db.AuditLogs.AnyAsync(a => a.Action == "HK.ExpiredArchived"));
    }

    [Fact]
    public async Task Archiving_PreservesComponentLinksFromApprovedParent()
    {
        await using var s = NewScope();
        var child = Seed(s, daysLeft: -1);

        var parent = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-PARENT-" + Suffix(),
            Version = "v1",
            Status = HKCardStatus.Approved,
            ObjectLevel = HKObjectLevel.EquipmentModel,
            EquipmentModelId = CreateEquipmentModel(s),
            BranchId = child.BranchId,
            CreatedAt = Today.AddDays(-40),
            ApprovedDate = Today.AddDays(-30),
        };
        s.Db.HKCards.Add(parent);
        s.Db.HKCardComponents.Add(new HKCardComponent
        {
            Id = Guid.NewGuid(),
            ParentHKCardId = parent.Id,
            ChildHKCardId = child.CardId,
            SortOrder = 1,
        });
        s.Db.SaveChanges();
        _cards.Add(parent.Id);

        await child.Expiration.ProcessAsync();

        Assert.Equal(HKCardStatus.Archived, await StatusAsync(child));

        // Ссылка из утверждённой родительской карты обязана сохраниться.
        var link = await s.Db.HKCardComponents.AsNoTracking()
            .SingleAsync(c => c.ChildHKCardId == child.CardId);
        Assert.Equal(parent.Id, link.ParentHKCardId);
    }

    // ── Вспомогательное ───────────────────────────────────────────────────

    // IgnoreQueryFilters обязателен: у удалённых карт иначе строка не находится,
    // и проверка «не архивирована» падала бы с «Sequence contains no elements».
    // IgnoreQueryFilters обязателен: у удалённых карт иначе строка не находится,
    // и проверка «не архивирована» падала бы с «Sequence contains no elements».
    // Scope передаётся явно, чтобы состояние можно было читать из другой сессии.
    private async Task<HKCardStatus> StatusAsync(Seeded seeded, TestScope? scope = null) =>
        (await (scope ?? seeded.Scope).Db.HKCards.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.Id == seeded.CardId)).Status;

    /// <summary>
    /// Помечает порог как уже отправленный. Так воспроизводится «приложение
    /// работало, потом пропало»: уведомление есть, дня порога обработка не
    /// видела.
    /// </summary>
    /// <summary>
    /// Открытая задача согласования карты. Нужна, чтобы сделать сбой
    /// адресным: закрытие задач выдаёт UPDATE WorkTasks, которого не делают
    /// карты без открытых задач.
    /// </summary>
    private static async Task SeedOpenReviewTaskAsync(TestScope s, Seeded seeded)
    {
        s.Db.WorkTasks.Add(new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = "Согласование " + seeded.CardCode,
            Type = WorkTaskType.HKReview,
            Status = WorkTaskStatus.Open,
            Priority = WorkTaskPriority.Normal,
            CreatedByUserId = null,
            AssignedToUserId = seeded.NormAdminId,
            BranchId = seeded.BranchId,
            EntityType = "HKCard",
            EntityId = seeded.CardId,
            EntityCodeSnapshot = seeded.CardCode,
            CreatedAtUtc = Today.AddDays(-1),
            DueDateUtc = Today.AddDays(6),
        });
        await s.Db.SaveChangesAsync();
    }
    private static async Task SeedSentThresholdAsync(TestScope s, Seeded seeded, int threshold)
    {
        s.Db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            // Получатель обязан существовать: на Notifications стоит FK на Users.
            UserId = seeded.NormAdminId,
            BranchId = seeded.BranchId,
            Type = NotificationType.HKExpiring,
            Title = "Уже отправлено",
            Message = "Порог отправлен до простоя приложения.",
            EntityType = "HKCard",
            EntityId = seeded.CardId,
            NavigationUrl = $"/хк/{seeded.CardId}",
            DeduplicationKey = HKCardExpirationService.WarningDeduplicationKey(seeded.CardId, threshold),
            IsRead = true,
            CreatedAtUtc = Today.AddDays(-(90 - threshold)),
        });
        await s.Db.SaveChangesAsync();
    }

    private static Npgsql.NpgsqlParameter GuidArray(IReadOnlyCollection<Guid> ids)
    {
        // Параметр создаётся на каждое утверждение: один экземпляр нельзя
        // переиспользовать между командами. Имя обязательно — без него драйвер
        // не сопоставляет @ids с параметром и отправляет плейсхолдер литералом,
        // что даёт «колонка ids не существует».
        return new Npgsql.NpgsqlParameter("ids", ids.ToArray())
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid,
        };
    }

    private static Npgsql.NpgsqlParameter TextArray(IReadOnlyCollection<Guid> ids) =>
        new("ids", ids.Select(i => i.ToString()).ToArray())
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        };

    private static async Task CleanupAsync(
        AppDbContext db, IReadOnlyCollection<Guid> ids, IReadOnlyCollection<Guid> nodes,
        IReadOnlyCollection<Guid> models, IReadOnlyCollection<Guid> branches)
    {
        // Порядок — от потомков к предкам, иначе FK не даст удалить.
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""Notifications"" WHERE ""EntityId"" = ANY(@ids)", GuidArray(ids));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""WorkTasks"" WHERE ""EntityId"" = ANY(@ids)", GuidArray(ids));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""HKCardComponents""
              WHERE ""ParentHKCardId"" = ANY(@ids) OR ""ChildHKCardId"" = ANY(@ids)", GuidArray(ids));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""HKCardItems"" WHERE ""HKCardId"" = ANY(@ids)", GuidArray(ids));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""HKCardStatusLogs"" WHERE ""HKCardId"" = ANY(@ids)", GuidArray(ids));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""AuditLogs"" WHERE ""EntityType"" = 'HKCard' AND ""EntityId"" = ANY(@ids)",
            TextArray(ids));
        await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""HKCards"" WHERE ""Id"" = ANY(@ids)", GuidArray(ids));

        if (nodes.Count > 0)
            await db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Nodes"" WHERE ""Id"" = ANY(@ids)", GuidArray(nodes));

        if (models.Count > 0)
            await db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""EquipmentModels"" WHERE ""Id"" = ANY(@ids)", GuidArray(models));

        if (branches.Count > 0)
            await db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Branches"" WHERE ""Id"" = ANY(@ids)", GuidArray(branches));
    }
}
