using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class AuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db) => _db = db;

    public Task<List<AuditLog>> GetLogsAsync(int page = 1, int pageSize = 50, string? entityType = null, string? action = null, string? period = null) =>
        BuildFilteredQuery(entityType, action, period)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

    public async Task<List<AuditLogDisplayDto>> GetLogsWithEntityNamesAsync(int page = 1, int pageSize = 50, string? entityType = null, string? action = null, string? period = null)
    {
        var logs = await BuildFilteredQuery(entityType, action, period)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var names = new Dictionary<string, string>();

        foreach (var group in logs.GroupBy(l => l.EntityType))
        {
            var ids = group
                .Select(l => Guid.TryParse(l.EntityId, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();

            if (ids.Count == 0) continue;

            switch (group.Key)
            {
                case "HKCard":
                {
                    var items = await _db.HKCards
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Code + " v" + e.Version })
                        .ToListAsync();
                    foreach (var x in items) names["HKCard:" + x.Id] = x.Display;
                    break;
                }
                case "Node":
                {
                    var items = await _db.Nodes
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Code + " " + e.Name })
                        .ToListAsync();
                    foreach (var x in items) names["Node:" + x.Id] = x.Display;
                    break;
                }
                case "EquipmentModel":
                {
                    var items = await _db.EquipmentModels
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Index + " " + e.Name })
                        .ToListAsync();
                    foreach (var x in items) names["EquipmentModel:" + x.Id] = x.Display;
                    break;
                }
                case "EquipmentInstance":
                {
                    var items = await _db.EquipmentInstances
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.SerialNumber + " " + e.Index })
                        .ToListAsync();
                    foreach (var x in items) names["EquipmentInstance:" + x.Id] = x.Display;
                    break;
                }
                case "IndividualCard":
                {
                    var items = await _db.IndividualCards
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.HKCard.Code + " / " + e.EquipmentInstance.Name })
                        .ToListAsync();
                    foreach (var x in items) names["IndividualCard:" + x.Id] = x.Display;
                    break;
                }
                case "GsmMaterial":
                {
                    var items = await _db.GsmMaterials
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Name })
                        .ToListAsync();
                    foreach (var x in items) names["GsmMaterial:" + x.Id] = x.Display;
                    break;
                }
                case "AssemblyUnit":
                {
                    var items = await _db.AssemblyUnits
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Code + " " + e.Name })
                        .ToListAsync();
                    foreach (var x in items) names["AssemblyUnit:" + x.Id] = x.Display;
                    break;
                }
                case "Branch":
                {
                    var items = await _db.Branches
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Name })
                        .ToListAsync();
                    foreach (var x in items) names["Branch:" + x.Id] = x.Display;
                    break;
                }
                case "Coefficient":
                {
                    var items = await _db.Coefficients
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Name })
                        .ToListAsync();
                    foreach (var x in items) names["Coefficient:" + x.Id] = x.Display;
                    break;
                }
                case "CoefficientType":
                {
                    var items = await _db.CoefficientTypes
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Name })
                        .ToListAsync();
                    foreach (var x in items) names["CoefficientType:" + x.Id] = x.Display;
                    break;
                }
                case "WorkTask":
                {
                    var items = await _db.WorkTasks
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new { e.Id, Display = e.Title })
                        .ToListAsync();
                    foreach (var x in items) names["WorkTask:" + x.Id] = x.Display;
                    break;
                }
            }
        }

        return logs.Select(l => new AuditLogDisplayDto
        {
            Id = l.Id,
            EntityType = l.EntityType,
            EntityId = l.EntityId,
            EntityDisplayName = names.GetValueOrDefault(l.EntityType + ":" + l.EntityId, ""),
            Action = l.Action,
            UserId = l.UserId,
            Details = l.Details,
            CreatedAt = l.CreatedAt
        }).ToList();
    }

    public Task<int> GetTotalCountAsync(string? entityType = null, string? action = null, string? period = null) =>
        BuildFilteredQuery(entityType, action, period).CountAsync();

    private IQueryable<AuditLog> BuildFilteredQuery(string? entityType, string? action, string? period)
    {
        var query = _db.AuditLogs.AsQueryable();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(l => l.EntityType == entityType);

        if (!string.IsNullOrEmpty(action))
            query = query.Where(l => l.Action == action);

        if (!string.IsNullOrEmpty(period) && int.TryParse(period, out var days) && days > 0)
        {
            var from = DateTime.UtcNow.AddDays(-days);
            query = query.Where(l => l.CreatedAt >= from);
        }

        return query;
    }

    public async Task<AuditLog> LogAsync(string entityType, string entityId, string action, Guid userId, string? details = null)
    {
        var log = new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            UserId = userId,
            Details = details,
            CreatedAt = DateTime.UtcNow
        };
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync();
        return log;
    }

    public Task<List<AuditLog>> GetLogsByEntityAsync(string entityType, string entityId) =>
        _db.AuditLogs
            .Where(l => l.EntityType == entityType && l.EntityId == entityId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
}
