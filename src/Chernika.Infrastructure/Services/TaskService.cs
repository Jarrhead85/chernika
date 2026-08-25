using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class TaskService
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICurrentUserService _currentUser;
    private readonly IPermissionService _permissions;
    private readonly AuditService _audit;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _time;

    public TaskService(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        AuditService audit,
        NotificationService notifications,
        TimeProvider time)
    {
        _db = db;
        _userManager = userManager;
        _currentUser = currentUser;
        _permissions = permissions;
        _audit = audit;
        _notifications = notifications;
        _time = time;
    }

    public async Task<WorkTaskDto> CreateAsync(CreateWorkTaskCommand command, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskAssign, ct);

        if (string.IsNullOrWhiteSpace(command.AssignedToUserId) && string.IsNullOrWhiteSpace(command.AssignedRole))
            throw new ArgumentException("Задача должна быть назначена пользователю или роли.");

        var safeBranchId = await GetAccessibleBranchIdAsync(command.BranchId, ct);

        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = command.Title.Trim(),
            Description = command.Description,
            Type = command.Type,
            Status = WorkTaskStatus.Open,
            Priority = command.Priority,
            CreatedByUserId = actorId.ToString(),
            AssignedToUserId = command.AssignedToUserId,
            AssignedRole = command.AssignedRole,
            BranchId = safeBranchId ?? command.BranchId,
            EntityType = command.EntityType,
            EntityId = command.EntityId,
            EntityCodeSnapshot = command.EntityCodeSnapshot,
            EntityTitleSnapshot = command.EntityTitleSnapshot,
            CreatedAtUtc = DateTime.UtcNow,
            DueDateUtc = command.DueDateUtc,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.WorkTasks.Add(task);
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "WorkTask",
                EntityId: task.Id.ToString(),
                Action: "Task.Created",
                ActorUserId: actorId,
                EntityDisplayName: task.Title),
            ct);

        if (command.AssignedToUserId != null && command.NotifyAssignee)
        {
            await _notifications.CreateFromWorkflowAsync(command.AssignedToUserId, new CreateNotificationCommand(
                Type: NotificationType.TaskAssigned,
                Title: $"Назначена задача: {task.Title}",
                Message: command.Description,
                EntityType: task.EntityType,
                EntityId: task.EntityId,
                WorkTaskId: task.Id,
                NavigationUrl: $"/задачи/{task.Id}",
                BranchId: task.BranchId,
                DeduplicationKey: $"task-assigned:{task.Id}:{command.AssignedToUserId}"), actorId, ct);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await MapToDtoAsync(task, ct);
    }

    public async Task<WorkTask> CreateFromWorkflowAsync(
        CreateWorkflowTaskCommand command, Guid? actorUserId = null, CancellationToken ct = default)
    {
        var actorId = actorUserId ?? _currentUser.GetRequiredUserId();

        if (string.IsNullOrWhiteSpace(command.AssignedToUserId))
            throw new ArgumentException("Workflow-задача должна быть назначена пользователю.");

        if (command.EntityType != null && command.EntityId.HasValue)
        {
            var duplicate = await _db.WorkTasks.AnyAsync(t =>
                !t.IsDeleted
                && t.Type == command.Type
                && t.EntityType == command.EntityType
                && t.EntityId == command.EntityId
                && (t.Status == WorkTaskStatus.Open
                    || t.Status == WorkTaskStatus.InProgress
                    || t.Status == WorkTaskStatus.Overdue), ct);
            if (duplicate)
                return (await _db.WorkTasks.FirstOrDefaultAsync(t =>
                    !t.IsDeleted
                    && t.Type == command.Type
                    && t.EntityType == command.EntityType
                    && t.EntityId == command.EntityId
                    && (t.Status == WorkTaskStatus.Open
                        || t.Status == WorkTaskStatus.InProgress
                        || t.Status == WorkTaskStatus.Overdue), ct))!;
        }

        var now = DateTime.UtcNow;
        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = command.Title.Trim(),
            Description = command.Description,
            Type = command.Type,
            Status = WorkTaskStatus.Open,
            Priority = command.Priority,
            CreatedByUserId = actorId == Guid.Empty ? null : actorId.ToString(),
            AssignedToUserId = command.AssignedToUserId,
            BranchId = command.BranchId,
            EntityType = command.EntityType,
            EntityId = command.EntityId,
            EntityCodeSnapshot = command.EntityCodeSnapshot,
            EntityTitleSnapshot = command.EntityTitleSnapshot,
            CreatedAtUtc = now,
            DueDateUtc = command.DueDateUtc,
            UpdatedAtUtc = now,
        };

        _db.WorkTasks.Add(task);
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "WorkTask",
                EntityId: task.Id.ToString(),
                Action: "Task.Created",
                ActorUserId: actorId,
                EntityDisplayName: task.Title),
            ct);

        if (command.NotifyAssignee)
        {
            await _notifications.CreateFromWorkflowAsync(command.AssignedToUserId, new CreateNotificationCommand(
                Type: NotificationType.TaskAssigned,
                Title: $"Назначена задача: {task.Title}",
                Message: command.Description,
                EntityType: task.EntityType,
                EntityId: task.EntityId,
                WorkTaskId: task.Id,
                NavigationUrl: $"/задачи/{task.Id}",
                BranchId: task.BranchId,
                DeduplicationKey: $"task-assigned:{task.Id}:{command.AssignedToUserId}"), actorId, ct);
        }

        return task;
    }

    public async Task<WorkTaskDto?> GetByIdAsync(Guid taskId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskView, ct);
        var safeBranchId = await GetAccessibleBranchIdAsync(null, ct);

        var baseQuery = _db.WorkTasks.AsNoTracking().Where(t => t.Id == taskId && !t.IsDeleted);
        if (safeBranchId.HasValue)
            baseQuery = baseQuery.Where(t => t.BranchId == safeBranchId.Value);
        baseQuery = await ApplyScopeAsync(baseQuery, actorId, safeBranchId, ct);

        var task = await baseQuery.FirstOrDefaultAsync(ct);
        return task == null ? null : await MapToDtoAsync(task, ct);
    }

    public async Task<WorkTaskDto> AssignAsync(AssignWorkTaskCommand command, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskAssign, ct);

        if (string.IsNullOrWhiteSpace(command.AssignedToUserId) && string.IsNullOrWhiteSpace(command.AssignedRole))
            throw new ArgumentException("Задача должна быть назначена пользователю или роли.");

        var task = await GetMutableTaskAsync(command.TaskId, ct);
        await GetAccessibleBranchIdAsync(task.BranchId, ct);

        task.AssignedToUserId = command.AssignedToUserId;
        task.AssignedRole = command.AssignedRole;
        task.UpdatedAtUtc = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "WorkTask",
                EntityId: task.Id.ToString(),
                Action: "Task.Assigned",
                ActorUserId: actorId,
                EntityDisplayName: task.Title,
                Details: string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment),
            ct);

        if (command.AssignedToUserId != null)
        {
            await _notifications.CreateFromWorkflowAsync(command.AssignedToUserId, new CreateNotificationCommand(
                Type: NotificationType.TaskAssigned,
                Title: $"Назначена задача: {task.Title}",
                Message: command.Comment,
                EntityType: task.EntityType,
                EntityId: task.EntityId,
                WorkTaskId: task.Id,
                NavigationUrl: $"/задачи/{task.Id}",
                BranchId: task.BranchId,
                DeduplicationKey: $"task-assigned:{task.Id}:{command.AssignedToUserId}"), actorId, ct);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await MapToDtoAsync(task, ct);
    }

    public async Task<WorkTaskDto> StartAsync(Guid taskId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskComplete, ct);

        var task = await GetMutableTaskAsync(taskId, ct);
        await EnsureAssigneeOrAdminAsync(task, actorId, ct);
        await GetAccessibleBranchIdAsync(task.BranchId, ct);

        if (WorkTaskTransitions.CanStart(task.Status))
        {
            task.Status = WorkTaskStatus.InProgress;
            task.StartedAtUtc = DateTime.UtcNow;
            task.UpdatedAtUtc = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _audit.CreateLogAsync(
                new AuditWriteRequest(
                    EntityType: "WorkTask",
                    EntityId: task.Id.ToString(),
                    Action: "Task.Started",
                    ActorUserId: actorId,
                    EntityDisplayName: task.Title),
                ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return await MapToDtoAsync(task, ct);
    }

    public async Task<WorkTaskDto> CompleteAsync(CompleteWorkTaskCommand command, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskComplete, ct);

        var task = await GetMutableTaskAsync(command.TaskId, ct);
        await EnsureAssigneeOrAdminAsync(task, actorId, ct);
        await GetAccessibleBranchIdAsync(task.BranchId, ct);

        if ((task.Type == WorkTaskType.HKRevision || task.Type == WorkTaskType.ReferenceProposalReview)
            && string.IsNullOrWhiteSpace(command.CompletionComment))
            throw new ArgumentException("Для этого типа задачи требуется комментарий выполнения.");

        task.Status = WorkTaskStatus.Completed;
        task.CompletedAtUtc = DateTime.UtcNow;
        task.CompletedByUserId = actorId.ToString();
        task.CompletionComment = command.CompletionComment;
        task.UpdatedAtUtc = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "WorkTask",
                EntityId: task.Id.ToString(),
                Action: "Task.Completed",
                ActorUserId: actorId,
                EntityDisplayName: task.Title,
                Details: command.CompletionComment),
            ct);

        if (!string.IsNullOrEmpty(task.CreatedByUserId) && task.CreatedByUserId != actorId.ToString())
        {
            await _notifications.CreateFromWorkflowAsync(task.CreatedByUserId, new CreateNotificationCommand(
                Type: NotificationType.TaskCompleted,
                Title: $"Задача выполнена: {task.Title}",
                Message: command.CompletionComment,
                EntityType: task.EntityType,
                EntityId: task.EntityId,
                WorkTaskId: task.Id,
                NavigationUrl: $"/задачи/{task.Id}",
                BranchId: task.BranchId,
                DeduplicationKey: $"task-completed:{task.Id}:{task.CreatedByUserId}"), actorId, ct);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await MapToDtoAsync(task, ct);
    }

    public async Task<WorkTaskDto> CancelAsync(CancelWorkTaskCommand command, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskCancel, ct);

        var task = await GetMutableTaskAsync(command.TaskId, ct);
        await GetAccessibleBranchIdAsync(task.BranchId, ct);

        task.Status = WorkTaskStatus.Cancelled;
        task.UpdatedAtUtc = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _audit.CreateLogAsync(
            new AuditWriteRequest(
                EntityType: "WorkTask",
                EntityId: task.Id.ToString(),
                Action: "Task.Cancelled",
                ActorUserId: actorId,
                EntityDisplayName: task.Title,
                Details: command.Reason),
            ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await MapToDtoAsync(task, ct);
    }

    public async Task<PagedResult<WorkTaskListItemDto>> GetMyTasksAsync(WorkTaskQuery query, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskView, ct);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var safeBranchId = await GetAccessibleBranchIdAsync(query.BranchId, ct);

        var baseQuery = _db.WorkTasks.AsNoTracking().Where(t => !t.IsDeleted);
        baseQuery = await ApplyScopeAsync(baseQuery, actorId, safeBranchId, ct);
        baseQuery = ApplyFilters(baseQuery, query);

        var total = await baseQuery.CountAsync(ct);
        var tasks = await ApplySorting(baseQuery, query.SortBy, query.SortDescending)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var names = await GetUserNamesAsync(tasks.Select(t => t.AssignedToUserId).Concat(tasks.Select(t => t.CompletedByUserId)), ct);
        var now = DateTime.UtcNow;

        return new PagedResult<WorkTaskListItemDto>
        {
            Items = tasks.Select(t => new WorkTaskListItemDto
            {
                Id = t.Id,
                Title = t.Title,
                Description = t.Description,
                Type = t.Type,
                Status = t.Status,
                Priority = t.Priority,
                AssignedToUserId = t.AssignedToUserId,
                AssignedToUserName = t.AssignedToUserId != null ? names.GetValueOrDefault(t.AssignedToUserId) : null,
                AssignedRole = t.AssignedRole,
                BranchId = t.BranchId,
                EntityType = t.EntityType,
                EntityId = t.EntityId,
                EntityCodeSnapshot = t.EntityCodeSnapshot,
                EntityTitleSnapshot = t.EntityTitleSnapshot,
                WorkTaskGroupId = t.WorkTaskGroupId,
                CreatedAtUtc = t.CreatedAtUtc,
                DueDateUtc = t.DueDateUtc,
                CompletedAtUtc = t.CompletedAtUtc,
                CompletedByUserId = t.CompletedByUserId,
                CompletedByUserName = t.CompletedByUserId != null ? names.GetValueOrDefault(t.CompletedByUserId) : null,
                IsOverdue = t.DueDateUtc.HasValue && t.DueDateUtc < now
                    && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress),
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<int> GetOpenTaskCountAsync(CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId().ToString();
        var safeBranchId = await GetAccessibleBranchIdAsync(null, ct);

        var query = _db.WorkTasks.AsNoTracking()
            .Where(t => !t.IsDeleted
                && t.AssignedToUserId == actorId
                && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue));
        if (safeBranchId.HasValue)
            query = query.Where(t => t.BranchId == safeBranchId.Value);

        return await query.CountAsync(ct);
    }

    public async Task<Dictionary<WorkTaskStatus, int>> GetStatusCountsAsync(CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskView, ct);
        var safeBranchId = await GetAccessibleBranchIdAsync(null, ct);

        var baseQuery = _db.WorkTasks.AsNoTracking().Where(t => !t.IsDeleted);
        if (safeBranchId.HasValue)
            baseQuery = baseQuery.Where(t => t.BranchId == safeBranchId.Value);
        baseQuery = await ApplyScopeAsync(baseQuery, actorId, safeBranchId, ct);

        var groups = await baseQuery
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return groups.ToDictionary(g => g.Status, g => g.Count);
    }

    public async Task<WorkTaskGroupResult> CreateCompositionReviewGroupAsync(
        CreateCompositionReviewGroupCommand command,
        IReadOnlyCollection<string> normAdminUserIds,
        CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var taskTypeName = nameof(WorkTaskType.CompositionReview);

        var existingGroup = await _db.WorkTaskGroups
            .Include(g => g.WorkTasks)
            .FirstOrDefaultAsync(g =>
                g.TaskType == taskTypeName
                && g.EntityType == command.EntityType
                && g.EntityId == command.EntityId
                && g.BranchId == command.BranchId
                && g.CompletedAt == null, ct);

        if (existingGroup != null)
            return new WorkTaskGroupResult(existingGroup.Id,
                existingGroup.WorkTasks.Select(t => t.Id).ToList(), CreatedNew: false);

        var now = _time.GetUtcNow().UtcDateTime;
        var group = new WorkTaskGroup
        {
            Id = Guid.NewGuid(),
            TaskType = taskTypeName,
            EntityType = command.EntityType,
            EntityId = command.EntityId,
            BranchId = command.BranchId,
            Title = command.Title,
            Description = command.Description,
            CreatedAt = now
        };
        _db.WorkTaskGroups.Add(group);

        var taskIds = new List<Guid>();
        foreach (var userId in normAdminUserIds)
        {
            var task = new WorkTask
            {
                Id = Guid.NewGuid(),
                Title = command.Title,
                Description = command.Description,
                Type = WorkTaskType.CompositionReview,
                Status = WorkTaskStatus.Open,
                Priority = WorkTaskPriority.Normal,
                CreatedByUserId = actorId.ToString(),
                AssignedToUserId = userId,
                BranchId = command.BranchId,
                EntityType = command.EntityType,
                EntityId = command.EntityId,
                EntityCodeSnapshot = command.Code,
                EntityTitleSnapshot = command.Version,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                WorkTaskGroupId = group.Id
            };
            _db.WorkTasks.Add(task);
            taskIds.Add(task.Id);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _audit.CreateLogAsync(
            new AuditWriteRequest("WorkTaskGroup", group.Id.ToString(), "WorkTaskGroup.Created",
                actorId, EntityDisplayName: group.Title), ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new WorkTaskGroupResult(group.Id, taskIds, CreatedNew: true);
    }

    public async Task<WorkTask> CreateCompositionReviewFallbackTaskAsync(
        CreateCompositionReviewGroupCommand command,
        CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var now = _time.GetUtcNow().UtcDateTime;

        var existing = await _db.WorkTasks.FirstOrDefaultAsync(t =>
            !t.IsDeleted
            && t.Type == WorkTaskType.CompositionReview
            && t.AssignedRole == nameof(UserRole.SystemAdmin)
            && t.EntityType == command.EntityType
            && t.EntityId == command.EntityId
            && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue), ct);

        if (existing != null)
            return existing;

        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = command.Title,
            Description = command.Description,
            Type = WorkTaskType.CompositionReview,
            Status = WorkTaskStatus.Open,
            Priority = WorkTaskPriority.High,
            CreatedByUserId = actorId.ToString(),
            AssignedRole = nameof(UserRole.SystemAdmin),
            BranchId = command.BranchId,
            EntityType = command.EntityType,
            EntityId = command.EntityId,
            EntityCodeSnapshot = command.Code,
            EntityTitleSnapshot = command.Version,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.WorkTasks.Add(task);
        await _audit.CreateLogAsync(
            new AuditWriteRequest("WorkTask", task.Id.ToString(), "Task.Created",
                actorId, EntityDisplayName: task.Title), ct);
        await _db.SaveChangesAsync(ct);

        return task;
    }

    public async Task<WorkTaskGroupResult> CreateCompositionReadinessGroupAsync(
        CreateCompositionReadinessGroupCommand command,
        IReadOnlyCollection<string> normAdminUserIds,
        CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var taskTypeName = nameof(WorkTaskType.CompositionReadiness);

        var existingGroup = await _db.WorkTaskGroups
            .Include(g => g.WorkTasks)
            .FirstOrDefaultAsync(g =>
                g.TaskType == taskTypeName
                && g.EntityType == command.EntityType
                && g.EntityId == command.EntityId
                && g.BranchId == command.BranchId
                && g.Description == command.ProblemKey
                && g.CompletedAt == null, ct);

        if (existingGroup != null)
            return new WorkTaskGroupResult(existingGroup.Id,
                existingGroup.WorkTasks.Select(t => t.Id).ToList(), CreatedNew: false);

        var now = _time.GetUtcNow().UtcDateTime;
        var title = $"Актуализировать ХК: {command.Code} — {command.StatusLabel}";
        var group = new WorkTaskGroup
        {
            Id = Guid.NewGuid(),
            TaskType = taskTypeName,
            EntityType = command.EntityType,
            EntityId = command.EntityId,
            BranchId = command.BranchId,
            Title = title,
            Description = command.ProblemKey,
            CreatedAt = now
        };
        _db.WorkTaskGroups.Add(group);

        var taskIds = new List<Guid>();
        var taskDescription = command.Description ?? $"Нормативная готовность состава: {command.StatusLabel}.";
        foreach (var userId in normAdminUserIds)
        {
            var task = new WorkTask
            {
                Id = Guid.NewGuid(),
                Title = title,
                Description = taskDescription,
                Type = WorkTaskType.CompositionReadiness,
                Status = WorkTaskStatus.Open,
                Priority = WorkTaskPriority.Normal,
                CreatedByUserId = actorId.ToString(),
                AssignedToUserId = userId,
                BranchId = command.BranchId,
                EntityType = command.EntityType,
                EntityId = command.EntityId,
                EntityCodeSnapshot = command.Code,
                EntityTitleSnapshot = command.Name,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                WorkTaskGroupId = group.Id
            };
            _db.WorkTasks.Add(task);
            taskIds.Add(task.Id);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _audit.CreateLogAsync(
            new AuditWriteRequest("WorkTaskGroup", group.Id.ToString(), "WorkTaskGroup.Created",
                actorId, EntityDisplayName: group.Title), ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new WorkTaskGroupResult(group.Id, taskIds, CreatedNew: true);
    }

    public async Task<WorkTask> CreateCompositionAuthorTaskAsync(
        CreateCompositionAuthorTaskCommand command,
        CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        var existing = await _db.WorkTasks
            .FirstOrDefaultAsync(t =>
                !t.IsDeleted
                && t.Type == WorkTaskType.CompositionReview
                && t.EntityType == command.EntityType
                && t.EntityId == command.EntityId
                && t.AssignedToUserId == command.AuthorId
                && (t.Status == WorkTaskStatus.Open
                    || t.Status == WorkTaskStatus.InProgress
                    || t.Status == WorkTaskStatus.Overdue), ct);

        if (existing != null)
        {
            existing.Title = command.Title;
            existing.Description = command.Description;
            existing.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = command.Title,
            Description = command.Description,
            Type = WorkTaskType.CompositionReview,
            Status = WorkTaskStatus.Open,
            Priority = WorkTaskPriority.Normal,
            CreatedByUserId = actorId.ToString(),
            AssignedToUserId = command.AuthorId,
            BranchId = command.BranchId,
            EntityType = command.EntityType,
            EntityId = command.EntityId,
            EntityCodeSnapshot = command.Code,
            EntityTitleSnapshot = command.Version,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.WorkTasks.Add(task);
        await _audit.CreateLogAsync(
            new AuditWriteRequest("WorkTask", task.Id.ToString(), "Task.Created",
                actorId, EntityDisplayName: task.Title), ct);
        await _db.SaveChangesAsync(ct);

        return task;
    }

    public async Task<GroupCompletionResult> CompleteGroupAsync(
        Guid taskId,
        string actorUserId,
        string? comment = null,
        CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.TaskComplete, ct);

        var actorId = actorUserId;
        var task = await _db.WorkTasks
            .Include(t => t.WorkTaskGroup)
            .ThenInclude(g => g.WorkTasks)
            .FirstOrDefaultAsync(t => t.Id == taskId && !t.IsDeleted, ct);

        if (task?.WorkTaskGroup == null)
            throw new InvalidOperationException("Задача не является групповой.");

        var group = task.WorkTaskGroup;
        if (group.CompletedAt.HasValue)
        {
            var names = await GetUserNamesAsync(new[] { group.CompletedByUserId }, ct);
            var completerName = names.GetValueOrDefault(group.CompletedByUserId) ?? group.CompletedByUserId ?? "другой пользователь";
            return new GroupCompletionResult(true, group.CompletedByUserId, completerName, AlreadyCompleted: true,
                $"Задача уже выполнена пользователем {completerName}.");
        }

        var actorIsSystemAdmin = await _permissions.HasPermissionAsync(actorId, PermissionCodes.SystemConfig);
        var isGroupMember = group.WorkTasks.Any(t =>
            t.AssignedToUserId == actorId
            && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue));

        if (!isGroupMember && !actorIsSystemAdmin)
            throw new UnauthorizedAccessException("Завершить групповую задачу может только назначенный исполнитель.");

        var now = _time.GetUtcNow().UtcDateTime;
        group.CompletedAt = now;
        group.CompletedByUserId = actorId;

        foreach (var t in group.WorkTasks.Where(t =>
            t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue))
        {
            t.Status = WorkTaskStatus.Completed;
            t.CompletedAtUtc = now;
            t.CompletedByUserId = actorId;
            t.CompletionComment = comment;
            t.UpdatedAtUtc = now;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await _audit.CreateLogAsync(
                new AuditWriteRequest("WorkTaskGroup", group.Id.ToString(), "WorkTaskGroup.Completed",
                    Guid.Parse(actorId), EntityDisplayName: group.Title, Details: comment),
                ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            var existing = await _db.WorkTaskGroups.AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == group.Id, ct);
            if (existing?.CompletedAt != null)
            {
                var completerName = (await GetUserNamesAsync(new[] { existing.CompletedByUserId }, ct))
                    .GetValueOrDefault(existing.CompletedByUserId) ?? existing.CompletedByUserId ?? "другой пользователь";
                return new GroupCompletionResult(true, existing.CompletedByUserId, completerName, AlreadyCompleted: true,
                    $"Задача уже выполнена пользователем {completerName}.");
            }
            throw;
        }

        var actorName = (await GetUserNamesAsync(new[] { actorId }, ct)).GetValueOrDefault(actorId) ?? actorId;
        return new GroupCompletionResult(true, actorId, actorName, AlreadyCompleted: false,
            $"Задача выполнена пользователем {actorName}.");
    }

    public async Task ProcessOverdueTasksAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var overdueIds = await _db.WorkTasks
            .Where(t => !t.IsDeleted
                && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress)
                && t.DueDateUtc != null && t.DueDateUtc < now)
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (overdueIds.Count == 0)
            return;

        var idSet = overdueIds.ToHashSet();
        var tasks = await _db.WorkTasks.Where(t => idSet.Contains(t.Id)).ToListAsync(ct);
        foreach (var task in tasks)
        {
            task.Status = WorkTaskStatus.Overdue;
            task.UpdatedAtUtc = now;
            await _audit.CreateLogAsync(
                new AuditWriteRequest(
                    EntityType: "WorkTask",
                    EntityId: task.Id.ToString(),
                    Action: "Task.Overdue",
                    ActorUserId: Guid.Empty,
                    EntityDisplayName: task.Title),
                ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<WorkTask> GetMutableTaskAsync(Guid taskId, CancellationToken ct)
    {
        var task = await _db.WorkTasks.FirstOrDefaultAsync(t => t.Id == taskId && !t.IsDeleted, ct);
        if (task == null)
            throw new InvalidOperationException("Задача не найдена.");

        if (WorkTaskTransitions.IsTerminal(task.Status))
            throw new InvalidOperationException("Нельзя изменить закрытую или отменённую задачу.");

        return task;
    }

    private async Task EnsureAssigneeOrAdminAsync(WorkTask task, Guid actorId, CancellationToken ct)
    {
        var actorIdStr = actorId.ToString();
        if (task.AssignedToUserId == actorIdStr)
            return;

        if (await _permissions.HasPermissionAsync(actorIdStr, PermissionCodes.SystemConfig))
            return;

        throw new UnauthorizedAccessException("Завершить задачу может только назначенный исполнитель.");
    }

    private async Task<Guid?> GetAccessibleBranchIdAsync(Guid? requestedBranchId, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();
        if (await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig))
            return requestedBranchId;

        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor is null)
            throw new UnauthorizedAccessException("Пользователь не найден.");

        if (actor.BranchId is null || actor.BranchId == Guid.Empty)
            throw new UnauthorizedAccessException("У пользователя не указан филиал.");

        if (requestedBranchId.HasValue && requestedBranchId != actor.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к данным другого филиала.");

        return actor.BranchId;
    }

    private async Task<IQueryable<WorkTask>> ApplyScopeAsync(
        IQueryable<WorkTask> query, Guid actorId, Guid? branchId, CancellationToken ct)
    {
        if (branchId.HasValue)
            query = query.Where(t => t.BranchId == branchId.Value);

        var actorIdStr = actorId.ToString();
        if (await _permissions.HasPermissionAsync(actorIdStr, PermissionCodes.SystemConfig))
            return query;

        var user = await _userManager.FindByIdAsync(actorIdStr);
        if (user == null)
            throw new UnauthorizedAccessException("Пользователь не найден.");

        var roles = await _userManager.GetRolesAsync(user);
        var baseRole = roles.FirstOrDefault(r =>
            r == nameof(UserRole.SystemAdmin) || r == nameof(UserRole.NormAdmin) ||
            r == nameof(UserRole.Operator) || r == nameof(UserRole.HeadOfDepartment) ||
            r == nameof(UserRole.Guest));

        query = query.Where(t => t.AssignedToUserId == actorIdStr
            || (baseRole != null && t.AssignedRole == baseRole));

        if (await _permissions.HasPermissionAsync(actorIdStr, PermissionCodes.TaskAssign))
            query = query.Where(t => t.AssignedToUserId == actorIdStr
                || (baseRole != null && t.AssignedRole == baseRole)
                || t.CreatedByUserId == actorIdStr);

        return query;
    }

    private static IQueryable<WorkTask> ApplySorting(IQueryable<WorkTask> query, string? sortBy, bool descending)
    {
        IOrderedQueryable<WorkTask> ordered = (sortBy ?? "").ToLowerInvariant() switch
        {
            "priority" => query.OrderBy(t => t.Priority).ThenBy(t => t.DueDateUtc),
            "created" => query.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.DueDateUtc),
            "status" => query.OrderBy(t => t.Status).ThenBy(t => t.DueDateUtc),
            _ => query.OrderBy(t => t.DueDateUtc).ThenByDescending(t => t.CreatedAtUtc),
        };
        return descending ? ordered.Reverse() : ordered;
    }

    private static IQueryable<WorkTask> ApplyFilters(IQueryable<WorkTask> query, WorkTaskQuery f)
    {
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var text = f.Text.Trim();
            query = query.Where(t => t.Title.Contains(text) || (t.Description != null && t.Description.Contains(text)));
        }

        if (f.Status.HasValue)
            query = query.Where(t => t.Status == f.Status.Value);

        if (f.ActiveOnly)
            query = query.Where(t => t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled);

        if (f.Type.HasValue)
            query = query.Where(t => t.Type == f.Type.Value);

        if (f.Priority.HasValue)
            query = query.Where(t => t.Priority == f.Priority.Value);

        if (!string.IsNullOrEmpty(f.EntityType))
            query = query.Where(t => t.EntityType == f.EntityType);

        if (f.EntityId.HasValue)
            query = query.Where(t => t.EntityId == f.EntityId.Value);

        if (f.CompletedWithinDays.HasValue && f.CompletedWithinDays.Value > 0)
        {
            var from = DateTime.UtcNow.AddDays(-f.CompletedWithinDays.Value);
            query = query.Where(t => t.CompletedAtUtc != null && t.CompletedAtUtc >= from);
        }

        var now = DateTime.UtcNow;
        switch (f.DueFilter)
        {
            case WorkTaskDueFilter.DueToday:
            {
                var start = now.Date;
                var end = start.AddDays(1);
                query = query.Where(t => t.DueDateUtc >= start && t.DueDateUtc < end
                    && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue));
                break;
            }
            case WorkTaskDueFilter.DueThisWeek:
            {
                var end = now.AddDays(7);
                query = query.Where(t => t.DueDateUtc >= now && t.DueDateUtc <= end
                    && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue));
                break;
            }
            case WorkTaskDueFilter.Overdue:
                query = query.Where(t => t.DueDateUtc != null && t.DueDateUtc < now
                    && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue));
                break;
        }

        return query;
    }

    private async Task<Dictionary<string, string>> GetUserNamesAsync(IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Count == 0)
            return result;

        var users = await _db.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.FullName ?? u.UserName ?? u.Id })
            .ToListAsync(ct);

        foreach (var u in users)
            result[u.Id] = u.Name;

        return result;
    }

    private async Task<WorkTaskDto> MapToDtoAsync(WorkTask task, CancellationToken ct)
    {
        var names = await GetUserNamesAsync(new[] { task.CreatedByUserId, task.AssignedToUserId, task.CompletedByUserId }, ct);
        var now = DateTime.UtcNow;
        return new WorkTaskDto
        {
            Id = task.Id,
            Title = task.Title,
            Description = task.Description,
            Type = task.Type,
            Status = task.Status,
            Priority = task.Priority,
            CreatedByUserId = task.CreatedByUserId,
            CreatedByUserName = names.GetValueOrDefault(task.CreatedByUserId),
            AssignedToUserId = task.AssignedToUserId,
            AssignedToUserName = task.AssignedToUserId != null ? names.GetValueOrDefault(task.AssignedToUserId) : null,
            AssignedRole = task.AssignedRole,
            BranchId = task.BranchId,
            EntityType = task.EntityType,
            EntityId = task.EntityId,
            EntityCodeSnapshot = task.EntityCodeSnapshot,
            EntityTitleSnapshot = task.EntityTitleSnapshot,
            WorkTaskGroupId = task.WorkTaskGroupId,
            CreatedAtUtc = task.CreatedAtUtc,
            DueDateUtc = task.DueDateUtc,
            StartedAtUtc = task.StartedAtUtc,
            CompletedAtUtc = task.CompletedAtUtc,
            CompletedByUserId = task.CompletedByUserId,
            CompletedByUserName = task.CompletedByUserId != null ? names.GetValueOrDefault(task.CompletedByUserId) : null,
            CompletionComment = task.CompletionComment,
            IsOverdue = task.DueDateUtc.HasValue && task.DueDateUtc < now
                && WorkTaskTransitions.IsActive(task.Status),
        };
    }
}
