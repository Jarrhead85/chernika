using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class EquipmentService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _time;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPermissionService _permissions;
    private readonly TaskService _tasks;
    private readonly NotificationService _notifications;

    public EquipmentService(AppDbContext db, AuditService audit, ICurrentUserService currentUser, TimeProvider time, UserManager<ApplicationUser> userManager, IPermissionService permissions, TaskService tasks, NotificationService notifications)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
        _time = time;
        _userManager = userManager;
        _permissions = permissions;
        _tasks = tasks;
        _notifications = notifications;
    }

    public Task<List<EquipmentModel>> GetModelsAsync() =>
        _db.EquipmentModels.Include(m => m.EquipmentType).OrderBy(m => m.Index).ToListAsync();

    public Task<List<Node>> GetNodesAsync() =>
        _db.Nodes.Where(n => !n.IsDraft).OrderBy(n => n.Code).ToListAsync();

    public Task<Node?> GetNodeAsync(Guid id) =>
        _db.Nodes.FirstOrDefaultAsync(n => n.Id == id);

    public async Task<PagedResult<Node>> GetNodesPagedAsync(NodeQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<Node> queryable = _db.Nodes.Where(n => !n.IsDraft);

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(n => n.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(n => !n.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(n => EF.Functions.ILike(n.Code, $"%{term}%")
                || EF.Functions.ILike(n.Name, $"%{term}%")
                || EF.Functions.ILike(n.Description ?? "", $"%{term}%"));
        }

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<Node> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? queryable.OrderByDescending(n => n.Name).ThenByDescending(n => n.Code)
                : queryable.OrderBy(n => n.Name).ThenBy(n => n.Code),
            _ => query.SortDescending
                ? queryable.OrderByDescending(n => n.Code).ThenByDescending(n => n.Name)
                : queryable.OrderBy(n => n.Code).ThenBy(n => n.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Node>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<Node> CreateNodeAsync(Node node)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        node.Id = Guid.NewGuid();
        node.IsDraft = false;
        _db.Nodes.Add(node);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("Node", node.Id.ToString(), "Create", _currentUser.GetRequiredUserId()));
        return node;
    }

    public async Task<Node> UpdateNodeAsync(Node node)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        _db.Nodes.Update(node);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("Node", node.Id.ToString(), "Update", _currentUser.GetRequiredUserId()));
        return node;
    }

    public async Task<bool> DeleteNodeAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var n = await _db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (n == null || n.IsDeleted) return false;
        n.IsDeleted = true;
        n.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("Node", id.ToString(), "Delete", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> RestoreNodeAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var n = await _db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (n == null || !n.IsDeleted) return false;
        n.IsDeleted = false;
        n.DeletedAt = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("Node", id.ToString(), "Restore", _currentUser.GetRequiredUserId()));
        return true;
    }

    public Task<List<HKCard>> GetHKCardsAsync() =>
        _db.HKCards
            .Include(c => c.Node)
            .Include(c => c.Aggregate)
            .Include(c => c.EquipmentModel)
            .Include(c => c.Complex)
            .OrderByDescending(c => c.CreatedAt).ToListAsync();

    public Task<EquipmentModel?> GetModelAsync(Guid id) =>
        _db.EquipmentModels.FirstOrDefaultAsync(m => m.Id == id);

    public Task<EquipmentModel?> GetModelWithDetailsAsync(Guid id) =>
        _db.EquipmentModels
            .Include(m => m.Instances)
            .Include(m => m.ProductCompositions)
            .FirstOrDefaultAsync(m => m.Id == id);

    public async Task<PagedResult<EquipmentModel>> GetModelsPagedAsync(EquipmentModelQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<EquipmentModel> queryable = _db.EquipmentModels.Include(m => m.EquipmentType);

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(m => m.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(m => !m.IsDeleted);
        }

        if (query.EquipmentTypeId.HasValue)
            queryable = queryable.Where(m => m.EquipmentTypeId == query.EquipmentTypeId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(m =>
                EF.Functions.ILike(m.Index ?? "", $"%{term}%")
                || EF.Functions.ILike(m.Name, $"%{term}%")
                || EF.Functions.ILike(m.Type ?? "", $"%{term}%")
                || EF.Functions.ILike(m.Brand ?? "", $"%{term}%")
                || EF.Functions.ILike(m.Modification ?? "", $"%{term}%")
                || EF.Functions.ILike(m.Description ?? "", $"%{term}%")
                || (m.EquipmentType != null && (EF.Functions.ILike(m.EquipmentType.TypeGroup ?? "", $"%{term}%") || EF.Functions.ILike(m.EquipmentType.Name, $"%{term}%"))));
        }

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<EquipmentModel> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? queryable.OrderByDescending(m => m.Name).ThenByDescending(m => m.Index)
                : queryable.OrderBy(m => m.Name).ThenBy(m => m.Index),
            "Type" => query.SortDescending
                ? queryable.OrderByDescending(m => m.Type).ThenByDescending(m => m.Name)
                : queryable.OrderBy(m => m.Type).ThenBy(m => m.Name),
            "Brand" => query.SortDescending
                ? queryable.OrderByDescending(m => m.Brand).ThenByDescending(m => m.Name)
                : queryable.OrderBy(m => m.Brand).ThenBy(m => m.Name),
            _ => query.SortDescending
                ? queryable.OrderByDescending(m => m.Index).ThenByDescending(m => m.Name)
                : queryable.OrderBy(m => m.Index).ThenBy(m => m.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<EquipmentModel>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<EquipmentModel> CreateModelAsync(EquipmentModel model)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        model.Id = Guid.NewGuid();
        await ValidateModelEquipmentTypeAsync(model);
        _db.EquipmentModels.Add(model);
        await _db.SaveChangesAsync();
        return model;
    }

    public async Task<EquipmentModel> UpdateModelAsync(
        EquipmentModel updated,
        CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        await ValidateModelEquipmentTypeAsync(updated);

        var existing = await _db.EquipmentModels
            .FirstOrDefaultAsync(x => x.Id == updated.Id && !x.IsDeleted, ct);

        if (existing == null)
            throw new InvalidOperationException(
                "Не удалось изменить модель. Обновите список и повторите попытку.");

        existing.Index = updated.Index?.Trim() ?? string.Empty;
        existing.Name = updated.Name?.Trim() ?? string.Empty;
        existing.Type = updated.Type?.Trim();
        existing.Brand = updated.Brand?.Trim();
        existing.Modification = updated.Modification?.Trim();
        existing.Description = updated.Description?.Trim();
        existing.EquipmentTypeId = updated.EquipmentTypeId;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "EquipmentModel",
            existing.Id.ToString(),
            "Update",
            _currentUser.GetRequiredUserId(),
            EntityDisplayName: $"{existing.Index} — {existing.Name}"), ct);

        return existing;
    }

    public async Task<bool> UpdateModelPropertiesAsync(Guid id, EquipmentModel updated)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var model = await _db.EquipmentModels.FindAsync(id);
        if (model == null) return false;
        await ValidateModelEquipmentTypeAsync(updated);
        model.Index = updated.Index;
        model.Name = updated.Name;
        model.Type = updated.Type;
        model.Brand = updated.Brand;
        model.Modification = updated.Modification;
        model.Description = updated.Description;
        model.EquipmentTypeId = updated.EquipmentTypeId;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("EquipmentModel", id.ToString(), "Update", _currentUser.GetRequiredUserId(),
            EntityDisplayName: $"{model.Index} — {model.Name}"));
        return true;
    }

    private async Task ValidateModelEquipmentTypeAsync(EquipmentModel model)
    {
        if (model.EquipmentTypeId == Guid.Empty)
            model.EquipmentTypeId = null;

        if (model.EquipmentTypeId == null)
            return;

        var exists = await _db.EquipmentTypes
            .AnyAsync(e => e.Id == model.EquipmentTypeId.Value && !e.IsDeleted);

        if (!exists)
            throw new InvalidOperationException("Выбранный вид техники не найден. Обновите справочник и повторите попытку.");
    }

    public async Task<bool> DeleteModelAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var m = await _db.EquipmentModels.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (m == null || m.IsDeleted) return false;
        m.IsDeleted = true;
        m.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("EquipmentModel", id.ToString(), "Delete", _currentUser.GetRequiredUserId(),
            EntityDisplayName: $"{m.Index} — {m.Name}"));
        return true;
    }

    public async Task<bool> RestoreModelAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var m = await _db.EquipmentModels.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (m == null || !m.IsDeleted) return false;
        m.IsDeleted = false;
        m.DeletedAt = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("EquipmentModel", id.ToString(), "Restore", _currentUser.GetRequiredUserId(),
            EntityDisplayName: $"{m.Index} — {m.Name}"));
        return true;
    }

    public Task<List<EquipmentInstance>> GetInstancesAsync() =>
        _db.EquipmentInstances.Include(i => i.EquipmentModel)
            .OrderBy(i => i.SerialNumber).ToListAsync();

    public Task<EquipmentInstance?> GetInstanceAsync(Guid id) =>
        _db.EquipmentInstances.Include(i => i.EquipmentModel)
            .FirstOrDefaultAsync(i => i.Id == id);

    public async Task<EquipmentInstance> CreateInstanceAsync(EquipmentInstance inst)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        inst.Id = Guid.NewGuid();
        _db.EquipmentInstances.Add(inst);
        await _db.SaveChangesAsync();
        return inst;
    }

    public async Task<EquipmentInstance> UpdateInstanceAsync(EquipmentInstance inst)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        _db.EquipmentInstances.Update(inst);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("EquipmentInstance", inst.Id.ToString(), "Update", _currentUser.GetRequiredUserId(),
            EntityDisplayName: $"{inst.SerialNumber} — {inst.Name}"));
        return inst;
    }

    public async Task<PagedResult<EquipmentInstance>> GetInstancesPagedAsync(EquipmentInstanceQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<EquipmentInstance> queryable = _db.EquipmentInstances.Include(i => i.EquipmentModel);

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(i => i.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(i => !i.IsDeleted);
        }

        if (query.EquipmentModelId.HasValue)
            queryable = queryable.Where(i => i.EquipmentModelId == query.EquipmentModelId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(i =>
                EF.Functions.ILike(i.SerialNumber, $"%{term}%")
                || EF.Functions.ILike(i.Index, $"%{term}%")
                || EF.Functions.ILike(i.Name, $"%{term}%")
                || EF.Functions.ILike(i.Description ?? "", $"%{term}%")
                || (i.EquipmentModel != null && (EF.Functions.ILike(i.EquipmentModel.Index, $"%{term}%") || EF.Functions.ILike(i.EquipmentModel.Name, $"%{term}%"))));
        }

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<EquipmentInstance> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? queryable.OrderByDescending(i => i.Name).ThenByDescending(i => i.SerialNumber)
                : queryable.OrderBy(i => i.Name).ThenBy(i => i.SerialNumber),
            "Index" => query.SortDescending
                ? queryable.OrderByDescending(i => i.Index).ThenByDescending(i => i.Name)
                : queryable.OrderBy(i => i.Index).ThenBy(i => i.Name),
            _ => query.SortDescending
                ? queryable.OrderByDescending(i => i.SerialNumber).ThenByDescending(i => i.Name)
                : queryable.OrderBy(i => i.SerialNumber).ThenBy(i => i.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<EquipmentInstance>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<PagedResult<EquipmentInstance>> GetInstancesPagedAsync(int page = 1, int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.EquipmentInstances.Include(i => i.EquipmentModel);
        var total = await query.CountAsync();
        var items = await query
            .OrderBy(i => i.SerialNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<EquipmentInstance>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<bool> DeleteInstanceAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var i = await _db.EquipmentInstances.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (i == null || i.IsDeleted) return false;
        i.IsDeleted = true;
        i.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("EquipmentInstance", id.ToString(), "Delete", _currentUser.GetRequiredUserId(),
            EntityDisplayName: $"{i.SerialNumber} — {i.Name}"));
        return true;
    }

    public async Task<bool> RestoreInstanceAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var i = await _db.EquipmentInstances.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (i == null || !i.IsDeleted) return false;
        i.IsDeleted = false;
        i.DeletedAt = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("EquipmentInstance", id.ToString(), "Restore", _currentUser.GetRequiredUserId(),
            EntityDisplayName: $"{i.SerialNumber} — {i.Name}"));
        return true;
    }

    private async Task EnsureCompositionAccessAsync(string permission, Guid? branchId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId();
        if (!await _permissions.HasPermissionAsync(userId.ToString(), permission))
            throw new UnauthorizedAccessException("Недостаточно прав для работы с конструктивным составом.");
        if (branchId.HasValue && !await _permissions.HasPermissionAsync(userId.ToString(), PermissionCodes.SystemConfig))
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user?.BranchId != branchId.Value)
                throw new UnauthorizedAccessException("Нет доступа к составу другого филиала.");
        }
    }

    private static string LevelEditDraft(int level) => level switch
    {
        0 => PermissionCodes.CompositionComplexEditDraft,
        1 => PermissionCodes.CompositionEquipmentModelEditDraft,
        _ => PermissionCodes.CompositionAggregateEditDraft
    };
    private static string LevelSubmit(int level) => level switch
    {
        0 => PermissionCodes.CompositionComplexSubmit,
        1 => PermissionCodes.CompositionEquipmentModelSubmit,
        _ => PermissionCodes.CompositionAggregateSubmit
    };
    private static string LevelReturn(int level) => level switch
    {
        0 => PermissionCodes.CompositionComplexReturnForRevision,
        1 => PermissionCodes.CompositionEquipmentModelReturnForRevision,
        _ => PermissionCodes.CompositionAggregateReturnForRevision
    };
    private static string LevelApprove(int level) => level switch
    {
        0 => PermissionCodes.CompositionComplexApprove,
        1 => PermissionCodes.CompositionEquipmentModelApprove,
        _ => PermissionCodes.CompositionAggregateApprove
    };
    private static string LevelCreate(int level) => level switch
    {
        0 => PermissionCodes.CompositionComplexCreate,
        1 => PermissionCodes.CompositionEquipmentModelCreate,
        _ => PermissionCodes.CompositionAggregateCreate
    };
    private static string LevelCreateVersion(int level) => level switch
    {
        0 => PermissionCodes.CompositionComplexCreateVersion,
        1 => PermissionCodes.CompositionEquipmentModelCreateVersion,
        _ => PermissionCodes.CompositionAggregateCreateVersion
    };
    private static string LevelEntity(int level) => level switch
    {
        0 => "ComplexComposition",
        1 => "ProductComposition",
        _ => "AggregateComposition"
    };

    private static string LevelChildEntity(int level) => level switch
    {
        0 => "EquipmentModel",
        1 => "Aggregate",
        _ => "Node"
    };

    private async Task<Guid?> GetCurrentUserBranchAsync(CancellationToken ct = default)
    {
        var actor = await _userManager.FindByIdAsync(_currentUser.GetRequiredUserId().ToString());
        return actor?.BranchId;
    }

    private async Task<List<string>> GetActiveNormAdminIdsAsync(Guid branchId, CancellationToken ct)
    {
        var roleId = await _db.Roles.Where(r => r.Name == nameof(UserRole.NormAdmin)).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (roleId == null) return new List<string>();
        return await _db.Users
            .Where(u => !u.IsDeleted && u.IsActive && u.BranchId == branchId
                && _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == roleId))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    private async Task<List<string>> GetActiveSystemAdminIdsAsync(CancellationToken ct)
    {
        var roleId = await _db.Roles.Where(r => r.Name == nameof(UserRole.SystemAdmin)).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (roleId == null) return new List<string>();
        return await _db.Users
            .Where(u => !u.IsDeleted && u.IsActive
                && _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == roleId))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    private async Task CreateCompositionReviewWorkflowAsync(int level, Guid compositionId, Guid? branchId, string code, string version, CancellationToken ct)
    {
        if (!branchId.HasValue) return;
        var entity = LevelEntity(level);
        var title = $"Согласование состава: {code} v{version}";
        var description = "Требуется согласование конструктивного состава.";

        var normAdminIds = await GetActiveNormAdminIdsAsync(branchId.Value, ct);
        var command = new CreateCompositionReviewGroupCommand(
            entity, compositionId, branchId.Value, code, version, title, description);

        if (normAdminIds.Count == 0)
        {
            await _tasks.CreateCompositionReviewFallbackTaskAsync(command, ct);
            var sysAdminIds = await GetActiveSystemAdminIdsAsync(ct);
            await _notifications.NotifyCompositionReviewFallbackAsync(sysAdminIds, entity, compositionId, branchId.Value, code, version, ct);
            await _audit.LogAsync(new AuditWriteRequest(entity, compositionId.ToString(), "ReferenceProposal.NoNormAdmin",
                _currentUser.GetRequiredUserId(), Details: "Нет активного NormAdmin в филиале для согласования состава."), ct);
            return;
        }

        await _tasks.CreateCompositionReviewGroupAsync(command, normAdminIds, ct);
        await _notifications.NotifyCompositionReviewRequestedAsync(normAdminIds, entity, compositionId, branchId.Value, code, version, ct);
    }

    private async Task CloseCompositionReviewWorkflowAsync(int level, Guid compositionId, Guid? branchId, string actorUserId, CancellationToken ct)
    {
        var entity = LevelEntity(level);
        var actorTask = await _db.WorkTasks
            .FirstOrDefaultAsync(t => t.EntityType == entity && t.EntityId == compositionId
                && t.Type == WorkTaskType.CompositionReview
                && t.AssignedToUserId == actorUserId
                && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue)
                && t.WorkTaskGroupId != null, ct);

        var anyTask = actorTask ?? await _db.WorkTasks
            .FirstOrDefaultAsync(t => t.EntityType == entity && t.EntityId == compositionId
                && t.Type == WorkTaskType.CompositionReview
                && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Overdue)
                && t.WorkTaskGroupId != null, ct);

        if (anyTask == null || anyTask.WorkTaskGroupId == null) return;

        await _tasks.CompleteGroupAsync(anyTask.Id, actorUserId, null, ct);
    }

    private async Task CreateCompositionAuthorWorkflowAsync(int level, Guid compositionId, Guid? branchId, string? authorId, string? comment, string code, string version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authorId)) return;
        var entity = LevelEntity(level);
        var title = "Доработать состав";

        await _tasks.CreateCompositionAuthorTaskAsync(new CreateCompositionAuthorTaskCommand(
            entity, compositionId, branchId, authorId, code, version, title, comment), ct);
        await _notifications.NotifyCompositionReturnedToDraftAsync(authorId, entity, compositionId, branchId, code, version, ct);
    }

    private async Task NotifyCompositionApprovedWorkflowAsync(int level, Guid compositionId, Guid? branchId, string? authorId, string code, string version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authorId)) return;
        var entity = LevelEntity(level);
        await _notifications.NotifyCompositionApprovedAsync(authorId, entity, compositionId, branchId, code, version, ct);
    }

    private async Task CreateCompositionReadinessWorkflowAsync(int level, Guid compositionId, Guid? branchId, CancellationToken ct)
    {
        if (!branchId.HasValue) return;
        var rows = await EvaluateReadinessAsync(level, compositionId, ct);
        var normAdminIds = await GetActiveNormAdminIdsAsync(branchId.Value, ct);
        var childType = LevelChildEntity(level);

        foreach (var row in rows.Where(r => r.IsProblem))
        {
            var problemKey = $"composition-readiness:{childType}:{row.ChildId}:{row.Status}";
            var command = new CreateCompositionReadinessGroupCommand(
                childType, row.ChildId, branchId.Value, row.ChildCode, row.ChildName, problemKey,
                ReadinessStatusLabel(row.Status));

            var result = await _tasks.CreateCompositionReadinessGroupAsync(command, normAdminIds, ct);

            if (result.CreatedNew && row.HkCardId.HasValue)
            {
                var hk = await _db.HKCards.AsNoTracking()
                    .FirstOrDefaultAsync(h => h.Id == row.HkCardId.Value, ct);
                var hkAuthorId = hk?.AuthorId?.ToString();
                if (!string.IsNullOrEmpty(hkAuthorId))
                {
                    var hkAuthor = await _userManager.FindByIdAsync(hkAuthorId);
                    if (hkAuthor != null && !hkAuthor.IsDeleted && hkAuthor.IsActive)
                    {
                        await _notifications.NotifyCompositionReadinessAsync(
                            hkAuthorId, childType, row.ChildId, branchId.Value, row.ChildCode,
                            ReadinessStatusLabel(row.Status), ct);
                    }
                }
            }
        }
    }

    private static string ReadinessStatusLabel(string status) => status switch
    {
        ReadinessRow.Ready => "действующая ХК",
        ReadinessRow.Missing => "ХК отсутствует",
        ReadinessRow.Expired => "срок ХК истёк",
        ReadinessRow.FutureEffective => "ХК ещё не вступила в силу",
        ReadinessRow.ArchivedOrClosed => "нет действующей ХК",
        _ => status
    };

    public Task<List<WorkTask>> GetCompositionTasksAsync(int level, Guid compositionId, CancellationToken ct = default)
    {
        var entity = LevelEntity(level);
        return _db.WorkTasks.AsNoTracking()
            .Where(t => t.EntityType == entity && t.EntityId == compositionId && !t.IsDeleted)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public Task<List<AuditLog>> GetCompositionHistoryAsync(int level, Guid compositionId, CancellationToken ct = default)
    {
        var entity = LevelEntity(level);
        return _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == entity && a.EntityId == compositionId.ToString())
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<ReadinessRow>> EvaluateReadinessAsync(int level, Guid compositionId, CancellationToken ct = default)
    {
        var today = _time.GetUtcNow().UtcDateTime.Date;
        var rows = new List<ReadinessRow>();

        if (level == 1)
        {
            var comp = await _db.ProductCompositions
                .Include(c => c.Parts).ThenInclude(p => p.Aggregates).ThenInclude(a => a.Aggregate)
                .FirstOrDefaultAsync(c => c.Id == compositionId, ct);
            if (comp == null) return rows;
            foreach (var agg in comp.Parts.SelectMany(p => p.Aggregates))
            {
                if (agg.Aggregate == null) continue;
                rows.Add(await EvaluateChildReadinessAsync(agg.AggregateId, HKObjectLevel.Aggregate, agg.Aggregate.Code, agg.Aggregate.Name, today, ct));
            }
        }
        else if (level == 2)
        {
            var comp = await _db.AggregateCompositions
                .Include(c => c.Nodes).ThenInclude(n => n.Node)
                .FirstOrDefaultAsync(c => c.Id == compositionId, ct);
            if (comp == null) return rows;
            foreach (var node in comp.Nodes)
            {
                if (node.Node == null) continue;
                rows.Add(await EvaluateChildReadinessAsync(node.NodeId, HKObjectLevel.Node, node.Node.Code, node.Node.Name, today, ct));
            }
        }
        else
        {
            var comp = await _db.ComplexCompositions
                .Include(c => c.Items).ThenInclude(i => i.EquipmentModel)
                .FirstOrDefaultAsync(c => c.Id == compositionId, ct);
            if (comp == null) return rows;
            foreach (var item in comp.Items)
            {
                if (item.EquipmentModel == null) continue;
                rows.Add(await EvaluateChildReadinessAsync(item.EquipmentModelId, HKObjectLevel.EquipmentModel, item.EquipmentModel.Index, item.EquipmentModel.Name, today, ct));
            }
        }

        return rows;
    }

    private async Task<ReadinessRow> EvaluateChildReadinessAsync(Guid childId, HKObjectLevel objectLevel, string code, string name, DateTime today, CancellationToken ct)
    {
        var query = objectLevel switch
        {
            HKObjectLevel.Complex => _db.HKCards.Where(h => h.ObjectLevel == objectLevel && h.ComplexId == childId),
            HKObjectLevel.EquipmentModel => _db.HKCards.Where(h => h.ObjectLevel == objectLevel && h.EquipmentModelId == childId),
            HKObjectLevel.Aggregate => _db.HKCards.Where(h => h.ObjectLevel == objectLevel && h.AggregateId == childId),
            _ => _db.HKCards.Where(h => h.ObjectLevel == objectLevel && h.NodeId == childId),
        };

        var hks = await query.ToListAsync(ct);

        var approved = hks.Where(h => h.Status == HKCardStatus.Approved).ToList();
        if (approved.Any())
        {
            var valid = approved.FirstOrDefault(h =>
                (h.EffectiveDate == null || h.EffectiveDate.Value.Date <= today)
                && (h.ExpirationDate == null || h.ExpirationDate.Value.Date >= today));
            if (valid != null)
                return new ReadinessRow(childId, code, name, ReadinessRow.Ready, valid.Id, valid.Version);

            var expired = approved.FirstOrDefault(h => h.ExpirationDate != null && h.ExpirationDate.Value.Date < today);
            if (expired != null)
                return new ReadinessRow(childId, code, name, ReadinessRow.Expired, expired.Id, expired.Version);

            return new ReadinessRow(childId, code, name, ReadinessRow.FutureEffective, null, null);
        }

        var archived = hks.FirstOrDefault(h => h.Status == HKCardStatus.Archived);
        if (archived != null)
            return new ReadinessRow(childId, code, name, ReadinessRow.ArchivedOrClosed, archived.Id, archived.Version);

        return new ReadinessRow(childId, code, name, ReadinessRow.Missing, null, null);
    }

    private static string BuildUniqueVersion(string baseVersion, IReadOnlyCollection<string> existing)
    {
        if (existing.Count == 0)
            return baseVersion;

        var maxSuffix = existing
            .Select(v =>
            {
                var suffix = v.Length > baseVersion.Length + 1 ? v[(baseVersion.Length + 1)..] : string.Empty;
                return int.TryParse(suffix, out var n) ? n : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        return maxSuffix == 0 ? $"{baseVersion}.2" : $"{baseVersion}.{maxSuffix + 1}";
    }

    // ── Product Composition ──────────────────────────────────────────────

    public async Task<List<ProductComposition>> GetCompositionsAsync(Guid? equipmentModelId = null, CancellationToken ct = default)
    {
        var query = _db.ProductCompositions
            .Include(c => c.EquipmentModel)
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates).ThenInclude(a => a.Aggregate)
            .AsQueryable();

        if (equipmentModelId.HasValue)
            query = query.Where(c => c.EquipmentModelId == equipmentModelId.Value);

        return await query.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
    }

    public async Task<ProductComposition?> GetCompositionAsync(Guid id, CancellationToken ct = default) =>
        await _db.ProductCompositions
            .Include(c => c.EquipmentModel)
            .Include(c => c.Parts.OrderBy(p => p.SortOrder))
                .ThenInclude(p => p.Aggregates.OrderBy(a => a.Aggregate.Code))
                    .ThenInclude(a => a.Aggregate)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<List<ProductComposition>> GetCompositionsWithCoverageAsync(Guid? equipmentModelId = null, CancellationToken ct = default)
    {
        var query = _db.ProductCompositions
            .Include(c => c.EquipmentModel)
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates).ThenInclude(a => a.Aggregate)
            .AsQueryable();

        if (equipmentModelId.HasValue)
            query = query.Where(c => c.EquipmentModelId == equipmentModelId.Value);

        return await query.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
    }

    public async Task<ProductCompositionPart?> GetCompositionPartAsync(Guid partId, CancellationToken ct = default) =>
        await _db.ProductCompositionParts
            .Include(p => p.Aggregates.OrderBy(a => a.Aggregate.Code))
                .ThenInclude(a => a.Aggregate)
            .FirstOrDefaultAsync(p => p.Id == partId, ct);

    public async Task<ProductCompositionAggregate?> GetCompositionAggregateAsync(Guid id, CancellationToken ct = default) =>
        await _db.ProductCompositionAggregates
            .Include(a => a.Aggregate)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<ProductComposition> CreateCompositionDraftAsync(CreateCompositionRequest request, CancellationToken ct = default)
    {
        await EnsureCompositionAccessAsync(LevelCreate(1), null, ct);
        if (request.EquipmentModelId == Guid.Empty)
            throw new ArgumentException("EquipmentModelId is required.");

        var now = _time.GetUtcNow();
        var branchId = await GetCurrentUserBranchAsync(ct);
        var comp = new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = request.EquipmentModelId,
            Status = ProductCompositionStatus.Draft,
            Version = "v" + now.ToString("MMyy"),
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
            AuthorId = _currentUser.GetRequiredUserId().ToString(),
            Comment = request.Comment,
            IsActive = false,
            BranchId = branchId
        };

        _db.ProductCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductComposition", comp.Id.ToString(), "CreateDraft", _currentUser.GetRequiredUserId()));
        return comp;
    }

    public async Task<(bool Success, Guid? CompositionId, string? Error)> CreateProductCompositionVersionAsync(Guid sourceCompositionId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId();
        var source = await _db.ProductCompositions
            .Include(c => c.Parts.OrderBy(p => p.SortOrder))
                .ThenInclude(p => p.Aggregates.OrderBy(a => a.SortOrder))
            .FirstOrDefaultAsync(c => c.Id == sourceCompositionId, ct);
        if (source == null)
            return (false, null, "Состав изделия не найден.");
        if (source.Status is not (ProductCompositionStatus.Approved or ProductCompositionStatus.Archived))
            return (false, null, "Новую версию можно создать только из утверждённого или архивного состава.");
        await EnsureCompositionAccessAsync(LevelCreateVersion(1), source.BranchId, ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var baseVersion = "v" + now.ToString("MMyy");
        var existingVersions = await _db.ProductCompositions
            .Where(c => c.EquipmentModelId == source.EquipmentModelId && c.Version.StartsWith(baseVersion))
            .Select(c => c.Version)
            .ToListAsync(ct);
        var version = BuildUniqueVersion(baseVersion, existingVersions);

        var comp = new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = source.EquipmentModelId,
            Status = ProductCompositionStatus.Draft,
            Version = version,
            CreatedAt = now,
            UpdatedAt = now,
            AuthorId = userId.ToString(),
            Comment = $"Новая версия на основе {source.Version}",
            IsActive = false,
            BranchId = source.BranchId,
            SupersedesProductCompositionId = source.Id,
            Parts = source.Parts.Select(p => new ProductCompositionPart
            {
                Id = Guid.NewGuid(),
                ProductCompositionId = Guid.Empty,
                Name = p.Name,
                Description = p.Description,
                SortOrder = p.SortOrder,
                Aggregates = p.Aggregates.Select(a => new ProductCompositionAggregate
                {
                    Id = Guid.NewGuid(),
                    ProductCompositionId = Guid.Empty,
                    PartId = null,
                    AggregateId = a.AggregateId,
                    Quantity = a.Quantity,
                    SortOrder = a.SortOrder,
                    Notes = a.Notes
                }).ToList()
            }).ToList()
        };

        foreach (var part in comp.Parts)
        {
            part.ProductCompositionId = comp.Id;
            foreach (var agg in part.Aggregates)
            {
                agg.ProductCompositionId = comp.Id;
                agg.PartId = part.Id;
            }
        }

        _db.ProductCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductComposition", comp.Id.ToString(), "ProductComposition.NewVersionCreated", userId,
            EntityDisplayName: $"{comp.Version}",
            Details: $"Создана новая версия состава изделия на основе {source.Version} (Id {source.Id}). Новая версия: {comp.Version} (Id {comp.Id})."), ct);
        return (true, comp.Id, null);
    }

    public async Task<bool> UpdateCompositionDraftAsync(UpdateCompositionDraftRequest request, CancellationToken ct = default)
    {
        var comp = await _db.ProductCompositions.FindAsync(new object[] { request.Id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(1), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.EffectiveDate.HasValue && request.ExpirationDate.HasValue && request.ExpirationDate < request.EffectiveDate)
            throw new InvalidOperationException("Дата окончания действия не может быть раньше даты начала.");

        comp.Comment = request.Comment;
        comp.EffectiveDate = request.EffectiveDate;
        comp.ExpirationDate = request.ExpirationDate;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductComposition", comp.Id.ToString(), "UpdateDraft", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> DeleteCompositionDraftAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.ProductCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(1), comp.BranchId, ct);
        if (comp.Status == ProductCompositionStatus.Approved || comp.Status == ProductCompositionStatus.Archived)
            throw new InvalidOperationException("Нельзя удалить утверждённый или архивный состав.");
        if (comp.Status == ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Нельзя удалить состав на проверке. Верните его в черновик.");

        _db.ProductCompositions.Remove(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductComposition", id.ToString(), "DeleteDraft", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> SubmitForReviewAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.ProductCompositions
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelSubmit(1), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Отправка на согласование возможна только из статуса «Черновик».");
        if (!comp.Parts.Any() || !comp.Parts.SelectMany(p => p.Aggregates).Any())
            throw new InvalidOperationException("Нельзя отправить на согласование пустой состав.");
        await ChangeCompositionStatusInternalAsync(id, ProductCompositionStatus.OnReview, null, ct);
        await CreateCompositionReviewWorkflowAsync(1, comp.Id, comp.BranchId,
            comp.EquipmentModel?.Index ?? comp.EquipmentModelId.ToString(), comp.Version, ct);
        return true;
    }

    public async Task<bool> ReturnToDraftAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var comp = await _db.ProductCompositions.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelReturn(1), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Возврат в черновик возможен только из статуса «На проверке».");
        await ChangeCompositionStatusInternalAsync(id, ProductCompositionStatus.Draft, comment, ct);
        await CloseCompositionReviewWorkflowAsync(1, comp.Id, comp.BranchId, _currentUser.GetRequiredUserId().ToString(), ct);
        await CreateCompositionAuthorWorkflowAsync(1, comp.Id, comp.BranchId, comp.AuthorId,
            comment, comp.EquipmentModel?.Index ?? comp.EquipmentModelId.ToString(), comp.Version, ct);
        return true;
    }

    public async Task<bool> ApproveCompositionAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var comp = await _db.ProductCompositions
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelApprove(1), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Утверждение возможно только для состава в статусе «На проверке».");
        if (!comp.Parts.Any() || !comp.Parts.SelectMany(p => p.Aggregates).Any())
            throw new InvalidOperationException("Нельзя утвердить пустой состав.");

        if (comp.SupersedesProductCompositionId.HasValue)
        {
            var predecessor = await _db.ProductCompositions
                .FirstOrDefaultAsync(c => c.Id == comp.SupersedesProductCompositionId.Value, ct);
            if (predecessor == null)
                throw new InvalidOperationException("Не найден предшествующий состав для новой версии.");
            if (predecessor.EquipmentModelId != comp.EquipmentModelId)
                throw new InvalidOperationException("Предшествующий состав относится к другому изделию.");
            if (predecessor.BranchId != comp.BranchId)
                throw new InvalidOperationException("Предшествующий состав относится к другому филиалу.");
            if (predecessor.Status is ProductCompositionStatus.Draft or ProductCompositionStatus.OnReview)
                throw new InvalidOperationException("Предшествующий состав ещё не утверждён.");
        }

        var now = _time.GetUtcNow().UtcDateTime;

        var active = await _db.ProductCompositions
            .Where(c => c.EquipmentModelId == comp.EquipmentModelId && c.Id != comp.Id && c.IsActive)
            .ToListAsync(ct);
        foreach (var p in active)
        {
            p.Status = ProductCompositionStatus.Archived;
            p.IsActive = false;
            p.UpdatedAt = now;
        }

        comp.Status = ProductCompositionStatus.Approved;
        comp.ApprovedByUserId = _currentUser.GetRequiredUserId().ToString();
        comp.ApprovedAt = now;
        comp.EffectiveDate ??= now;
        comp.IsActive = true;
        comp.UpdatedAt = now;
        comp.Comment = comment ?? comp.Comment;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductComposition", id.ToString(), "ProductComposition.Approved", _currentUser.GetRequiredUserId()));
        await CloseCompositionReviewWorkflowAsync(1, comp.Id, comp.BranchId, _currentUser.GetRequiredUserId().ToString(), ct);
        await NotifyCompositionApprovedWorkflowAsync(1, comp.Id, comp.BranchId, comp.AuthorId,
            comp.EquipmentModel?.Index ?? comp.EquipmentModelId.ToString(), comp.Version, ct);
        await CreateCompositionReadinessWorkflowAsync(1, comp.Id, comp.BranchId, ct);
        return true;
    }

    public async Task<bool> ArchiveCompositionAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.ProductCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelApprove(1), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Approved)
            throw new InvalidOperationException("Архивирование разрешено только для утверждённого состава.");

        comp.Status = ProductCompositionStatus.Archived;
        comp.IsActive = false;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductComposition", id.ToString(), "ProductComposition.Archived", _currentUser.GetRequiredUserId()));
        return true;
    }

    private async Task<bool> ChangeCompositionStatusInternalAsync(Guid id, ProductCompositionStatus newStatus, string? comment, CancellationToken ct)
    {
        var comp = await _db.ProductCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;

        var allowed = (comp.Status, newStatus) switch
        {
            (ProductCompositionStatus.Draft, ProductCompositionStatus.OnReview) => true,
            (ProductCompositionStatus.OnReview, ProductCompositionStatus.Draft) => true,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Переход из «{comp.Status}» в «{newStatus}» не допускается.");

        comp.Status = newStatus;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        comp.Comment = comment ?? comp.Comment;
        await _db.SaveChangesAsync(ct);

        var action = newStatus == ProductCompositionStatus.OnReview
            ? "ProductComposition.Submitted"
            : "ProductComposition.ReturnedToDraft";
        await _audit.LogAsync(new AuditWriteRequest("ProductComposition", id.ToString(), action, _currentUser.GetRequiredUserId()));
        return true;
    }

    // ── Parts ────────────────────────────────────────────────────────────

    public async Task<ProductCompositionPart> AddPartAsync(AddPartRequest request, CancellationToken ct = default)
    {
        var comp = await _db.ProductCompositions.FindAsync(new object[] { request.CompositionId }, ct);
        if (comp == null) throw new InvalidOperationException("Состав не найден.");
        await EnsureCompositionAccessAsync(LevelEditDraft(1), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Наименование части обязательно.");

        var part = new ProductCompositionPart
        {
            Id = Guid.NewGuid(),
            ProductCompositionId = request.CompositionId,
            Name = request.Name.Trim(),
            Description = request.Description,
            SortOrder = request.SortOrder
        };

        _db.ProductCompositionParts.Add(part);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("ProductCompositionPart", part.Id.ToString(), "ProductComposition.PartAdded", _currentUser.GetRequiredUserId()));
        return await _db.ProductCompositionParts
            .Include(p => p.Aggregates).ThenInclude(a => a.Aggregate)
            .FirstAsync(p => p.Id == part.Id, ct);
    }

    public async Task<bool> UpdatePartAsync(UpdatePartRequest request, CancellationToken ct = default)
    {
        var part = await _db.ProductCompositionParts
            .Include(p => p.ProductComposition)
            .FirstOrDefaultAsync(p => p.Id == request.PartId, ct);
        if (part == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(1), part.ProductComposition.BranchId, ct);
        if (part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        part.Name = request.Name.Trim();
        part.Description = request.Description;
        part.SortOrder = request.SortOrder;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductCompositionPart", request.PartId.ToString(), "ProductComposition.PartRenamed", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> RemovePartAsync(Guid partId, CancellationToken ct = default)
    {
        var part = await _db.ProductCompositionParts
            .Include(p => p.ProductComposition)
            .FirstOrDefaultAsync(p => p.Id == partId, ct);
        if (part == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(1), part.ProductComposition.BranchId, ct);
        if (part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление частей разрешено только в статусе «Черновик».");

        _db.ProductCompositionParts.Remove(part);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductCompositionPart", partId.ToString(), "ProductComposition.PartRemoved", _currentUser.GetRequiredUserId()));
        return true;
    }

    // ── Aggregates ────────────────────────────────────────────────────────

    public async Task<ProductCompositionAggregate> AddAggregateAsync(AddProductCompositionAggregateRequest request, CancellationToken ct = default)
    {
        if (request.PartId == Guid.Empty)
            throw new ArgumentException("PartId is required.");
        if (request.AggregateId == Guid.Empty)
            throw new ArgumentException("AggregateId is required.");
        if (request.Quantity <= 0)
            throw new ArgumentException("Количество должно быть больше 0.");

        var part = await _db.ProductCompositionParts
            .Include(p => p.ProductComposition)
            .Include(p => p.Aggregates)
            .FirstOrDefaultAsync(p => p.Id == request.PartId, ct);
        if (part == null) throw new InvalidOperationException("Часть состава не найдена.");
        await EnsureCompositionAccessAsync(LevelEditDraft(1), part.ProductComposition.BranchId, ct);
        if (part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        var aggregateExists = await _db.Aggregates.AnyAsync(a => a.Id == request.AggregateId && !a.IsDeleted, ct);
        if (!aggregateExists) throw new InvalidOperationException("Агрегат не найден.");

        if (part.Aggregates.Any(a => a.AggregateId == request.AggregateId))
            throw new InvalidOperationException("Агрегат уже добавлен в эту часть.");

        var pca = new ProductCompositionAggregate
        {
            Id = Guid.NewGuid(),
            ProductCompositionId = part.ProductCompositionId,
            PartId = request.PartId,
            AggregateId = request.AggregateId,
            Quantity = request.Quantity
        };

        _db.ProductCompositionAggregates.Add(pca);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("ProductCompositionAggregate", pca.Id.ToString(), "ProductComposition.AggregateAdded", _currentUser.GetRequiredUserId()));
        return await _db.ProductCompositionAggregates
            .Include(a => a.Aggregate)
            .FirstAsync(a => a.Id == pca.Id, ct);
    }

    public async Task<bool> UpdateAggregateQuantityAsync(UpdateProductCompositionAggregateRequest request, CancellationToken ct = default)
    {
        if (request.Quantity <= 0)
            throw new ArgumentException("Количество должно быть больше 0.");

        var pca = await _db.ProductCompositionAggregates
            .Include(a => a.ProductComposition)
            .FirstOrDefaultAsync(a => a.Id == request.Id, ct);
        if (pca == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(1), pca.ProductComposition.BranchId, ct);
        if (pca.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        pca.Quantity = request.Quantity;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductCompositionAggregate", request.Id.ToString(), "ProductComposition.AggregateUpdated", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> RemoveAggregateAsync(Guid id, CancellationToken ct = default)
    {
        var pca = await _db.ProductCompositionAggregates
            .Include(a => a.ProductComposition)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (pca == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(1), pca.ProductComposition.BranchId, ct);
        if (pca.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление агрегатов разрешено только в статусе «Черновик».");

        _db.ProductCompositionAggregates.Remove(pca);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ProductCompositionAggregate", id.ToString(), "ProductComposition.AggregateRemoved", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> IsCompositionActiveByAggregateAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.ProductCompositionAggregates
            .Include(a => a.ProductComposition)
            .AnyAsync(a => a.Id == id && a.ProductComposition.IsActive, ct);
    }

    public Task<List<Branch>> GetBranchesAsync() =>
        _db.Branches.OrderBy(b => b.Name).ToListAsync();

    public Task<Branch?> GetBranchAsync(Guid id) =>
        _db.Branches.FirstOrDefaultAsync(b => b.Id == id);

    public async Task<PagedResult<Branch>> GetBranchesPagedAsync(BranchQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<Branch> queryable = _db.Branches;

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(b => b.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(b => !b.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(b => EF.Functions.ILike(b.Name, $"%{term}%")
                || EF.Functions.ILike(b.Description ?? "", $"%{term}%"));
        }

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<Branch> ordered = query.SortBy switch
        {
            "CreatedAt" => query.SortDescending
                ? queryable.OrderByDescending(b => b.CreatedAt)
                : queryable.OrderBy(b => b.CreatedAt),
            _ => query.SortDescending
                ? queryable.OrderByDescending(b => b.Name)
                : queryable.OrderBy(b => b.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Branch>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<Branch> CreateBranchAsync(Branch branch, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.SystemConfig, ct);
        ValidateBranch(branch);
        await EnsureNoDuplicateBranchAsync(branch.Name, null, ct);

        branch.Id = Guid.NewGuid();
        branch.CreatedAt = _time.GetUtcNow().UtcDateTime;
        branch.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        branch.IsDeleted = false;
        branch.DeletedAt = null;
        _db.Branches.Add(branch);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("Branch", branch.Id.ToString(), "Create", _currentUser.GetRequiredUserId(),
            EntityDisplayName: branch.Name), ct);

        return branch;
    }

    public async Task<bool> UpdateBranchAsync(Branch branch, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.SystemConfig, ct);
        ValidateBranch(branch);

        var existing = await _db.Branches.FirstOrDefaultAsync(x => x.Id == branch.Id && !x.IsDeleted, ct);
        if (existing == null)
            throw new InvalidOperationException("Не удалось изменить запись справочника. Обновите список и повторите попытку.");

        await EnsureNoDuplicateBranchAsync(branch.Name, existing.Id, ct);

        existing.Name = branch.Name;
        existing.Description = branch.Description;
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("Branch", existing.Id.ToString(), "Update", _currentUser.GetRequiredUserId(),
            EntityDisplayName: existing.Name), ct);

        return true;
    }

    public async Task<(bool Deleted, string? Error)> DeleteBranchAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.SystemConfig, ct);

        var branch = await _db.Branches.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (branch == null) return (false, null);

        var hasActiveUsers = await _db.Users.AnyAsync(u => u.BranchId == id && !u.IsDeleted, ct);
        if (hasActiveUsers) return (false, "Невозможно архивировать филиал: с ним связаны активные пользователи или документы.\nСначала переназначьте или деактивируйте связанные записи.");

        var hasActiveCards = await _db.HKCards.AnyAsync(h => h.BranchId == id && h.Status != HKCardStatus.Deleted, ct);
        if (hasActiveCards) return (false, "Невозможно архивировать филиал: с ним связаны активные пользователи или документы.\nСначала переназначьте или деактивируйте связанные записи.");

        var hasUnfinishedTasks = await _db.WorkTasks.AnyAsync(w => w.BranchId == id && !w.IsDeleted
            && w.Status != WorkTaskStatus.Completed && w.Status != WorkTaskStatus.Cancelled, ct);
        if (hasUnfinishedTasks) return (false, "Невозможно архивировать филиал: с ним связаны активные пользователи или документы.\nСначала переназначьте или деактивируйте связанные записи.");

        branch.IsDeleted = true;
        branch.DeletedAt = _time.GetUtcNow().UtcDateTime;
        branch.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("Branch", id.ToString(), "Delete", _currentUser.GetRequiredUserId(),
            EntityDisplayName: branch.Name), ct);

        return (true, null);
    }

    public async Task<bool> RestoreBranchAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.SystemConfig, ct);

        var existing = await _db.Branches.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (existing == null || !existing.IsDeleted) return false;

        await EnsureNoDuplicateBranchAsync(existing.Name, id, ct);

        existing.IsDeleted = false;
        existing.DeletedAt = null;
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("Branch", id.ToString(), "Restore", _currentUser.GetRequiredUserId(),
            EntityDisplayName: existing.Name), ct);

        return true;
    }

    private static void ValidateBranch(Branch branch)
    {
        branch.Name = branch.Name?.Trim() ?? "";
        branch.Description = string.IsNullOrWhiteSpace(branch.Description) ? null : branch.Description.Trim();

        if (string.IsNullOrWhiteSpace(branch.Name))
            throw new InvalidOperationException("Укажите наименование.");
        if (branch.Name.Length > 256)
            throw new InvalidOperationException("Наименование должно быть не длиннее 256 символов.");
        if (branch.Description?.Length > 2000)
            throw new InvalidOperationException("Описание должно быть не длиннее 2000 символов.");
    }

    private async Task EnsureNoDuplicateBranchAsync(string name, Guid? selfId, CancellationToken ct = default)
    {
        var normalizedName = name.Trim().ToUpperInvariant();

        var query = _db.Branches
            .AsNoTracking()
            .Where(b => !b.IsDeleted && b.Name.Trim().ToUpper() == normalizedName);
        if (selfId.HasValue)
            query = query.Where(b => b.Id != selfId.Value);

        if (await query.AnyAsync(ct))
            throw new InvalidOperationException($"Филиал «{name.Trim()}» уже существует.");
    }

    public Task<List<Aggregate>> GetAggregatesAsync() =>
        _db.Aggregates.OrderBy(a => a.Code).ToListAsync();

    public Task<Aggregate?> GetAggregateAsync(Guid id) =>
        _db.Aggregates.FirstOrDefaultAsync(a => a.Id == id);

    public async Task<PagedResult<Aggregate>> GetAggregatesPagedAsync(AggregateQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<Aggregate> queryable = _db.Aggregates;

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(a => a.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(a => !a.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(a => EF.Functions.ILike(a.Code, $"%{term}%")
                || EF.Functions.ILike(a.Name, $"%{term}%")
                || EF.Functions.ILike(a.Description ?? "", $"%{term}%"));
        }

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<Aggregate> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? queryable.OrderByDescending(a => a.Name).ThenByDescending(a => a.Code)
                : queryable.OrderBy(a => a.Name).ThenBy(a => a.Code),
            _ => query.SortDescending
                ? queryable.OrderByDescending(a => a.Code).ThenByDescending(a => a.Name)
                : queryable.OrderBy(a => a.Code).ThenBy(a => a.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Aggregate>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<Aggregate> CreateAggregateAsync(CreateAggregateRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        var entity = new Aggregate
        {
            Id = Guid.NewGuid(),
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim()
        };
        _db.Aggregates.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("Aggregate", entity.Id.ToString(), "Create", _currentUser.GetRequiredUserId()), ct);
        return entity;
    }

    public async Task<bool> UpdateAggregateAsync(UpdateAggregateRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        var a = await _db.Aggregates.FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, ct);
        if (a == null) return false;
        a.Code = request.Code.Trim();
        a.Name = request.Name.Trim();
        a.Description = request.Description?.Trim();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("Aggregate", request.Id.ToString(), "Update", _currentUser.GetRequiredUserId()), ct);
        return true;
    }

    public async Task<(bool Deleted, string? Error)> DeleteAggregateAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var a = await _db.Aggregates.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a == null || a.IsDeleted) return (false, null);
        var inActiveComposition = await _db.ProductCompositionAggregates
            .AnyAsync(pca => pca.AggregateId == id && pca.ProductComposition.IsActive, ct);
        if (inActiveComposition) return (false, "Нельзя удалить: агрегат используется в активном составе изделия.");
        var inApprovedHK = await _db.AggregateCompositionNodes
            .AnyAsync(acn => acn.AggregateComposition.AggregateId == id
                          && acn.AggregateComposition.IsActive
                          && acn.Node.HKCards.Any(h => h.Status == HKCardStatus.Approved), ct);
        if (inApprovedHK) return (false, "Нельзя удалить: агрегат используется в утверждённой ХК (через узел).");
        a.IsDeleted = true;
        a.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("Aggregate", id.ToString(), "Delete", _currentUser.GetRequiredUserId()), ct);
        return (true, null);
    }

    public async Task<bool> RestoreAggregateAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var a = await _db.Aggregates.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a == null || !a.IsDeleted) return false;

        a.IsDeleted = false;
        a.DeletedAt = null;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("Aggregate", id.ToString(), "Restore", _currentUser.GetRequiredUserId()), ct);
        return true;
    }

    // ── AggregateComposition ───────────────────────────────────

    public Task<List<AggregateComposition>> GetAggregateCompositionsAsync(Guid aggregateId, CancellationToken ct = default) =>
        _db.AggregateCompositions
            .Include(c => c.Nodes.OrderBy(n => n.SortOrder)).ThenInclude(n => n.Node)
            .Where(c => c.AggregateId == aggregateId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public Task<AggregateComposition?> GetAggregateCompositionAsync(Guid id, CancellationToken ct = default) =>
        _db.AggregateCompositions
            .Include(c => c.Nodes.OrderBy(n => n.SortOrder)).ThenInclude(n => n.Node)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<List<ProductComposition>> GetAllProductCompositionsLightAsync(CancellationToken ct = default) =>
        _db.ProductCompositions
            .Include(c => c.EquipmentModel)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    public Task<List<ComplexComposition>> GetAllComplexCompositionsLightAsync(CancellationToken ct = default) =>
        _db.ComplexCompositions
            .AsNoTracking()
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    public Task<List<AggregateComposition>> GetAllAggregateCompositionsLightAsync(CancellationToken ct = default) =>
        _db.AggregateCompositions
            .AsNoTracking()
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<AggregateComposition> CreateAggregateCompositionAsync(CreateAggregateCompositionRequest request, CancellationToken ct = default)
    {
        await EnsureCompositionAccessAsync(LevelCreate(2), null, ct);
        if (request.AggregateId == Guid.Empty)
            throw new ArgumentException("AggregateId is required.");
        var aggregate = await _db.Aggregates.FindAsync(new object[] { request.AggregateId }, ct);
        if (aggregate == null) throw new InvalidOperationException("Агрегат не найден.");

        var now = _time.GetUtcNow();
        var branchId = await GetCurrentUserBranchAsync(ct);
        var comp = new AggregateComposition
        {
            Id = Guid.NewGuid(),
            AggregateId = request.AggregateId,
            Status = ProductCompositionStatus.Draft,
            Version = "v" + now.ToString("MMyy"),
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
            AuthorId = _currentUser.GetRequiredUserId().ToString(),
            Comment = request.Comment,
            IsActive = false,
            BranchId = branchId
        };
        _db.AggregateCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateComposition", comp.Id.ToString(), "CreateDraft", _currentUser.GetRequiredUserId()));
        return comp;
    }

    public async Task<(bool Success, Guid? CompositionId, string? Error)> CreateAggregateCompositionVersionAsync(Guid sourceCompositionId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId();
        var source = await _db.AggregateCompositions
            .Include(c => c.Nodes.OrderBy(n => n.SortOrder))
            .FirstOrDefaultAsync(c => c.Id == sourceCompositionId, ct);
        if (source == null)
            return (false, null, "Состав агрегата не найден.");
        if (source.Status is not (ProductCompositionStatus.Approved or ProductCompositionStatus.Archived))
            return (false, null, "Новую версию можно создать только из утверждённого или архивного состава.");
        await EnsureCompositionAccessAsync(LevelCreateVersion(2), source.BranchId, ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var baseVersion = "v" + now.ToString("MMyy");
        var existingVersions = await _db.AggregateCompositions
            .Where(c => c.AggregateId == source.AggregateId && c.Version.StartsWith(baseVersion))
            .Select(c => c.Version)
            .ToListAsync(ct);
        var version = BuildUniqueVersion(baseVersion, existingVersions);

        var comp = new AggregateComposition
        {
            Id = Guid.NewGuid(),
            AggregateId = source.AggregateId,
            Status = ProductCompositionStatus.Draft,
            Version = version,
            CreatedAt = now,
            UpdatedAt = now,
            AuthorId = userId.ToString(),
            Comment = $"Новая версия на основе {source.Version}",
            IsActive = false,
            BranchId = source.BranchId,
            SupersedesAggregateCompositionId = source.Id,
            Nodes = source.Nodes.Select(n => new AggregateCompositionNode
            {
                Id = Guid.NewGuid(),
                AggregateCompositionId = Guid.Empty,
                NodeId = n.NodeId,
                Quantity = n.Quantity,
                SortOrder = n.SortOrder,
                Notes = n.Notes
            }).ToList()
        };

        foreach (var node in comp.Nodes)
            node.AggregateCompositionId = comp.Id;

        _db.AggregateCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateComposition", comp.Id.ToString(), "AggregateComposition.NewVersionCreated", userId,
            EntityDisplayName: comp.Version,
            Details: $"Создана новая версия состава агрегата на основе {source.Version} (Id {source.Id}). Новая версия: {comp.Version} (Id {comp.Id})."), ct);
        return (true, comp.Id, null);
    }

    public async Task<bool> UpdateAggregateCompositionDraftAsync(UpdateAggregateCompositionDraftRequest request, CancellationToken ct = default)
    {
        var comp = await _db.AggregateCompositions.FindAsync(new object[] { request.Id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(2), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.EffectiveDate.HasValue && request.ExpirationDate.HasValue && request.ExpirationDate < request.EffectiveDate)
            throw new InvalidOperationException("Дата окончания действия не может быть раньше даты начала.");

        comp.Comment = request.Comment;
        comp.EffectiveDate = request.EffectiveDate;
        comp.ExpirationDate = request.ExpirationDate;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateComposition", request.Id.ToString(), "UpdateDraft", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> DeleteAggregateCompositionDraftAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.AggregateCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(2), comp.BranchId, ct);
        if (comp.Status == ProductCompositionStatus.Approved || comp.Status == ProductCompositionStatus.Archived)
            throw new InvalidOperationException("Нельзя удалить утверждённый или архивный состав.");
        if (comp.Status == ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Нельзя удалить состав на проверке. Верните его в черновик.");

        _db.AggregateCompositions.Remove(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateComposition", id.ToString(), "DeleteDraft", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> SubmitAggregateCompositionForReviewAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.AggregateCompositions
            .Include(c => c.Nodes)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelSubmit(2), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Отправка на согласование возможна только из статуса «Черновик».");
        if (!comp.Nodes.Any())
            throw new InvalidOperationException("Нельзя отправить на согласование пустой состав.");
        await ChangeCompositionStatusInternalAsync<AggregateComposition>(_db.AggregateCompositions, id,
            ProductCompositionStatus.OnReview, null, ct);
        await CreateCompositionReviewWorkflowAsync(2, comp.Id, comp.BranchId, comp.Aggregate?.Code ?? comp.AggregateId.ToString(), comp.Version, ct);
        return true;
    }

    public async Task<bool> ReturnAggregateCompositionToDraftAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var comp = await _db.AggregateCompositions.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelReturn(2), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Возврат в черновик возможен только из статуса «На проверке».");
        await ChangeCompositionStatusInternalAsync<AggregateComposition>(_db.AggregateCompositions, id,
            ProductCompositionStatus.Draft, comment, ct);
        await CloseCompositionReviewWorkflowAsync(2, comp.Id, comp.BranchId, _currentUser.GetRequiredUserId().ToString(), ct);
        await CreateCompositionAuthorWorkflowAsync(2, comp.Id, comp.BranchId, comp.AuthorId,
            comment, comp.Aggregate?.Code ?? comp.AggregateId.ToString(), comp.Version, ct);
        return true;
    }

    public async Task<bool> ApproveAggregateCompositionAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var comp = await _db.AggregateCompositions
            .Include(c => c.Nodes)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelApprove(2), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Утверждение возможно только для состава в статусе «На проверке».");
        if (!comp.Nodes.Any())
            throw new InvalidOperationException("Нельзя утвердить пустой состав.");

        if (comp.SupersedesAggregateCompositionId.HasValue)
        {
            var predecessor = await _db.AggregateCompositions
                .FirstOrDefaultAsync(c => c.Id == comp.SupersedesAggregateCompositionId.Value, ct);
            if (predecessor == null)
                throw new InvalidOperationException("Не найден предшествующий состав для новой версии.");
            if (predecessor.AggregateId != comp.AggregateId)
                throw new InvalidOperationException("Предшествующий состав относится к другому агрегату.");
            if (predecessor.BranchId != comp.BranchId)
                throw new InvalidOperationException("Предшествующий состав относится к другому филиалу.");
            if (predecessor.Status is ProductCompositionStatus.Draft or ProductCompositionStatus.OnReview)
                throw new InvalidOperationException("Предшествующий состав ещё не утверждён.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var active = await _db.AggregateCompositions
            .Where(c => c.AggregateId == comp.AggregateId && c.Id != comp.Id && c.IsActive)
            .ToListAsync(ct);
        foreach (var p in active)
        {
            p.Status = ProductCompositionStatus.Archived;
            p.IsActive = false;
            p.UpdatedAt = now;
        }

        comp.Status = ProductCompositionStatus.Approved;
        comp.ApprovedByUserId = _currentUser.GetRequiredUserId().ToString();
        comp.ApprovedAt = now;
        comp.EffectiveDate ??= now;
        comp.IsActive = true;
        comp.UpdatedAt = now;
        comp.Comment = comment ?? comp.Comment;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateComposition", id.ToString(), "AggregateComposition.Approved", _currentUser.GetRequiredUserId()));
        await CloseCompositionReviewWorkflowAsync(2, comp.Id, comp.BranchId, _currentUser.GetRequiredUserId().ToString(), ct);
        await NotifyCompositionApprovedWorkflowAsync(2, comp.Id, comp.BranchId, comp.AuthorId,
            comp.Aggregate?.Code ?? comp.AggregateId.ToString(), comp.Version, ct);
        await CreateCompositionReadinessWorkflowAsync(2, comp.Id, comp.BranchId, ct);
        return true;
    }

    public async Task<bool> ArchiveAggregateCompositionAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.AggregateCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelApprove(2), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Approved)
            throw new InvalidOperationException("Архивирование разрешено только для утверждённого состава.");

        comp.Status = ProductCompositionStatus.Archived;
        comp.IsActive = false;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateComposition", id.ToString(), "AggregateComposition.Archived", _currentUser.GetRequiredUserId()));
        return true;
    }

    // ── AggregateComposition Nodes ──────────────────────────────

    public async Task<AggregateCompositionNode> AddAggregateCompositionNodeAsync(AddAggregateCompositionNodeRequest request, CancellationToken ct = default)
    {
        var comp = await _db.AggregateCompositions
            .Include(c => c.Nodes)
            .FirstOrDefaultAsync(c => c.Id == request.AggregateCompositionId, ct);
        if (comp == null) throw new InvalidOperationException("Состав агрегата не найден.");
        await EnsureCompositionAccessAsync(LevelEditDraft(2), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.Quantity <= 0)
            throw new ArgumentException("Количество должно быть больше 0.");

        var nodeExists = await _db.Nodes.AnyAsync(n => n.Id == request.NodeId && !n.IsDeleted, ct);
        if (!nodeExists) throw new InvalidOperationException("Узел не найден.");
        if (comp.Nodes.Any(n => n.NodeId == request.NodeId))
            throw new InvalidOperationException("Узел уже добавлен в состав.");

        var acn = new AggregateCompositionNode
        {
            Id = Guid.NewGuid(),
            AggregateCompositionId = request.AggregateCompositionId,
            NodeId = request.NodeId,
            Quantity = request.Quantity,
            Notes = request.Notes
        };
        _db.AggregateCompositionNodes.Add(acn);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateCompositionNode", acn.Id.ToString(), "AggregateComposition.NodeAdded", _currentUser.GetRequiredUserId()));
        return await _db.AggregateCompositionNodes.Include(n => n.Node).FirstAsync(n => n.Id == acn.Id, ct);
    }

    public async Task<bool> UpdateAggregateCompositionNodeAsync(UpdateAggregateCompositionNodeRequest request, CancellationToken ct = default)
    {
        var acn = await _db.AggregateCompositionNodes
            .Include(n => n.AggregateComposition)
            .FirstOrDefaultAsync(n => n.Id == request.Id, ct);
        if (acn == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(2), acn.AggregateComposition.BranchId, ct);
        if (acn.AggregateComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        acn.Quantity = request.Quantity;
        acn.SortOrder = request.SortOrder;
        acn.Notes = request.Notes;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateCompositionNode", request.Id.ToString(), "AggregateComposition.NodeUpdated", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> RemoveAggregateCompositionNodeAsync(Guid id, CancellationToken ct = default)
    {
        var acn = await _db.AggregateCompositionNodes
            .Include(n => n.AggregateComposition)
            .FirstOrDefaultAsync(n => n.Id == id, ct);
        if (acn == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(2), acn.AggregateComposition.BranchId, ct);
        if (acn.AggregateComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление узлов разрешено только в статусе «Черновик».");

        _db.AggregateCompositionNodes.Remove(acn);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("AggregateCompositionNode", id.ToString(), "AggregateComposition.NodeRemoved", _currentUser.GetRequiredUserId()));
        return true;
    }

    // ── Complex CRUD ────────────────────────────────────────────

    public Task<List<Complex>> GetComplexesAsync() =>
        _db.Complexes.OrderBy(c => c.Code).ToListAsync();

    public Task<Complex?> GetComplexAsync(Guid id) =>
        _db.Complexes.FirstOrDefaultAsync(c => c.Id == id);

    public async Task<PagedResult<Complex>> GetComplexesPagedAsync(ComplexQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<Complex> queryable = _db.Complexes;

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(c => c.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(c => !c.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(c => EF.Functions.ILike(c.Code, $"%{term}%")
                || EF.Functions.ILike(c.Name, $"%{term}%")
                || EF.Functions.ILike(c.Description ?? "", $"%{term}%"));
        }

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<Complex> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? queryable.OrderByDescending(c => c.Name).ThenByDescending(c => c.Code)
                : queryable.OrderBy(c => c.Name).ThenBy(c => c.Code),
            _ => query.SortDescending
                ? queryable.OrderByDescending(c => c.Code).ThenByDescending(c => c.Name)
                : queryable.OrderBy(c => c.Code).ThenBy(c => c.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Complex>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<Complex> CreateComplexAsync(CreateComplexRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        var entity = new Complex
        {
            Id = Guid.NewGuid(),
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim()
        };
        _db.Complexes.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("Complex", entity.Id.ToString(), "Create", _currentUser.GetRequiredUserId()), ct);
        return entity;
    }

    public async Task<bool> UpdateComplexAsync(UpdateComplexRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        var c = await _db.Complexes.FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, ct);
        if (c == null) return false;
        c.Code = request.Code.Trim();
        c.Name = request.Name.Trim();
        c.Description = request.Description?.Trim();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("Complex", request.Id.ToString(), "Update", _currentUser.GetRequiredUserId()), ct);
        return true;
    }

    public async Task<(bool Deleted, string? Error)> DeleteComplexAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var c = await _db.Complexes.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c == null || c.IsDeleted) return (false, null);
        var inActiveComposition = await _db.ComplexCompositions
            .AnyAsync(cc => cc.ComplexId == id && cc.IsActive, ct);
        if (inActiveComposition) return (false, "Нельзя удалить: комплекс используется в активном составе.");
        var inApprovedHK = await _db.ComplexCompositionItems
            .AnyAsync(cci => cci.ComplexComposition.ComplexId == id
                          && cci.ComplexComposition.IsActive
                          && cci.EquipmentModel.ProductCompositions
                              .Any(pc => pc.IsActive
                                  && pc.Parts.SelectMany(p => p.Aggregates)
                                      .Any(pa => pa.Aggregate.AggregateCompositions
                                          .Any(ac => ac.IsActive
                                              && ac.Nodes.Any(n => n.Node.HKCards.Any(h => h.Status == HKCardStatus.Approved))))), ct);
        if (inApprovedHK) return (false, "Нельзя удалить: комплекс используется в утверждённой ХК.");
        c.IsDeleted = true;
        c.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("Complex", id.ToString(), "Delete", _currentUser.GetRequiredUserId()), ct);
        return (true, null);
    }

    public async Task<bool> RestoreComplexAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var c = await _db.Complexes.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c == null || !c.IsDeleted) return false;

        c.IsDeleted = false;
        c.DeletedAt = null;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("Complex", id.ToString(), "Restore", _currentUser.GetRequiredUserId()), ct);
        return true;
    }

    // ── ComplexComposition ──────────────────────────────────────

    public Task<List<ComplexComposition>> GetComplexCompositionsAsync(Guid complexId, CancellationToken ct = default) =>
        _db.ComplexCompositions
            .Include(c => c.Items.OrderBy(i => i.SortOrder)).ThenInclude(i => i.EquipmentModel)
            .Where(c => c.ComplexId == complexId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public Task<ComplexComposition?> GetComplexCompositionAsync(Guid id, CancellationToken ct = default) =>
        _db.ComplexCompositions
            .Include(c => c.Items.OrderBy(i => i.SortOrder)).ThenInclude(i => i.EquipmentModel)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<ComplexComposition> CreateComplexCompositionAsync(CreateComplexCompositionRequest request, CancellationToken ct = default)
    {
        await EnsureCompositionAccessAsync(LevelCreate(0), null, ct);
        if (request.ComplexId == Guid.Empty)
            throw new ArgumentException("ComplexId is required.");
        var complex = await _db.Complexes.FindAsync(new object[] { request.ComplexId }, ct);
        if (complex == null) throw new InvalidOperationException("Комплекс не найден.");

        var now = _time.GetUtcNow();
        var branchId = await GetCurrentUserBranchAsync(ct);
        var comp = new ComplexComposition
        {
            Id = Guid.NewGuid(),
            ComplexId = request.ComplexId,
            Status = ProductCompositionStatus.Draft,
            Version = "v" + now.ToString("MMyy"),
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
            AuthorId = _currentUser.GetRequiredUserId().ToString(),
            Comment = request.Comment,
            IsActive = false,
            BranchId = branchId
        };
        _db.ComplexCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexComposition", comp.Id.ToString(), "CreateDraft", _currentUser.GetRequiredUserId()));
        return comp;
    }

    public async Task<(bool Success, Guid? CompositionId, string? Error)> CreateComplexCompositionVersionAsync(Guid sourceCompositionId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId();
        var source = await _db.ComplexCompositions
            .Include(c => c.Items.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(c => c.Id == sourceCompositionId, ct);
        if (source == null)
            return (false, null, "Состав комплекса не найден.");
        if (source.Status is not (ProductCompositionStatus.Approved or ProductCompositionStatus.Archived))
            return (false, null, "Новую версию можно создать только из утверждённого или архивного состава.");
        await EnsureCompositionAccessAsync(LevelCreateVersion(0), source.BranchId, ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var baseVersion = "v" + now.ToString("MMyy");
        var existingVersions = await _db.ComplexCompositions
            .Where(c => c.ComplexId == source.ComplexId && c.Version.StartsWith(baseVersion))
            .Select(c => c.Version)
            .ToListAsync(ct);
        var version = BuildUniqueVersion(baseVersion, existingVersions);

        var comp = new ComplexComposition
        {
            Id = Guid.NewGuid(),
            ComplexId = source.ComplexId,
            Status = ProductCompositionStatus.Draft,
            Version = version,
            CreatedAt = now,
            UpdatedAt = now,
            AuthorId = userId.ToString(),
            Comment = $"Новая версия на основе {source.Version}",
            IsActive = false,
            BranchId = source.BranchId,
            SupersedesComplexCompositionId = source.Id,
            Items = source.Items.Select(i => new ComplexCompositionItem
            {
                Id = Guid.NewGuid(),
                ComplexCompositionId = Guid.Empty,
                EquipmentModelId = i.EquipmentModelId,
                Quantity = i.Quantity,
                SortOrder = i.SortOrder,
                Notes = i.Notes
            }).ToList()
        };

        foreach (var item in comp.Items)
            item.ComplexCompositionId = comp.Id;

        _db.ComplexCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexComposition", comp.Id.ToString(), "ComplexComposition.NewVersionCreated", userId,
            EntityDisplayName: comp.Version,
            Details: $"Создана новая версия состава комплекса на основе {source.Version} (Id {source.Id}). Новая версия: {comp.Version} (Id {comp.Id})."), ct);
        return (true, comp.Id, null);
    }

    public async Task<bool> UpdateComplexCompositionDraftAsync(UpdateComplexCompositionDraftRequest request, CancellationToken ct = default)
    {
        var comp = await _db.ComplexCompositions.FindAsync(new object[] { request.Id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(0), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.EffectiveDate.HasValue && request.ExpirationDate.HasValue && request.ExpirationDate < request.EffectiveDate)
            throw new InvalidOperationException("Дата окончания действия не может быть раньше даты начала.");

        comp.Comment = request.Comment;
        comp.EffectiveDate = request.EffectiveDate;
        comp.ExpirationDate = request.ExpirationDate;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexComposition", request.Id.ToString(), "UpdateDraft", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> DeleteComplexCompositionDraftAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.ComplexCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(0), comp.BranchId, ct);
        if (comp.Status == ProductCompositionStatus.Approved || comp.Status == ProductCompositionStatus.Archived)
            throw new InvalidOperationException("Нельзя удалить утверждённый или архивный состав.");
        if (comp.Status == ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Нельзя удалить состав на проверке. Верните его в черновик.");

        _db.ComplexCompositions.Remove(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexComposition", id.ToString(), "DeleteDraft", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> SubmitComplexCompositionForReviewAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.ComplexCompositions
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelSubmit(0), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Отправка на согласование возможна только из статуса «Черновик».");
        if (!comp.Items.Any())
            throw new InvalidOperationException("Нельзя отправить на согласование пустой состав.");
        await ChangeCompositionStatusInternalAsync<ComplexComposition>(_db.ComplexCompositions, id,
            ProductCompositionStatus.OnReview, null, ct);
        await CreateCompositionReviewWorkflowAsync(0, comp.Id, comp.BranchId, comp.Complex?.Code ?? comp.ComplexId.ToString(), comp.Version, ct);
        return true;
    }

    public async Task<bool> ReturnComplexCompositionToDraftAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var comp = await _db.ComplexCompositions.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelReturn(0), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Возврат в черновик возможен только из статуса «На проверке».");
        await ChangeCompositionStatusInternalAsync<ComplexComposition>(_db.ComplexCompositions, id,
            ProductCompositionStatus.Draft, comment, ct);
        await CloseCompositionReviewWorkflowAsync(0, comp.Id, comp.BranchId, _currentUser.GetRequiredUserId().ToString(), ct);
        await CreateCompositionAuthorWorkflowAsync(0, comp.Id, comp.BranchId, comp.AuthorId,
            comment, comp.Complex?.Code ?? comp.ComplexId.ToString(), comp.Version, ct);
        return true;
    }

    public async Task<bool> ApproveComplexCompositionAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var comp = await _db.ComplexCompositions
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelApprove(0), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Утверждение возможно только для состава в статусе «На проверке».");
        if (!comp.Items.Any())
            throw new InvalidOperationException("Нельзя утвердить пустой состав.");

        if (comp.SupersedesComplexCompositionId.HasValue)
        {
            var predecessor = await _db.ComplexCompositions
                .FirstOrDefaultAsync(c => c.Id == comp.SupersedesComplexCompositionId.Value, ct);
            if (predecessor == null)
                throw new InvalidOperationException("Не найден предшествующий состав для новой версии.");
            if (predecessor.ComplexId != comp.ComplexId)
                throw new InvalidOperationException("Предшествующий состав относится к другому комплексу.");
            if (predecessor.BranchId != comp.BranchId)
                throw new InvalidOperationException("Предшествующий состав относится к другому филиалу.");
            if (predecessor.Status is ProductCompositionStatus.Draft or ProductCompositionStatus.OnReview)
                throw new InvalidOperationException("Предшествующий состав ещё не утверждён.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var active = await _db.ComplexCompositions
            .Where(c => c.ComplexId == comp.ComplexId && c.Id != comp.Id && c.IsActive)
            .ToListAsync(ct);
        foreach (var p in active)
        {
            p.Status = ProductCompositionStatus.Archived;
            p.IsActive = false;
            p.UpdatedAt = now;
        }

        comp.Status = ProductCompositionStatus.Approved;
        comp.ApprovedByUserId = _currentUser.GetRequiredUserId().ToString();
        comp.ApprovedAt = now;
        comp.EffectiveDate ??= now;
        comp.IsActive = true;
        comp.UpdatedAt = now;
        comp.Comment = comment ?? comp.Comment;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexComposition", id.ToString(), "ComplexComposition.Approved", _currentUser.GetRequiredUserId()));
        await CloseCompositionReviewWorkflowAsync(0, comp.Id, comp.BranchId, _currentUser.GetRequiredUserId().ToString(), ct);
        await NotifyCompositionApprovedWorkflowAsync(0, comp.Id, comp.BranchId, comp.AuthorId,
            comp.Complex?.Code ?? comp.ComplexId.ToString(), comp.Version, ct);
        await CreateCompositionReadinessWorkflowAsync(0, comp.Id, comp.BranchId, ct);
        return true;
    }

    public async Task<bool> ArchiveComplexCompositionAsync(Guid id, CancellationToken ct = default)
    {
        var comp = await _db.ComplexCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        await EnsureCompositionAccessAsync(LevelApprove(0), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Approved)
            throw new InvalidOperationException("Архивирование разрешено только для утверждённого состава.");

        comp.Status = ProductCompositionStatus.Archived;
        comp.IsActive = false;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexComposition", id.ToString(), "ComplexComposition.Archived", _currentUser.GetRequiredUserId()));
        return true;
    }

    // ── ComplexComposition Items ────────────────────────────────

    public async Task<ComplexCompositionItem> AddComplexCompositionItemAsync(AddComplexCompositionItemRequest request, CancellationToken ct = default)
    {
        var comp = await _db.ComplexCompositions
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == request.CompositionId, ct);
        if (comp == null) throw new InvalidOperationException("Состав комплекса не найден.");
        await EnsureCompositionAccessAsync(LevelEditDraft(0), comp.BranchId, ct);
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.Quantity <= 0)
            throw new ArgumentException("Количество должно быть больше 0.");

        var modelExists = await _db.EquipmentModels.AnyAsync(m => m.Id == request.EquipmentModelId && !m.IsDeleted, ct);
        if (!modelExists) throw new InvalidOperationException("Модель техники не найдена.");
        if (comp.Items.Any(i => i.EquipmentModelId == request.EquipmentModelId))
            throw new InvalidOperationException("Модель уже добавлена в состав.");

        var item = new ComplexCompositionItem
        {
            Id = Guid.NewGuid(),
            ComplexCompositionId = request.CompositionId,
            EquipmentModelId = request.EquipmentModelId,
            Quantity = request.Quantity
        };
        _db.ComplexCompositionItems.Add(item);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexCompositionItem", item.Id.ToString(), "ComplexComposition.ItemAdded", _currentUser.GetRequiredUserId()));
        return await _db.ComplexCompositionItems.Include(i => i.EquipmentModel).FirstAsync(i => i.Id == item.Id, ct);
    }

    public async Task<bool> UpdateComplexCompositionItemAsync(UpdateComplexCompositionItemRequest request, CancellationToken ct = default)
    {
        var item = await _db.ComplexCompositionItems
            .Include(i => i.ComplexComposition)
            .FirstOrDefaultAsync(i => i.Id == request.Id, ct);
        if (item == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(0), item.ComplexComposition.BranchId, ct);
        if (item.ComplexComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        item.Quantity = request.Quantity;
        item.SortOrder = request.SortOrder;
        item.Notes = request.Notes;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexCompositionItem", request.Id.ToString(), "ComplexComposition.ItemUpdated", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> RemoveComplexCompositionItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.ComplexCompositionItems
            .Include(i => i.ComplexComposition)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item == null) return false;
        await EnsureCompositionAccessAsync(LevelEditDraft(0), item.ComplexComposition.BranchId, ct);
        if (item.ComplexComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление строк разрешено только в статусе «Черновик».");

        _db.ComplexCompositionItems.Remove(item);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditWriteRequest("ComplexCompositionItem", id.ToString(), "ComplexComposition.ItemRemoved", _currentUser.GetRequiredUserId()));
        return true;
    }

    // ── Generic composition status helper ───────────────────────

    private async Task<bool> ChangeCompositionStatusInternalAsync<T>(DbSet<T> dbSet, Guid id,
        ProductCompositionStatus newStatus, string? comment, CancellationToken ct) where T : class
    {
        var comp = await dbSet.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;

        var statusProp = typeof(T).GetProperty("Status");
        var updatedAtProp = typeof(T).GetProperty("UpdatedAt");
        var commentProp = typeof(T).GetProperty("Comment");
        var entityName = typeof(T).Name;

        if (statusProp == null) return false;

        var currentStatus = (ProductCompositionStatus)statusProp.GetValue(comp)!;
        var allowed = (currentStatus, newStatus) switch
        {
            (ProductCompositionStatus.Draft, ProductCompositionStatus.OnReview) => true,
            (ProductCompositionStatus.OnReview, ProductCompositionStatus.Draft) => true,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Переход из «{currentStatus}» в «{newStatus}» не допускается.");

        statusProp.SetValue(comp, newStatus);
        updatedAtProp?.SetValue(comp, _time.GetUtcNow().UtcDateTime);
        if (comment != null && commentProp != null)
            commentProp.SetValue(comp, comment);

        await _db.SaveChangesAsync(ct);

        var action = newStatus == ProductCompositionStatus.OnReview
            ? $"{entityName}.Submitted"
            : $"{entityName}.ReturnedToDraft";
        await _audit.LogAsync(new AuditWriteRequest(entityName, id.ToString(), action, _currentUser.GetRequiredUserId()));
        return true;
    }

    public Task<List<AssemblyUnit>> GetAssemblyUnitsAsync() =>
        _db.AssemblyUnits.Where(a => !a.IsDraft).OrderBy(a => a.Code).ToListAsync();

    public Task<AssemblyUnit?> GetAssemblyUnitAsync(Guid id) =>
        _db.AssemblyUnits.FirstOrDefaultAsync(a => a.Id == id);

    public async Task<PagedResult<AssemblyUnit>> GetAssemblyUnitsPagedAsync(AssemblyUnitQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<AssemblyUnit> queryable = _db.AssemblyUnits.Where(a => !a.IsDraft);

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(a => a.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(a => !a.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(a => EF.Functions.ILike(a.Code, $"%{term}%")
                || EF.Functions.ILike(a.Name, $"%{term}%")
                || EF.Functions.ILike(a.Description ?? "", $"%{term}%"));
        }

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<AssemblyUnit> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? queryable.OrderByDescending(a => a.Name).ThenByDescending(a => a.Code)
                : queryable.OrderBy(a => a.Name).ThenBy(a => a.Code),
            _ => query.SortDescending
                ? queryable.OrderByDescending(a => a.Code).ThenByDescending(a => a.Name)
                : queryable.OrderBy(a => a.Code).ThenBy(a => a.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<AssemblyUnit>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<AssemblyUnit> CreateAssemblyUnitAsync(AssemblyUnit unit)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        unit.Id = Guid.NewGuid();
        unit.IsDraft = false;
        _db.AssemblyUnits.Add(unit);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("AssemblyUnit", unit.Id.ToString(), "Create", _currentUser.GetRequiredUserId()));
        return unit;
    }

    public async Task<bool> UpdateAssemblyUnitAsync(AssemblyUnit unit)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        _db.AssemblyUnits.Update(unit);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("AssemblyUnit", unit.Id.ToString(), "Update", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> DeleteAssemblyUnitAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var a = await _db.AssemblyUnits.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null || a.IsDeleted) return false;
        a.IsDeleted = true;
        a.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("AssemblyUnit", id.ToString(), "Delete", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> RestoreAssemblyUnitAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        var a = await _db.AssemblyUnits.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null || !a.IsDeleted) return false;
        a.IsDeleted = false;
        a.DeletedAt = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("AssemblyUnit", id.ToString(), "Restore", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<PagedResult<MilitaryBranch>> GetMilitaryBranchesPagedAsync(MilitaryBranchQuery query, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<MilitaryBranch> q = _db.MilitaryBranches;

        if (query.ShowDeleted == null)
        {
            q = q.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            q = q.IgnoreQueryFilters().Where(b => b.IsDeleted);
        }
        else
        {
            q = q.Where(b => !b.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(b => EF.Functions.ILike(b.ArmedForcesType, $"%{term}%")
                || EF.Functions.ILike(b.Name, $"%{term}%")
                || EF.Functions.ILike(b.Description ?? "", $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.ArmedForcesType))
            q = q.Where(b => b.ArmedForcesType == query.ArmedForcesType);

        var totalCount = await q.CountAsync(ct);

        IOrderedQueryable<MilitaryBranch> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? q.OrderByDescending(b => b.Name).ThenByDescending(b => b.ArmedForcesType)
                : q.OrderBy(b => b.Name).ThenBy(b => b.ArmedForcesType),
            "CreatedAt" => query.SortDescending
                ? q.OrderByDescending(b => b.CreatedAt)
                : q.OrderBy(b => b.CreatedAt),
            _ => query.SortDescending
                ? q.OrderByDescending(b => b.ArmedForcesType).ThenByDescending(b => b.Name)
                : q.OrderBy(b => b.ArmedForcesType).ThenBy(b => b.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<MilitaryBranch>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<List<MilitaryBranch>> GetMilitaryBranchesAsync(
        string? search = null,
        string? armedForcesType = null,
        bool? showDeleted = false,
        string? sortBy = null,
        bool sortDesc = false,
        CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<MilitaryBranch> query = _db.MilitaryBranches;

        if (showDeleted == null)
        {
            query = query.IgnoreQueryFilters();
        }
        else if (showDeleted == true)
        {
            query = query.IgnoreQueryFilters().Where(b => b.IsDeleted);
        }
        else
        {
            query = query.Where(b => !b.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(b => EF.Functions.ILike(b.ArmedForcesType, $"%{term}%")
                || EF.Functions.ILike(b.Name, $"%{term}%")
                || EF.Functions.ILike(b.Description ?? "", $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(armedForcesType))
            query = query.Where(b => b.ArmedForcesType == armedForcesType);

        IOrderedQueryable<MilitaryBranch> ordered = sortBy switch
        {
            "Name" => sortDesc
                ? query.OrderByDescending(b => b.Name).ThenByDescending(b => b.ArmedForcesType)
                : query.OrderBy(b => b.Name).ThenBy(b => b.ArmedForcesType),
            "CreatedAt" => sortDesc
                ? query.OrderByDescending(b => b.CreatedAt)
                : query.OrderBy(b => b.CreatedAt),
            _ => sortDesc
                ? query.OrderByDescending(b => b.ArmedForcesType).ThenByDescending(b => b.Name)
                : query.OrderBy(b => b.ArmedForcesType).ThenBy(b => b.Name),
        };

        return await ordered.ToListAsync(ct);
    }

    public async Task<List<string>> GetMilitaryBranchTypesAsync(CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);
        return await _db.MilitaryBranches
            .Where(b => !b.IsDeleted)
            .OrderBy(b => b.ArmedForcesType)
            .Select(b => b.ArmedForcesType)
            .Distinct()
            .ToListAsync(ct);
    }

    private static void ValidateMilitaryBranch(MilitaryBranch branch)
    {
        branch.ArmedForcesType = branch.ArmedForcesType?.Trim() ?? "";
        branch.Name = branch.Name?.Trim() ?? "";
        branch.Description = branch.Description?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(branch.ArmedForcesType))
            throw new InvalidOperationException("Укажите вид ВС РФ.");
        if (branch.ArmedForcesType.Length > 250)
            throw new InvalidOperationException("Вид ВС РФ должен быть не длиннее 250 символов.");
        if (string.IsNullOrWhiteSpace(branch.Name))
            throw new InvalidOperationException("Укажите наименование.");
        if (branch.Name.Length > 250)
            throw new InvalidOperationException("Наименование должно быть не длиннее 250 символов.");
        if (branch.Description?.Length > 1000)
            throw new InvalidOperationException("Описание должно быть не длиннее 1000 символов.");
    }

    private async Task EnsureNoDuplicateMilitaryBranchAsync(string armedForcesType, string name, Guid? selfId, CancellationToken ct = default)
    {
        var type = armedForcesType.ToUpperInvariant();
        var normalizedName = name.ToUpperInvariant();
        var query = _db.MilitaryBranches
            .AsNoTracking()
            .Where(b => !b.IsDeleted
                && b.ArmedForcesType.ToUpper() == type
                && b.Name.ToUpper() == normalizedName);
        if (selfId.HasValue)
            query = query.Where(b => b.Id != selfId.Value);

        if (await query.AnyAsync(ct))
            throw new InvalidOperationException($"Род войск «{armedForcesType} — {name}» уже существует.");
    }

    public async Task<MilitaryBranch> CreateMilitaryBranchAsync(MilitaryBranch branch)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);
        ValidateMilitaryBranch(branch);
        await EnsureNoDuplicateMilitaryBranchAsync(branch.ArmedForcesType, branch.Name, null);
        branch.Id = Guid.NewGuid();
        branch.CreatedAt = DateTime.UtcNow;
        branch.UpdatedAt = DateTime.UtcNow;
        _db.MilitaryBranches.Add(branch);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("MilitaryBranch", branch.Id.ToString(), "Create", _currentUser.GetRequiredUserId(), EntityDisplayName: $"{branch.ArmedForcesType} — {branch.Name}"));
        return branch;
    }

    public async Task<MilitaryBranch> UpdateMilitaryBranchAsync(MilitaryBranch branch, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        ValidateMilitaryBranch(branch);

        var existing = await _db.MilitaryBranches
            .FirstOrDefaultAsync(x => x.Id == branch.Id && !x.IsDeleted, ct);

        if (existing == null)
            throw new InvalidOperationException("Не удалось изменить запись справочника. Обновите список и повторите попытку.");

        await EnsureNoDuplicateMilitaryBranchAsync(branch.ArmedForcesType, branch.Name, existing.Id, ct);

        existing.ArmedForcesType = branch.ArmedForcesType;
        existing.Name = branch.Name;
        existing.Description = branch.Description;
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("MilitaryBranch", existing.Id.ToString(), "Update", _currentUser.GetRequiredUserId(), EntityDisplayName: $"{existing.ArmedForcesType} — {existing.Name}"), ct);

        return existing;
    }

    public async Task<bool> DeleteMilitaryBranchAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);

        var hasApprovedCard = await _db.HKCardMilitaryBranches
            .AnyAsync(mb => mb.MilitaryBranchId == id
                && mb.HKCard.Status == HKCardStatus.Approved);
        if (hasApprovedCard)
            throw new InvalidOperationException("Нельзя удалить род войск, используемый в утверждённой ХК.");

        var b = await _db.MilitaryBranches.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (b == null || b.IsDeleted) return false;
        b.IsDeleted = true;
        b.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("MilitaryBranch", id.ToString(), "Delete", _currentUser.GetRequiredUserId(), EntityDisplayName: $"{b.ArmedForcesType} — {b.Name}"));
        return true;
    }

    public async Task<bool> RestoreMilitaryBranchAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit);

        var b = await _db.MilitaryBranches.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (b == null || !b.IsDeleted) return false;
        await EnsureNoDuplicateMilitaryBranchAsync(b.ArmedForcesType, b.Name, id);
        b.IsDeleted = false;
        b.DeletedAt = null;
        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("MilitaryBranch", id.ToString(), "Restore", _currentUser.GetRequiredUserId(), EntityDisplayName: $"{b.ArmedForcesType} — {b.Name}"));
        return true;
    }

    public async Task SetHKCardMilitaryBranchesAsync(Guid hkCardId, List<Guid> branchIds, CancellationToken ct = default)
    {
        var card = await _db.HKCards.FindAsync(hkCardId, ct)
            ?? throw new ArgumentException("ХК не найдена.");
        if (card.ObjectLevel != HKObjectLevel.EquipmentModel)
            throw new InvalidOperationException("Род войск применяется только к ХК изделия.");

        var existing = await _db.HKCardMilitaryBranches
            .Where(mb => mb.HKCardId == hkCardId)
            .ToListAsync(ct);

        var toRemove = existing.Where(e => !branchIds.Contains(e.MilitaryBranchId)).ToList();
        var existingIds = existing.Select(e => e.MilitaryBranchId).ToHashSet();
        var toAdd = branchIds.Where(id => !existingIds.Contains(id)).ToList();

        _db.HKCardMilitaryBranches.RemoveRange(toRemove);
        foreach (var branchId in toAdd)
        {
            _db.HKCardMilitaryBranches.Add(new HKCardMilitaryBranch
            {
                HKCardId = hkCardId,
                MilitaryBranchId = branchId
            });
        }

        if (toRemove.Count > 0 || toAdd.Count > 0)
        {
            var addedNames = toAdd.Select(id => _db.MilitaryBranches.FirstOrDefault(b => b.Id == id)?.Name ?? id.ToString());
            var removedNames = toRemove.Select(e => _db.MilitaryBranches.FirstOrDefault(b => b.Id == e.MilitaryBranchId)?.Name ?? e.MilitaryBranchId.ToString());
            var details = new List<string>();
            if (addedNames.Any()) details.Add($"Добавлены: {string.Join(", ", addedNames)}");
            if (removedNames.Any()) details.Add($"Удалены: {string.Join(", ", removedNames)}");

            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(new AuditWriteRequest("HKCard", hkCardId.ToString(), "Update", _currentUser.GetRequiredUserId(),
                EntityDisplayName: $"{card.Code} v{card.Version}",
                Details: $"Род войск: {string.Join("; ", details)}"), ct);
        }
    }

    public async Task<List<MilitaryBranch>> GetHKCardMilitaryBranchesAsync(Guid hkCardId, CancellationToken ct = default)
    {
        return await _db.HKCardMilitaryBranches
            .Where(mb => mb.HKCardId == hkCardId)
            .Select(mb => mb.MilitaryBranch)
            .OrderBy(b => b.ArmedForcesType)
            .ThenBy(b => b.Name)
            .ToListAsync(ct);
    }

    public async Task<PagedResult<EquipmentType>> GetEquipmentTypesPagedAsync(
        EquipmentTypeQuery query,
        CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        IQueryable<EquipmentType> queryable = _db.EquipmentTypes;

        if (query.ShowDeleted == null)
        {
            queryable = queryable.IgnoreQueryFilters();
        }
        else if (query.ShowDeleted == true)
        {
            queryable = queryable.IgnoreQueryFilters().Where(e => e.IsDeleted);
        }
        else
        {
            queryable = queryable.Where(e => !e.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(e => EF.Functions.ILike(e.TypeGroup ?? "", $"%{term}%")
                || EF.Functions.ILike(e.Name, $"%{term}%")
                || EF.Functions.ILike(e.Description ?? "", $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.TypeGroup))
            queryable = queryable.Where(e => e.TypeGroup == query.TypeGroup);

        var totalCount = await queryable.CountAsync(ct);

        IOrderedQueryable<EquipmentType> ordered = query.SortBy switch
        {
            "Name" => query.SortDescending
                ? queryable.OrderByDescending(e => e.Name).ThenByDescending(e => e.TypeGroup)
                : queryable.OrderBy(e => e.Name).ThenBy(e => e.TypeGroup),
            "CreatedAt" => query.SortDescending
                ? queryable.OrderByDescending(e => e.CreatedAt)
                : queryable.OrderBy(e => e.CreatedAt),
            _ => query.SortDescending
                ? queryable.OrderByDescending(e => e.TypeGroup).ThenByDescending(e => e.Name)
                : queryable.OrderBy(e => e.TypeGroup).ThenBy(e => e.Name),
        };

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<EquipmentType>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<List<EquipmentType>> GetActiveEquipmentTypesAsync(CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        return await _db.EquipmentTypes
            .Where(e => !e.IsDeleted)
            .OrderBy(e => e.TypeGroup)
            .ThenBy(e => e.Name)
            .ToListAsync(ct);
    }

    public async Task<List<string>> GetEquipmentTypeGroupsAsync(CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceView, ct);

        return await _db.EquipmentTypes
            .Where(e => !e.IsDeleted && e.TypeGroup != null)
            .Select(e => e.TypeGroup!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync(ct);
    }

    public async Task<EquipmentType> CreateEquipmentTypeAsync(EquipmentType type, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        ValidateEquipmentType(type);
        await EnsureNoDuplicateEquipmentTypeAsync(type.TypeGroup, type.Name, null, ct);

        type.Id = Guid.NewGuid();
        type.CreatedAt = _time.GetUtcNow().UtcDateTime;
        type.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        _db.EquipmentTypes.Add(type);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("EquipmentType", type.Id.ToString(), "Create", _currentUser.GetRequiredUserId(),
            EntityDisplayName: EquipmentTypeDisplayName(type)), ct);

        return type;
    }

    public async Task<EquipmentType> UpdateEquipmentTypeAsync(EquipmentType type, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);
        ValidateEquipmentType(type);

        var existing = await _db.EquipmentTypes
            .FirstOrDefaultAsync(x => x.Id == type.Id && !x.IsDeleted, ct);

        if (existing == null)
            throw new InvalidOperationException("Не удалось изменить запись справочника. Обновите список и повторите попытку.");

        await EnsureNoDuplicateEquipmentTypeAsync(type.TypeGroup, type.Name, existing.Id, ct);

        existing.TypeGroup = type.TypeGroup;
        existing.Name = type.Name;
        existing.Description = type.Description;
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("EquipmentType", existing.Id.ToString(), "Update", _currentUser.GetRequiredUserId(),
            EntityDisplayName: EquipmentTypeDisplayName(existing)), ct);

        return existing;
    }

    public async Task<bool> DeleteEquipmentTypeAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var existing = await _db.EquipmentTypes.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (existing == null || existing.IsDeleted) return false;

        existing.IsDeleted = true;
        existing.DeletedAt = _time.GetUtcNow().UtcDateTime;
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("EquipmentType", id.ToString(), "Delete", _currentUser.GetRequiredUserId(),
            EntityDisplayName: EquipmentTypeDisplayName(existing)), ct);

        return true;
    }

    public async Task<bool> RestoreEquipmentTypeAsync(Guid id, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.ReferenceEdit, ct);

        var existing = await _db.EquipmentTypes.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (existing == null || !existing.IsDeleted) return false;

        await EnsureNoDuplicateEquipmentTypeAsync(existing.TypeGroup, existing.Name, id, ct);

        existing.IsDeleted = false;
        existing.DeletedAt = null;
        existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditWriteRequest("EquipmentType", id.ToString(), "Restore", _currentUser.GetRequiredUserId(),
            EntityDisplayName: EquipmentTypeDisplayName(existing)), ct);

        return true;
    }

    private static void ValidateEquipmentType(EquipmentType type)
    {
        type.TypeGroup = string.IsNullOrWhiteSpace(type.TypeGroup) ? null : type.TypeGroup.Trim();
        type.Name = type.Name?.Trim() ?? "";
        type.Description = string.IsNullOrWhiteSpace(type.Description) ? null : type.Description.Trim();

        if (string.IsNullOrWhiteSpace(type.Name))
            throw new InvalidOperationException("Укажите наименование.");
        if (type.Name.Length > 250)
            throw new InvalidOperationException("Наименование должно быть не длиннее 250 символов.");
        if (type.TypeGroup?.Length > 250)
            throw new InvalidOperationException("Вид техники должен быть не длиннее 250 символов.");
        if (type.Description?.Length > 1000)
            throw new InvalidOperationException("Описание должно быть не длиннее 1000 символов.");
    }

    private async Task EnsureNoDuplicateEquipmentTypeAsync(string? typeGroup, string name, Guid? selfId, CancellationToken ct = default)
    {
        var group = (typeGroup ?? "").ToUpperInvariant();
        var normalizedName = name.ToUpperInvariant();

        var query = _db.EquipmentTypes
            .AsNoTracking()
            .Where(e => !e.IsDeleted
                && (e.TypeGroup ?? "").ToUpper() == group
                && e.Name.ToUpper() == normalizedName);
        if (selfId.HasValue)
            query = query.Where(e => e.Id != selfId.Value);

        if (await query.AnyAsync(ct))
            throw new InvalidOperationException($"Вид техники «{EquipmentTypeDisplayName(typeGroup, name)}» уже существует.");
    }

    private static string EquipmentTypeDisplayName(EquipmentType type) =>
        string.IsNullOrWhiteSpace(type.TypeGroup) ? type.Name : $"{type.TypeGroup} — {type.Name}";

    private static string EquipmentTypeDisplayName(string? typeGroup, string name) =>
        string.IsNullOrWhiteSpace(typeGroup) ? name : $"{typeGroup} — {name}";
}
