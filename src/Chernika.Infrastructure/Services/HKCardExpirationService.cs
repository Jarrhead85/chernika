using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Обработка сроков действия химмотологических карт.
/// <para>
/// <b>Семантика дня окончания.</b> Карта действует В ТЕЧЕНИЕ всего своего
/// <c>ExpirationDate</c>: в день окончания она ещё действует, поэтому в этот день
/// отправляется уведомление «истёк сегодня» и автоархивации НЕТ. Архивирование
/// начинается со СЛЕДУЮЩЕГО UTC-дня. Это то же правило, что и в исходной
/// реализации; оно не менялось, а зафиксировано явно и покрыто тестами.
/// </para>
/// <para>
/// <b>Догоняющая обработка.</b> Пороги 90/30/7 выбирались по ТОЧНОМУ совпадению
/// числа дней, поэтому простой приложения пропускал уведомления безвозвратно.
/// Теперь, если порог уже прошёл, отправляется ОДИН наиболее актуальный ещё не
/// отправленный порог, а картам, запущенным уже после истечения, — одно
/// догоняющее уведомление об истечении. Лавины старых уведомлений не
/// возникает: не более одного уведомления о приближении на карту за прогон.
/// </para>
/// <para>
/// <b>Защита от двух прогонов.</b> Перед обработкой берётся advisory lock уровня
/// сессии. Настройка владельца (<c>HKExpiration:WorkerEnabled</c>) защищает от
/// двух <i>хостов</i>; lock защищает от двух одновременных <i>прогонов</i> в
/// одной базе — это случается при нескольких экземплярах хоста, при повторном
/// запуске worker'а и при ручном вызове сервиса из теста.
/// </para>
/// </summary>
public class HKCardExpirationService
{
    /// <summary>
    /// Ключ advisory lock. Произвольное, но стабильное число: оно идентифицирует
    /// работу с терминами действия ХК во всех экземплярах приложения на одной
    /// базе.
    /// </summary>
    public const long AdvisoryLockKey = 8_726_451_003_377_711L;

    private readonly AppDbContext _db;
    private readonly HKCardService _hkCards;
    private readonly TaskService _tasks;
    private readonly NotificationService _notifications;
    private readonly AuditService _audit;
    private readonly IOptions<HKExpirationOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HKCardExpirationService> _logger;

    public HKCardExpirationService(
        AppDbContext db,
        HKCardService hkCards,
        TaskService tasks,
        NotificationService notifications,
        AuditService audit,
        IOptions<HKExpirationOptions> options,
        TimeProvider time,
        ILogger<HKCardExpirationService> logger)
    {
        _db = db;
        _hkCards = hkCards;
        _tasks = tasks;
        _notifications = notifications;
        _audit = audit;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Результат одного прогона обработки.</summary>
    public sealed record RunResult(
        bool AcquiredLock,
        int CardsProcessed,
        int CardsArchived,
        int WarningsSent,
        int ExpiryNotificationsSent,
        int ReviewTasksCreated,
        int CardsFailed,
        int RecipientsSkipped);

    public async Task<int> ProcessExpiringCardsAsync(CancellationToken ct = default)
        => (await ProcessAsync(ct)).CardsProcessed;

    /// <summary>
    /// Прогон с подробным результатом. Нужен для проверок и для журнала: по
    /// одному числу обработанных карт нельзя отличить «всё хорошо» от «всё
    /// молча упало, потому что карт не было».
    /// </summary>
    public async Task<RunResult> ProcessAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        var today = _time.GetUtcNow().UtcDateTime.Date;

        // Одно соединение удерживается на весь прогон: advisory lock привязан к
        // сессии, и без явного удержания соединения блокировка освободилась бы
        // после первой транзакции карты.
        await _db.Database.OpenConnectionAsync(ct);
        var acquired = await TryAcquireLockAsync(ct);
        if (!acquired)
        {
            _logger.LogInformation(
                "Обработка сроков ХК пропущена: параллельный прогон уже выполняется в этой базе.");
            await _db.Database.CloseConnectionAsync();
            return new RunResult(false, 0, 0, 0, 0, 0, 0, 0);
        }

        try
        {
            var cards = await _db.HKCards
                .AsNoTracking()
                .Where(c => c.Status == HKCardStatus.Approved && c.ExpirationDate.HasValue)
                .OrderBy(c => c.Id)
                .ToListAsync(ct);

            var counters = new Counters();
            foreach (var card in cards)
            {
                try
                {
                    await ProcessCardAsync(card, today, options, counters, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Одна сбойная карта не должна останавливать цикл: иначе
                    // после первой ошибки весь цикл сроков молча перестаёт
                    // обрабатываться, и это заметят только по отсутствию
                    // уведомлений.
                    counters.Failed++;
                    _logger.LogError(
                        ex,
                        "Ошибка обработки срока действия ХК {CardCode} ({CardId}); обработка остальных карт продолжается.",
                        card.Code, card.Id);
                }
            }

            try
            {
                await _tasks.ProcessOverdueTasksAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки просроченных задач");
            }

            if (counters.Failed > 0)
                _logger.LogWarning("Ошибки обработки сроков: карт с ошибкой — {Failed}.", counters.Failed);

            return new RunResult(
                true, cards.Count, counters.Archived, counters.WarningsSent,
                counters.ExpiryNotifications, counters.ReviewTasksCreated,
                counters.Failed, counters.RecipientsSkipped);
        }
        finally
        {
            await ReleaseLockAsync(ct);
            await _db.Database.CloseConnectionAsync();
        }
    }

    private async Task<bool> TryAcquireLockAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        await using var command = conn.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        command.Parameters.Add(new NpgsqlParameter("key", AdvisoryLockKey));
        var result = await command.ExecuteScalarAsync(ct);
        return result is bool locked && locked;
    }

    private async Task ReleaseLockAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        try
        {
            await using var command = conn.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@key)";
            command.Parameters.Add(new NpgsqlParameter("key", AdvisoryLockKey));
            await command.ExecuteScalarAsync(ct);
        }
        catch (Exception ex)
        {
            // Соединение всё равно закроется и освободит блокировку вместе с
            // сессией; падать из-за этого нельзя, иначе потеряем результаты
            // успешно обработанных карт.
            _logger.LogWarning(ex, "Не удалось снять advisory lock обработки сроков ХК; он освободится при закрытии соединения.");
        }
    }

    private sealed class Counters
    {
        public int Archived;
        public int WarningsSent;
        public int ExpiryNotifications;
        public int ReviewTasksCreated;
        public int Failed;
        public int RecipientsSkipped;
    }

    private async Task ProcessCardAsync(
        HKCard card, DateTime today, HKExpirationOptions options, Counters counters, CancellationToken ct)
    {
        var expiration = card.ExpirationDate!.Value.Date;
        var daysLeft = (expiration - today).Days;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (daysLeft < 0)
        {
            // Действует весь день окончания, значит архивирование — со следующего
            // дня. Перед архивированием доставляем догоняющее уведомление об
            // истечении: если приложение запустили уже после истечения, уведомление
            // за день истечения было пропущено, и молча архивировать карту значит
            // оставить пользователя без известия. Ключ дедупликации делает это
            // идемпотентным.
            if (await CreateExpiredNotificationAsync(card, counters, ct) > 0)
                counters.ExpiryNotifications++;

            if (await _hkCards.ArchiveExpiredAsync(card.Id, ct))
                counters.Archived++;
        }
        else if (daysLeft == 0)
        {
            if (await CreateExpiredNotificationAsync(card, counters, ct) > 0)
                counters.ExpiryNotifications++;
        }
        else
        {
            var outcome = await CreateWarningAsync(card, daysLeft, options, counters, ct);
            counters.WarningsSent += outcome.NotificationsSent;
            counters.ReviewTasksCreated += outcome.ReviewTaskCreated ? 1 : 0;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private sealed record WarningOutcome(int NotificationsSent, bool ReviewTaskCreated);

    private async Task<WarningOutcome> CreateWarningAsync(
        HKCard card, int daysLeft, HKExpirationOptions options, Counters counters, CancellationToken ct)
    {
        var thresholds = options.WarningDays
            .Distinct()
            .Where(d => d > 0)
            .OrderBy(d => d)
            .ToArray();
        if (thresholds.Length == 0)
            return new WarningOutcome(0, false);

        // Уже отправленные пороги этой карты: без этого запроса после простоя
        // приложения порог 90 отправился бы заново вместе с 30 и 7 — три
        // уведомления вместо одного.
        var alreadySent = await SentWarningThresholdsAsync(card.Id, ct);

        // Порог считается ПРОЙДЕННЫМ, когда дней осталось не больше него: в этом и
        // состоит разница между штатным днём порога (daysLeft == threshold) и
        // простоем (daysLeft < threshold) — в обоих случаях порог наступил,
        // различается только давность.
        //
        // Берётся БЛИЖАЙШИЙ (наименьший) пройденный порог, и только если он ещё
        // не отправлен. Рассмотрение более далёких порогов при уже отправленном
        // ближнем было бы лавиной: отправив 7, на следующем прогоне сервис счёл бы
        // «неотправленным» 30 и отправил его, потом 90 — по уведомлению в день.
        var nearest = thresholds.Where(d => d >= daysLeft).DefaultIfEmpty(0).Min();
        var threshold = nearest > 0 && !alreadySent.Contains(nearest) ? nearest : 0;

        var sent = 0;
        var (recipients, skipped) = await ResolveRecipientsAsync(card, ct);
        counters.RecipientsSkipped += skipped.Count;

        if (threshold > 0)
        {
            var warning = new CreateNotificationCommand(
                Type: NotificationType.HKExpiring,
                Title: $"Срок действия ХК {card.Code} истекает",
                Message: $"Срок действия ХК {card.Code} (v{card.Version}) истекает {card.ExpirationDate!.Value:dd.MM.yyyy}.",
                EntityType: "HKCard",
                EntityId: card.Id,
                NavigationUrl: $"/хк/{card.Id}",
                BranchId: card.BranchId,
                DeduplicationKey: WarningDeduplicationKey(card.Id, threshold));

            foreach (var userId in recipients)
            {
                var created = await _notifications.CreateFromWorkflowAsync(userId, warning, Guid.Empty, ct);
                if (created != null)
                    sent++;
            }
        }

        // Задача пересмотра: как и прежде, на любом пороге КРОМЕ самого раннего
        // (90 дней — слишком рано, чтобы пересматривать). Условие «не самый
        // дальний» вместо «последний порог» сохранено намеренно: сужение до
        // одного порога означало бы, что карта на 30 днях останется без задачи.
        //
        // При этом попытка больше не зависит от того, отправлено ли НОВОЕ
        // уведомление. Раньше она была вложена в ветку «точное совпадение
        // порога»: если назначение не удалось в день порога, на следующий день
        // порог уже не совпадал и задача не создавалась никогда. Теперь условие
        // выполняется каждый день, пока срок внутри окна, поэтому попытка
        // повторяется и сбой назначения не остаётся навсегда незамеченным.
        var reviewTaskCreated = false;
        if (nearest > 0 && nearest != thresholds[thresholds.Length - 1])
            reviewTaskCreated = await CreateReviewTaskAsync(card, options, ct);

        if (sent > 0 || reviewTaskCreated)
        {
            await _audit.CreateLogAsync(new AuditWriteRequest(
                EntityType: "HKCard",
                EntityId: card.Id.ToString(),
                Action: "HK.ExpirationWarningCreated",
                ActorUserId: Guid.Empty,
                EntityDisplayName: $"{card.Code} v{card.Version}",
                Details: $"Срок действия истекает {card.ExpirationDate!.Value:dd.MM.yyyy} (порог: {threshold} дн.)."), ct);
        }

        return new WarningOutcome(sent, reviewTaskCreated);
    }

    /// <summary>
    /// Ключ дедупликации уведомления о приближении. Публичный статический метод:
    /// сервис и его тесты обязаны строить ключ одинаково, иначе дедупликация
    /// перестанет работать незаметно.
    /// </summary>
    public static string WarningDeduplicationKey(Guid cardId, int threshold) =>
        $"hk-exp-warning:{cardId}:{threshold}";

    /// <summary>Ключ дедупликации уведомления об истечении.</summary>
    public static string ExpiredDeduplicationKey(Guid cardId) => $"hk-expired:{cardId}";

    private async Task<HashSet<int>> SentWarningThresholdsAsync(Guid cardId, CancellationToken ct)
    {
        var keys = await _db.Notifications
            .AsNoTracking()
            .Where(n => n.EntityType == "HKCard" && n.EntityId == cardId
                && n.Type == NotificationType.HKExpiring
                && n.DeduplicationKey != null)
            .Select(n => n.DeduplicationKey!)
            .ToListAsync(ct);

        var result = new HashSet<int>();
        foreach (var key in keys)
        {
            var tail = key[(key.LastIndexOf(':') + 1)..];
            if (int.TryParse(tail, out var threshold))
                result.Add(threshold);
        }

        return result;
    }

    private async Task<bool> CreateReviewTaskAsync(HKCard card, HKExpirationOptions options, CancellationToken ct)
    {
        var assignee = (await _hkCards.GetBranchUsersInRoleAsync(card.BranchId, "NormAdmin")).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(assignee))
        {
            // Нет адресата — это проблема назначения, а не повод уронить цикл.
            // Фиксируется записью в аудит; обработка остальных карт продолжается.
            // На следующем прогоне попытка повторится: порог проверяется как
            // «срок подошёл», а не «точное совпадение дня».
            await _audit.CreateLogAsync(new AuditWriteRequest(
                EntityType: "HKCard",
                EntityId: card.Id.ToString(),
                Action: "Workflow.NoAssignee",
                ActorUserId: Guid.Empty,
                EntityDisplayName: $"{card.Code} v{card.Version}",
                Details: $"Нет активного пользователя с ролью NormAdmin в организации для задачи «Пересмотр ХК {card.Code}»."), ct);
            return false;
        }

        var hasOpenTask = await _db.WorkTasks.AnyAsync(t =>
            !t.IsDeleted
            && t.Type == WorkTaskType.HKExpirationReview
            && t.EntityType == "HKCard"
            && t.EntityId == card.Id
            && (t.Status == WorkTaskStatus.Open
                || t.Status == WorkTaskStatus.InProgress
                || t.Status == WorkTaskStatus.Overdue), ct);
        if (hasOpenTask)
            return false;

        await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
            Title: $"Пересмотр ХК {card.Code}",
            Type: WorkTaskType.HKExpirationReview,
            Priority: WorkTaskPriority.Normal,
            Description: $"Срок действия ХК {card.Code} (v{card.Version}) истекает {card.ExpirationDate!.Value:dd.MM.yyyy}. Требуется пересмотр карты.",
            AssignedToUserId: assignee,
            BranchId: card.BranchId,
            EntityType: "HKCard",
            EntityId: card.Id,
            EntityCodeSnapshot: card.Code,
            EntityTitleSnapshot: $"v{card.Version}",
            DueDateUtc: ReviewTaskDueDate(card, options, _time.GetUtcNow().UtcDateTime)),
            actorUserId: Guid.Empty,
            ct: ct);

        return true;
    }

    /// <summary>
    /// Срок задачи «Пересмотр ХК»: <c>сейчас + ReviewTaskDueDays</c>, но не позже
    /// конца дня окончания действия карты.
    /// <para>
    /// Задача создаётся на последнем пороге (7 дней), а автоархивация
    /// происходит на следующий день после <c>ExpirationDate</c>, то есть через
    /// 7 дней после порога. При <c>ReviewTaskDueDays = 14</c> наивный срок
    /// оказывался бы на 7 дней позже момента, когда автоархивация отменит задачу
    /// — пользователь получил бы «долгую» задачу, которую снимут, не дав её
    /// выполнить. Поэтому срок усекается границей архивации.
    /// </para>
    /// <para>
    /// Правило ровно одно и оно проверяется тестом: срок задачи никогда не
    /// позже момента автоархивации. Если <c>ReviewTaskDueDays</c> меньше
    /// доступного окна, берётся он — усечение тогда ничего не меняет.
    /// </para>
    /// </summary>
    public static DateTime ReviewTaskDueDate(HKCard card, HKExpirationOptions options, DateTime nowUtc)
    {
        var naive = nowUtc.AddDays(options.ReviewTaskDueDays);

        // Конец дня окончания по UTC. Карта действует до конца этого дня,
        // поэтому задача должна быть выполнена не позже конца этого дня.
        var archiveBoundary = card.ExpirationDate!.Value.Date.AddDays(1).AddSeconds(-1);

        return naive <= archiveBoundary ? naive : archiveBoundary;
    }

    private async Task<int> CreateExpiredNotificationAsync(HKCard card, Counters counters, CancellationToken ct)
    {
        var (recipients, skipped) = await ResolveRecipientsAsync(card, ct);
        counters.RecipientsSkipped += skipped.Count;

        var command = new CreateNotificationCommand(
            Type: NotificationType.HKExpired,
            Title: $"Срок действия ХК {card.Code} истёк",
            Message: $"Срок действия ХК {card.Code} (v{card.Version}) истёк {card.ExpirationDate!.Value:dd.MM.yyyy}.",
            EntityType: "HKCard",
            EntityId: card.Id,
            NavigationUrl: $"/хк/{card.Id}",
            BranchId: card.BranchId,
            DeduplicationKey: ExpiredDeduplicationKey(card.Id));

        var created = 0;
        foreach (var userId in recipients)
        {
            var notification = await _notifications.CreateFromWorkflowAsync(userId, command, Guid.Empty, ct);
            if (notification != null)
                created++;
        }

        return created;
    }

    /// <summary>
    /// Получатели уведомлений по карте: автор и активные NormAdmin её
    /// организации.
    /// <para>
    /// Фильтр по организации и активности применяется к ВСЕМ, включая автора.
    /// Раньше автор добавлялся первым и без проверок, из-за чего уведомление
    /// уходило человеку, который уже не относится к организации карты: в
    /// рабочей базе автор одной из карт имеет пустой BranchId.
    /// </para>
    /// </summary>
    private async Task<(List<string> Recipients, List<string> Skipped)> ResolveRecipientsAsync(
        HKCard card, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (card.AuthorId.HasValue)
            candidates.Add(card.AuthorId.Value.ToString());
        candidates.AddRange(await _hkCards.GetBranchUsersInRoleAsync(card.BranchId, "NormAdmin"));

        var distinct = candidates.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            return (new List<string>(), new List<string>());

        var users = await _db.Users
            .Where(u => distinct.Contains(u.Id))
            .Select(u => new { u.Id, u.IsActive, u.BranchId })
            .ToListAsync(ct);

        var recipients = new List<string>();
        var skipped = new List<string>();
        foreach (var user in users)
        {
            if (!user.IsActive)
            {
                skipped.Add(user.Id);
                continue;
            }

            // BranchId сравнивается как строка: ApplicationUser.Id — строка, а
            // BranchId в карте — Guid, поэтому оба приводятся к Guid там, где
            // значение задано. Пустая ветка у пользователя — это «вне организации».
            var userBranch = user.BranchId?.ToString();
            if (string.IsNullOrEmpty(userBranch)
                || !string.Equals(userBranch, card.BranchId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(user.Id);
                continue;
            }

            recipients.Add(user.Id);
        }

        // Кандидат, которого нет в Users (удалённая запись), не является
        // адресатом и не считается отказом по организации.
        var known = users.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in distinct.Where(c => !known.Contains(c)))
            _logger.LogWarning("Получатель уведомления по ХК {CardCode} не найден среди пользователей.", card.Code);

        return (recipients, skipped);
    }
}