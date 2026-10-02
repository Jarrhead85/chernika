using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Прогноз обработки сроков действия ХК. Только чтение, без записи.
/// <para>
/// Зачем отдельный сервис. Включение worker'а способно архивировать
/// существующие карты, и «посмотреть, что произойдёт» обычным запуском означало
/// бы запись на рабочей базе. Прогноз отвечает на тот же вопрос, не меняя ни
/// одной строки.
/// </para>
/// <para>
/// <b>Доказательство отсутствия записи.</b> Прогноз выполняется внутри транзакции
/// READ ONLY, которую выставляет сервер, а не «заметкой в коде»: любая попытка
/// записи в ней отклоняется с ошибкой 25006. Без этого «посмотреть, что
/// произойдёт» оставалось бы обещанием, а не свойством.
/// </para>
/// <para>
/// Расчёт повторяет логику <see cref="HKCardExpirationService"/>. Дублирование
/// неизбежно: прогноз исполняется ДО прогона и по другой причине (не отменяя
/// его). Совпадение закреплено тестами на одном и том же наборе карт.
/// </para>
/// </summary>
public sealed class HKExpirationForecastService
{
    private readonly AppDbContext _db;
    private readonly IOptions<HKExpirationOptions> _options;
    private readonly TimeProvider _time;

    public HKExpirationForecastService(
        AppDbContext db,
        IOptions<HKExpirationOptions> options,
        TimeProvider time)
    {
        _db = db;
        _options = options;
        _time = time;
    }

    /// <summary>Фактические получатели уведомлений по карте с указанием причин отсечения.</summary>
    public sealed record RecipientReport(string UserId, bool Delivered, string? Reason);

    public async Task<HKExpirationForecast> BuildAsync(CancellationToken ct = default)
    {
        // Транзакция выставляется только если её ещё нет: вызывающий мог открыть
        // свою, а SET TRANSACTION READ ONLY в чужой транзакции недопустим.
        var readOnly = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        if (readOnly is not null)
            await _db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);

        try
        {
            var forecast = await BuildCoreAsync(ct);
            if (readOnly is not null)
                await readOnly.CommitAsync(ct);
            return forecast;
        }
        finally
        {
            if (readOnly is not null)
                await readOnly.DisposeAsync();
        }
    }

    private async Task<HKExpirationForecast> BuildCoreAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var thresholds = options.WarningDays.Distinct().Where(d => d > 0).OrderBy(d => d).ToArray();
        var reviewThreshold = thresholds.Length > 0 ? thresholds[0] : 0;

        var cards = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Status == HKCardStatus.Approved && c.ExpirationDate.HasValue)
            .OrderBy(c => c.ExpirationDate)
            .ThenBy(c => c.Code)
            .ToListAsync(ct);

        var result = new List<HKExpirationForecastCard>(cards.Count);

        foreach (var card in cards)
        {
            result.Add(await BuildCardAsync(
                card, today, thresholds, options.ReviewTaskDueDays, ct));
        }

        return new HKExpirationForecast
        {
            TodayUtc = today,
            WarningDays = thresholds,
            ReviewTaskThreshold = reviewThreshold,
            ReviewTaskDueDays = options.ReviewTaskDueDays,
            Cards = result,
        };
    }

    /// <summary>
    /// Получатели уведомлений по карте. Отсечённые возвращаются отдельным
    /// списком: «кому уйдёт» и «кого система не считает своим» — разные вопросы,
    /// и владельцу нужен ответ на оба.
    /// </summary>
    public async Task<(List<RecipientReport> Delivered, List<RecipientReport> Excluded)>
        RecipientsAsync(HKCard card, CancellationToken ct = default)
    {
        var candidates = new List<string>();
        if (card.AuthorId.HasValue)
            candidates.Add(card.AuthorId.Value.ToString());
        candidates.AddRange(await GetRoleHoldersIncludingInactiveAsync(card.BranchId, "NormAdmin", ct));

        var distinct = candidates.Distinct(StringComparer.Ordinal).ToList();
        var delivered = new List<RecipientReport>();
        var excluded = new List<RecipientReport>();

        if (distinct.Count == 0)
            return (delivered, excluded);

        var users = await _db.Users
            .AsNoTracking()
            .Where(u => distinct.Contains(u.Id))
            .Select(u => new { u.Id, u.IsActive, u.BranchId })
            .ToListAsync(ct);

        foreach (var id in distinct)
        {
            var user = users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal));
            if (user is null)
            {
                excluded.Add(new RecipientReport(id, false, "учётная запись не найдена"));
                continue;
            }

            if (!user.IsActive)
            {
                excluded.Add(new RecipientReport(id, false, "пользователь отключён"));
                continue;
            }

            if (user.BranchId is null)
            {
                excluded.Add(new RecipientReport(id, false, "пользователь вне организации карты (ветка не задана)"));
                continue;
            }

            if (user.BranchId.Value != card.BranchId)
            {
                excluded.Add(new RecipientReport(id, false, "пользователь относится к другой организации"));
                continue;
            }

            delivered.Add(new RecipientReport(id, true, null));
        }

        return (delivered, excluded);
    }

    /// <summary>
    /// Пользователи с ролью в ветке, ВКЛЮЧАЯ отключённых. Отличие от
    /// <c>HKCardService.GetBranchUsersInRoleAsync</c> намеренное: обработке нужны
    /// только активные, прогнозу — ещё и то, кто выбыл. Иначе отключённый
    /// NormAdmin просто исчез бы из кандидатов, и прогноз промолчал бы: владелец
    /// не увидел бы, что уведомление уйдёт мимо человека, которому оно
    /// предназначалось.
    /// </summary>
    private async Task<List<string>> GetRoleHoldersIncludingInactiveAsync(
        Guid branchId, string role, CancellationToken ct)
    {
        // Id роли — строка (Identity), а не Guid: приведение недопустимо.
        var roleId = await _db.Roles
            .Where(r => r.Name == role)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(roleId))
            return new List<string>();

        return await _db.Users
            .AsNoTracking()
            .Where(u => u.BranchId == branchId
                && _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == roleId))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    private async Task<HKExpirationForecastCard> BuildCardAsync(
        HKCard card, DateOnly today, int[] thresholds, int reviewDueDays,
        CancellationToken ct)
    {
        var expirationDay = DateOnly.FromDateTime(card.ExpirationDate!.Value.Date);
        var daysLeft = expirationDay.DayNumber - today.DayNumber;

        var sent = await SentThresholdsAsync(card.Id, ct);
        var expiredSent = await ExpiredNotificationSentAsync(card.Id, ct);

        var willArchive = daysLeft < 0;
        var willNotifyExpired = daysLeft <= 0 && !expiredSent;
        var willNotifyExpiring = false;
        var threshold = 0;
        var catchUp = false;

        if (daysLeft > 0)
        {
            // Порог считается пройденным, когда дней осталось не больше него:
            // daysLeft == threshold — штатный день порога, daysLeft < threshold —
            // простой приложения. Различается только давность.
            //
            // Берётся БЛИЖАЙШИЙ (наименьший) пройденный порог и только если он
            // ещё не отправлен. Рассмотрение более далёких порогов при уже
            // отправленном ближнем было бы лавиной.
            var nearest = thresholds.Where(d => d >= daysLeft).DefaultIfEmpty(0).Min();

            if (nearest > 0 && !sent.Contains(nearest))
            {
                threshold = nearest;
                willNotifyExpiring = true;
                // Догоняющим считаем уведомление, которое отправилось бы позже
                // своей даты: точное совпадение даёт daysLeft == threshold.
                catchUp = daysLeft != threshold;
            }
        }

        // Задача пересмотра создаётся на любом пройденном пороге КРОМЕ самого
        // раннего: пересматривать карту за 90 дней рано. Совпадает с обработкой,
        // где условие выполняется каждый день внутри окна, поэтому сбой
        // назначения не остаётся незамеченным.
        var nearestThreshold = thresholds.Where(d => d >= daysLeft).DefaultIfEmpty(0).Min();
        var willCreateReviewTask =
            nearestThreshold > 0 && nearestThreshold != thresholds[thresholds.Length - 1];

        var openTasks = await _db.WorkTasks
            .AsNoTracking()
            .CountAsync(t => !t.IsDeleted
                && t.EntityType == "HKCard" && t.EntityId == card.Id
                && (t.Type == WorkTaskType.HKExpirationReview || t.Type == WorkTaskType.HKReview)
                && (t.Status == WorkTaskStatus.Open
                    || t.Status == WorkTaskStatus.InProgress
                    || t.Status == WorkTaskStatus.Overdue), ct);

        var links = await _db.HKCardComponents
            .AsNoTracking()
            .CountAsync(c => c.ParentHKCardId == card.Id || c.ChildHKCardId == card.Id, ct);

        var statusLogs = await _db.HKCardStatusLogs
            .AsNoTracking()
            .CountAsync(l => l.HKCardId == card.Id, ct);

        var (_, excludedRecipients) = await RecipientsAsync(card, ct);

        var actions = new List<string>();
        if (willNotifyExpired)
            actions.Add(daysLeft == 0
                ? "уведомление «истёк сегодня» (архива в этот день нет)"
                : "догоняющее уведомление об истечении (было пропущено простоем)");
        if (willNotifyExpiring)
            actions.Add(catchUp
                ? $"догоняющее уведомление за порог {threshold} дн. (точный день был пропущен)"
                : $"уведомление за порог {threshold} дн.");
        if (willCreateReviewTask)
        {
            var nowUtc = _time.GetUtcNow().UtcDateTime;
            var due = HKCardExpirationService.ReviewTaskDueDate(card, ReviewTaskDueOptions(reviewDueDays), nowUtc);
            var naive = nowUtc.AddDays(reviewDueDays);
            actions.Add($"задача «Пересмотр ХК» со сроком {due:yyyy-MM-dd}"
                + (naive > due ? $" (усечено с {naive:yyyy-MM-dd}: позже автоархивации)" : string.Empty));
        }
        if (willArchive)
            actions.Add($"АВТОАРХИВИРОВАНИЕ (срок истёк {(today.DayNumber - expirationDay.DayNumber)} дн. назад)");
        if (actions.Count == 0)
            actions.Add("не трогается этим прогоном");

        return new HKExpirationForecastCard
        {
            Id = card.Id,
            Code = card.Code,
            Version = card.Version,
            ExpirationDayUtc = expirationDay,
            DaysLeft = daysLeft,
            WillArchive = willArchive,
            WillNotifyExpiring = willNotifyExpiring,
            WillNotifyExpired = willNotifyExpired,
            WillCreateReviewTask = willCreateReviewTask,
            NotificationThreshold = threshold,
            IsCatchUpNotification = catchUp,
            OpenWorkflowTasksToClose = willArchive ? openTasks : 0,
            ComponentLinksToPreserve = links,
            StatusLogRowsToPreserve = statusLogs,
            RecipientsExcluded = excludedRecipients.Count,
            PlannedActions = actions,
        };
    }

    private static HKExpirationOptions ReviewTaskDueOptions(int dueDays) =>
        new() { ReviewTaskDueDays = dueDays };

    private async Task<HashSet<int>> SentThresholdsAsync(Guid cardId, CancellationToken ct)
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

    private Task<bool> ExpiredNotificationSentAsync(Guid cardId, CancellationToken ct) =>
        _db.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.EntityType == "HKCard" && n.EntityId == cardId
                && n.Type == NotificationType.HKExpired, ct);
}