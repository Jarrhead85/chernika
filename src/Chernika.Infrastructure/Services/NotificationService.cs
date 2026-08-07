using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class NotificationService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IPermissionService _permissions;
    private readonly AuditService _audit;

    public NotificationService(
        AppDbContext db,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _permissions = permissions;
        _audit = audit;
    }

    public async Task CreateAsync(CreateNotificationCommand command, CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId().ToString();
        var created = await AddAsync(userId, command, ct);
        if (created == null) return;
        await _audit.LogAsync(
            new AuditWriteRequest(
                EntityType: "Notification",
                EntityId: created.Id.ToString(),
                Action: "Notification.Created",
                ActorUserId: _currentUser.GetRequiredUserId(),
                EntityDisplayName: created.Title),
            ct);
    }

    public async Task CreateForUsersAsync(IEnumerable<string> userIds, CreateNotificationCommand command, CancellationToken ct = default)
    {
        var uniqueUsers = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (uniqueUsers.Count == 0)
            return;

        var created = new List<Notification>();
        if (!string.IsNullOrEmpty(command.DeduplicationKey))
        {
            var existing = await _db.Notifications
                .Where(n => command.DeduplicationKey != null && n.DeduplicationKey == command.DeduplicationKey && uniqueUsers.Contains(n.UserId))
                .Select(n => n.UserId)
                .ToListAsync(ct);
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);
            foreach (var userId in uniqueUsers)
            {
                if (existingSet.Contains(userId))
                    continue;
                var notification = CreateEntity(userId, command);
                _db.Notifications.Add(notification);
                created.Add(notification);
            }
        }
        else
        {
            foreach (var userId in uniqueUsers)
            {
                var notification = CreateEntity(userId, command);
                _db.Notifications.Add(notification);
                created.Add(notification);
            }
        }

        await _db.SaveChangesAsync(ct);

        foreach (var notification in created)
        {
            await _audit.LogAsync(
                new AuditWriteRequest(
                    EntityType: "Notification",
                    EntityId: notification.Id.ToString(),
                    Action: "Notification.Created",
                    ActorUserId: Guid.Empty,
                    EntityDisplayName: notification.Title),
                ct);
        }
    }

    public async Task<PagedResult<NotificationDto>> GetMyNotificationsAsync(NotificationQuery query, CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId().ToString();
        await _permissions.DemandPermissionAsync(PermissionCodes.NotificationView, ct);

        var baseQuery = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        baseQuery = ApplyFilters(baseQuery, query);

        var total = await baseQuery.CountAsync(ct);
        var items = await baseQuery
            .OrderByDescending(n => n.CreatedAtUtc)
            .Skip((Math.Max(1, query.Page) - 1) * Math.Clamp(query.PageSize, 1, 200))
            .Take(Math.Clamp(query.PageSize, 1, 200))
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Message = n.Message,
                EntityType = n.EntityType,
                EntityId = n.EntityId,
                WorkTaskId = n.WorkTaskId,
                NavigationUrl = n.NavigationUrl,
                IsRead = n.IsRead,
                ReadAtUtc = n.ReadAtUtc,
                CreatedAtUtc = n.CreatedAtUtc,
            })
            .ToListAsync(ct);

        return new PagedResult<NotificationDto>
        {
            Items = items,
            TotalCount = total,
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, 200),
        };
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId().ToString();
        var now = DateTime.UtcNow;
        return await _db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead && (n.ExpiresAtUtc == null || n.ExpiresAtUtc > now))
            .CountAsync(ct);
    }

    public async Task MarkAsReadAsync(Guid notificationId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId().ToString();
        await _permissions.DemandPermissionAsync(PermissionCodes.NotificationMarkRead, ct);

        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notification == null)
            throw new UnauthorizedAccessException("Уведомление не найдено или принадлежит другому пользователю.");

        if (notification.IsRead)
            return;

        notification.IsRead = true;
        notification.ReadAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            new AuditWriteRequest(
                EntityType: "Notification",
                EntityId: notification.Id.ToString(),
                Action: "Notification.Read",
                ActorUserId: _currentUser.GetRequiredUserId(),
                EntityDisplayName: notification.Title),
            ct);
    }

    public async Task MarkAllAsReadAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId().ToString();
        await _permissions.DemandPermissionAsync(PermissionCodes.NotificationMarkRead, ct);

        var unreadIds = await _db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .Select(n => n.Id)
            .ToListAsync(ct);
        if (unreadIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var unreadSet = unreadIds.ToHashSet();
        var unread = await _db.Notifications.Where(n => unreadSet.Contains(n.Id)).ToListAsync(ct);
        foreach (var notification in unread)
        {
            notification.IsRead = true;
            notification.ReadAtUtc = now;
        }
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            new AuditWriteRequest(
                EntityType: "Notification",
                EntityId: "System",
                Action: "Notification.ReadAll",
                ActorUserId: _currentUser.GetRequiredUserId(),
                EntityDisplayName: "Все уведомления"),
            ct);
    }

    public async Task<Notification?> AddAsync(string userId, CreateNotificationCommand command, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(command.DeduplicationKey))
        {
            var exists = await _db.Notifications.AnyAsync(
                n => n.UserId == userId && n.DeduplicationKey == command.DeduplicationKey, ct);
            if (exists)
                return null;
        }

        var notification = CreateEntity(userId, command);
        _db.Notifications.Add(notification);
        return notification;
    }

    public async Task<Notification?> CreateFromWorkflowAsync(
        string recipientUserId,
        CreateNotificationCommand command,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var created = await AddAsync(recipientUserId, command, ct);
        if (created == null) return null;

        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "Notification",
                EntityId: created.Id.ToString(),
                Action: "Notification.Created",
                ActorUserId: actorUserId,
                EntityDisplayName: created.Title),
            ct);

        return created;
    }

    private static Notification CreateEntity(string userId, CreateNotificationCommand command) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        BranchId = command.BranchId,
        Type = command.Type,
        Title = command.Title,
        Message = command.Message,
        EntityType = command.EntityType,
        EntityId = command.EntityId,
        WorkTaskId = command.WorkTaskId,
        NavigationUrl = command.NavigationUrl,
        DeduplicationKey = command.DeduplicationKey,
        IsRead = false,
        CreatedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = command.ExpiresAtUtc,
    };

    private static IQueryable<Notification> ApplyFilters(IQueryable<Notification> query, NotificationQuery f)
    {
        if (f.UnreadOnly)
            query = query.Where(n => !n.IsRead);

        if (f.Type.HasValue)
            query = query.Where(n => n.Type == f.Type.Value);

        if (!string.IsNullOrEmpty(f.EntityType))
            query = query.Where(n => n.EntityType == f.EntityType);

        if (f.EntityId.HasValue)
            query = query.Where(n => n.EntityId == f.EntityId.Value);

        if (f.Days.HasValue && f.Days.Value > 0)
        {
            var from = DateTime.UtcNow.AddDays(-f.Days.Value);
            query = query.Where(n => n.CreatedAtUtc >= from);
        }

        return query;
    }
}
