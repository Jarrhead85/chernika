using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class TaskService
{
    private readonly AppDbContext _db;

    public TaskService(AppDbContext db) => _db = db;

    public Task<List<WorkTask>> GetActiveTasksAsync(string? assigneeId = null)
    {
        var query = _db.WorkTasks.Where(t => !t.IsCompleted);
        if (assigneeId != null)
            query = query.Where(t => t.AssigneeId == assigneeId);
        return query.OrderBy(t => t.DueDate).ThenByDescending(t => t.CreatedAt).ToListAsync();
    }

    public Task<List<WorkTask>> GetCompletedTasksAsync(string? assigneeId = null, int limit = 20)
    {
        var query = _db.WorkTasks.Where(t => t.IsCompleted);
        if (assigneeId != null)
            query = query.Where(t => t.AssigneeId == assigneeId);
        return query.OrderByDescending(t => t.CompletedAt).Take(limit).ToListAsync();
    }

    public async Task<WorkTask> CreateTaskAsync(string title, string assigneeId, string? description = null, string? entityType = null, string? entityId = null, DateTime? dueDate = null)
    {
        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = title,
            AssigneeId = assigneeId,
            Description = description,
            EntityType = entityType,
            EntityId = entityId,
            DueDate = dueDate,
            CreatedAt = DateTime.UtcNow
        };
        _db.WorkTasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    public async Task<bool> CompleteTaskAsync(Guid id)
    {
        var task = await _db.WorkTasks.FindAsync(id);
        if (task == null) return false;
        task.IsCompleted = true;
        task.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteTaskAsync(Guid id)
    {
        var task = await _db.WorkTasks.FindAsync(id);
        if (task == null) return false;
        _db.WorkTasks.Remove(task);
        await _db.SaveChangesAsync();
        return true;
    }

    public Task<int> GetActiveCountAsync(string? assigneeId = null)
    {
        var query = _db.WorkTasks.Where(t => !t.IsCompleted);
        if (assigneeId != null)
            query = query.Where(t => t.AssigneeId == assigneeId);
        return query.CountAsync();
    }
}
