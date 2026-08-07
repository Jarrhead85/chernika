using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class AuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db) => _db = db;

    public async Task<AuditLog> LogAsync(AuditWriteRequest request, CancellationToken ct = default)
    {
        string? actorFullName = null;
        string? actorLogin = null;

        if (request.ActorUserId != Guid.Empty)
        {
            var actor = await _db.Users
                .Where(u => u.Id == request.ActorUserId.ToString())
                .Select(u => new { u.FullName, u.UserName })
                .FirstOrDefaultAsync(ct);

            actorFullName = actor?.FullName;
            actorLogin = actor?.UserName;
        }
        else
        {
            actorFullName = "Система";
            actorLogin = "system";
        }

        var log = new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            Action = request.Action,
            UserId = request.ActorUserId,
            Details = LimitLength(request.Details, 2000),
            CreatedAt = DateTime.UtcNow,
            EntityDisplayName = LimitLength(request.EntityDisplayName, 150),
            ActorFullName = LimitLength(actorFullName, 200),
            ActorLogin = LimitLength(actorLogin, 150),
            Source = request.Source ?? (request.ActorUserId == Guid.Empty ? AuditSource.System : AuditSource.User),
        };

        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(ct);
        return log;
    }

    public async Task<AuditLog> CreateLogAsync(AuditWriteRequest request, CancellationToken ct = default)
    {
        string? actorFullName = null;
        string? actorLogin = null;

        if (request.ActorUserId != Guid.Empty)
        {
            var actor = await _db.Users
                .Where(u => u.Id == request.ActorUserId.ToString())
                .Select(u => new { u.FullName, u.UserName })
                .FirstOrDefaultAsync(ct);

            actorFullName = actor?.FullName;
            actorLogin = actor?.UserName;
        }
        else
        {
            actorFullName = "Система";
            actorLogin = "system";
        }

        var log = new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            Action = request.Action,
            UserId = request.ActorUserId,
            Details = LimitLength(request.Details, 2000),
            CreatedAt = DateTime.UtcNow,
            EntityDisplayName = LimitLength(request.EntityDisplayName, 150),
            ActorFullName = LimitLength(actorFullName, 200),
            ActorLogin = LimitLength(actorLogin, 150),
            Source = request.Source ?? (request.ActorUserId == Guid.Empty ? AuditSource.System : AuditSource.User),
        };

        _db.AuditLogs.Add(log);
        return log;
    }

    [System.Obsolete("Use LogAsync(AuditWriteRequest) with EntityDisplayName")]
    public async Task<AuditLog> LogAsync(string entityType, string entityId, string action, Guid userId, string? details = null)
    {
        return await LogAsync(new AuditWriteRequest(entityType, entityId, action, userId, Details: details));
    }

    public Task<List<AuditLog>> GetLogsAsync(int page = 1, int pageSize = 50, string? entityType = null, string? action = null, string? period = null, string? source = null) =>
        BuildFilteredQuery(entityType, action, period, source)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

    public async Task<List<AuditLogDisplayDto>> GetLogsWithEntityNamesAsync(
        int page = 1, int pageSize = 50,
        string? entityType = null, string? action = null, string? period = null, string? source = null)
    {
        var query = BuildFilteredQuery(entityType, action, period, source);

        var totalCount = await query.CountAsync();

        var logs = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var userIds = logs
            .Where(l => l.UserId != Guid.Empty && string.IsNullOrEmpty(l.ActorFullName))
            .Select(l => l.UserId)
            .Distinct()
            .ToList();

        Dictionary<Guid, (string FullName, string UserName)> userMap = new();
        if (userIds.Count > 0)
        {
            var userIdStrs = userIds.Select(u => u.ToString()).ToList();
            var users = await _db.Users
                .Where(u => userIdStrs.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.UserName })
                .ToListAsync();

            foreach (var u in users)
            {
                if (Guid.TryParse(u.Id, out var gid))
                    userMap[gid] = (u.FullName ?? "", u.UserName ?? "");
            }
        }

        var missingEntityTypes = logs
            .Where(l => string.IsNullOrEmpty(l.EntityDisplayName) && Guid.TryParse(l.EntityId, out _))
            .Select(l => l.EntityType)
            .Distinct()
            .ToList();

        Dictionary<string, Dictionary<Guid, string>> entityNames = new();
        foreach (var et in missingEntityTypes)
        {
            var ids = logs
                .Where(l => l.EntityType == et && string.IsNullOrEmpty(l.EntityDisplayName) && Guid.TryParse(l.EntityId, out var g) && g != Guid.Empty)
                .Select(l => Guid.Parse(l.EntityId))
                .Distinct()
                .ToList();

            if (ids.Count == 0) continue;
            entityNames[et] = await ResolveEntityNames(et, ids);
        }

        return logs.Select(l =>
        {
            var actionDisplay = AuditDisplayCatalog.GetAction(l.Action);
            var entityDisplayName = l.EntityDisplayName;
            var isSnapshotMissing = false;

            if (string.IsNullOrEmpty(entityDisplayName))
            {
                if (Guid.TryParse(l.EntityId, out var eid) && entityNames.TryGetValue(l.EntityType, out var dict) && dict.TryGetValue(eid, out var resolved))
                    entityDisplayName = resolved;
                else if (l.EntityType == "SecurityRepair" || l.EntityId == "System")
                    entityDisplayName = "Система";
                else
                {
                    isSnapshotMissing = true;
                    entityDisplayName = l.EntityId;
                }
            }

            string actorFullName;
            string actorLogin;
            if (!string.IsNullOrEmpty(l.ActorFullName))
            {
                actorFullName = l.ActorFullName;
                actorLogin = l.ActorLogin ?? "";
            }
            else if (l.UserId == Guid.Empty)
            {
                actorFullName = "Система";
                actorLogin = "system";
            }
            else if (userMap.TryGetValue(l.UserId, out var user))
            {
                actorFullName = string.IsNullOrEmpty(user.FullName)
                    ? $"Не указано ФИО (логин: {user.UserName})"
                    : user.FullName;
                actorLogin = user.UserName;
            }
            else
            {
                actorFullName = "Исторический пользователь недоступен";
                actorLogin = "";
            }

            var detailsDisplay = FormatDetails(l.Action, l.Details);

            return new AuditLogDisplayDto
            {
                Id = l.Id,
                CreatedAt = l.CreatedAt,
                ActionCode = l.Action,
                ActionDisplay = actionDisplay.Title,
                ActionSeverity = actionDisplay.Severity,
                EntityTypeCode = l.EntityType,
                EntityTypeDisplay = AuditDisplayCatalog.GetEntityTypeDisplay(l.EntityType),
                EntityId = l.EntityId,
                EntityDisplayName = entityDisplayName,
                IsEntitySnapshotMissing = isSnapshotMissing,
                ActorFullName = actorFullName,
                ActorLogin = actorLogin,
                DetailsDisplay = detailsDisplay,
                Source = l.Source,
                SourceDisplay = l.Source == AuditSource.System ? "Система" : "Пользователь",
            };
        }).ToList();
    }

    public Task<int> GetTotalCountAsync(
        string? entityType = null, string? action = null, string? period = null, string? source = null) =>
        BuildFilteredQuery(entityType, action, period, source).CountAsync();

    public Task<List<AuditLog>> GetLogsByEntityAsync(string entityType, string entityId) =>
        _db.AuditLogs
            .Where(l => l.EntityType == entityType && l.EntityId == entityId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    private IQueryable<AuditLog> BuildFilteredQuery(string? entityType, string? action, string? period, string? source)
    {
        var query = _db.AuditLogs.AsQueryable();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(l => l.EntityType == entityType);

        if (!string.IsNullOrEmpty(action))
        {
            if (action == "StatusChange")
                query = query.Where(l => l.Action.StartsWith("Status:"));
            else
            {
                var actions = AuditDisplayCatalog.GetFilterActions(action);
                query = query.Where(l => actions.Contains(l.Action));
            }
        }

        if (!string.IsNullOrEmpty(period) && int.TryParse(period, out var days) && days > 0)
        {
            var from = DateTime.UtcNow.AddDays(-days);
            query = query.Where(l => l.CreatedAt >= from);
        }

        if (!string.IsNullOrEmpty(source))
        {
            var sourceValue = source == "System" ? AuditSource.System : AuditSource.User;
            query = query.Where(l => l.Source == sourceValue);
        }

        return query;
    }

    private async Task<Dictionary<Guid, string>> ResolveEntityNames(string entityType, List<Guid> ids)
    {
        var result = new Dictionary<Guid, string>();

        switch (entityType)
        {
            case "HKCard":
                var hk = await _db.HKCards.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Code + " v" + e.Version }).ToListAsync();
                foreach (var x in hk) result[x.Id] = x.Display;
                break;
            case "Node":
                var nodes = await _db.Nodes.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Code + " " + e.Name }).ToListAsync();
                foreach (var x in nodes) result[x.Id] = x.Display;
                break;
            case "EquipmentModel":
                var em = await _db.EquipmentModels.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Index + " " + e.Name }).ToListAsync();
                foreach (var x in em) result[x.Id] = x.Display;
                break;
            case "EquipmentInstance":
                var ei = await _db.EquipmentInstances.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.SerialNumber + " " + e.Index }).ToListAsync();
                foreach (var x in ei) result[x.Id] = x.Display;
                break;
            case "IndividualCard":
                var ic = await _db.IndividualCards.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.HKCard.Code + " / " + e.EquipmentInstance.Name }).ToListAsync();
                foreach (var x in ic) result[x.Id] = x.Display;
                break;
            case "GsmMaterial":
                var gm = await _db.GsmMaterials.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Name }).ToListAsync();
                foreach (var x in gm) result[x.Id] = x.Display;
                break;
            case "AssemblyUnit":
                var au = await _db.AssemblyUnits.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Code + " " + e.Name }).ToListAsync();
                foreach (var x in au) result[x.Id] = x.Display;
                break;
            case "Branch":
                var br = await _db.Branches.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Name }).ToListAsync();
                foreach (var x in br) result[x.Id] = x.Display;
                break;
            case "Coefficient":
                var co = await _db.Coefficients.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Name }).ToListAsync();
                foreach (var x in co) result[x.Id] = x.Display;
                break;
            case "CoefficientType":
                var ct = await _db.CoefficientTypes.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Name }).ToListAsync();
                foreach (var x in ct) result[x.Id] = x.Display;
                break;
            case "WorkTask":
                var wt = await _db.WorkTasks.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Title }).ToListAsync();
                foreach (var x in wt) result[x.Id] = x.Display;
                break;
            case "Aggregate":
                var ag = await _db.Aggregates.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Code + " " + e.Name }).ToListAsync();
                foreach (var x in ag) result[x.Id] = x.Display;
                break;
            case "Complex":
                var cx = await _db.Complexes.Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, Display = e.Code + " " + e.Name }).ToListAsync();
                foreach (var x in cx) result[x.Id] = x.Display;
                break;
        }

        return result;
    }

    private static string FormatDetails(string action, string? details)
    {
        if (string.IsNullOrEmpty(details))
            return "—";

        if (action.StartsWith("Status:"))
        {
            var status = AuditDisplayCatalog.TranslateStatus(action["Status:".Length..]);
            return $"Статус изменён: {status}";
        }

        return details.Length > 120 ? details[..120] + "…" : details;
    }

    private static string? LimitLength(string? value, int maxLength) =>
        value != null && value.Length > maxLength ? value[..maxLength] : value;
}
