using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chernika.Infrastructure.Services;

public class HKCardExpirationService
{
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

    public async Task<int> ProcessExpiringCardsAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        var today = _time.GetUtcNow().UtcDateTime.Date;

        var cards = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Status == HKCardStatus.Approved && c.ExpirationDate.HasValue)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        var processed = 0;
        foreach (var card in cards)
        {
            try
            {
                await ProcessCardAsync(card, today, options, ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки срока действия ХК {CardCode} ({CardId})", card.Code, card.Id);
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

        return processed;
    }

    private async Task ProcessCardAsync(HKCard card, DateTime today, HKExpirationOptions options, CancellationToken ct)
    {
        var expiration = card.ExpirationDate!.Value.Date;
        var daysLeft = (expiration - today).Days;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (daysLeft < 0)
        {
            await _hkCards.ArchiveExpiredAsync(card.Id, ct);
        }
        else if (daysLeft == 0)
        {
            await CreateExpiredNotificationAsync(card, ct);
        }
        else
        {
            await CreateWarningAsync(card, daysLeft, options, ct);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task CreateWarningAsync(HKCard card, int daysLeft, HKExpirationOptions options, CancellationToken ct)
    {
        var thresholds = options.WarningDays
            .Where(d => d > 0)
            .OrderByDescending(d => d)
            .ToArray();
        if (thresholds.Length == 0)
            return;

        var threshold = thresholds.FirstOrDefault(d => daysLeft <= d);
        if (threshold <= 0)
            return;

        var recipients = await GetRecipientIdsAsync(card, ct);
        var warning = new CreateNotificationCommand(
            Type: NotificationType.HKExpiring,
            Title: $"Срок действия ХК {card.Code} истекает",
            Message: $"Срок действия ХК {card.Code} (v{card.Version}) истекает {card.ExpirationDate!.Value:dd.MM.yyyy}.",
            EntityType: "HKCard",
            EntityId: card.Id,
            NavigationUrl: $"/хк/{card.Id}",
            BranchId: card.BranchId,
            DeduplicationKey: $"hk-exp-warning:{card.Id}:{threshold}");

        foreach (var userId in recipients)
            await _notifications.CreateFromWorkflowAsync(userId, warning, Guid.Empty, ct);

        if (threshold < thresholds[0])
            await CreateReviewTaskAsync(card, options, ct);

        await _audit.CreateLogAsync(new AuditWriteRequest(
            EntityType: "HKCard",
            EntityId: card.Id.ToString(),
            Action: "HK.ExpirationWarningCreated",
            ActorUserId: Guid.Empty,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: $"Срок действия истекает {card.ExpirationDate!.Value:dd.MM.yyyy} (порог: {threshold} дн.)."), ct);
    }

    private async Task CreateReviewTaskAsync(HKCard card, HKExpirationOptions options, CancellationToken ct)
    {
        var assignee = (await _hkCards.GetBranchUsersInRoleAsync(card.BranchId, "NormAdmin")).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(assignee))
        {
            await _audit.CreateLogAsync(new AuditWriteRequest(
                EntityType: "HKCard",
                EntityId: card.Id.ToString(),
                Action: "Workflow.NoAssignee",
                ActorUserId: Guid.Empty,
                EntityDisplayName: $"{card.Code} v{card.Version}",
                Details: $"Нет активного пользователя с ролью NormAdmin в филиале для задачи «Пересмотр ХК {card.Code}»."), ct);
            return;
        }

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
            DueDateUtc: _time.GetUtcNow().UtcDateTime.AddDays(options.ReviewTaskDueDays)),
            actorUserId: Guid.Empty,
            ct: ct);
    }

    private async Task CreateExpiredNotificationAsync(HKCard card, CancellationToken ct)
    {
        var recipients = await GetRecipientIdsAsync(card, ct);
        var command = new CreateNotificationCommand(
            Type: NotificationType.HKExpired,
            Title: $"Срок действия ХК {card.Code} истёк",
            Message: $"Срок действия ХК {card.Code} (v{card.Version}) истёк {card.ExpirationDate!.Value:dd.MM.yyyy}.",
            EntityType: "HKCard",
            EntityId: card.Id,
            NavigationUrl: $"/хк/{card.Id}",
            BranchId: card.BranchId,
            DeduplicationKey: $"hk-expired:{card.Id}");

        foreach (var userId in recipients)
            await _notifications.CreateFromWorkflowAsync(userId, command, Guid.Empty, ct);
    }

    private async Task<List<string>> GetRecipientIdsAsync(HKCard card, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (card.AuthorId.HasValue)
            candidates.Add(card.AuthorId.Value.ToString());
        candidates.AddRange(await _hkCards.GetBranchUsersInRoleAsync(card.BranchId, "NormAdmin"));

        var ids = candidates.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return ids;

        return await _db.Users
            .Where(u => u.IsActive && ids.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }
}
