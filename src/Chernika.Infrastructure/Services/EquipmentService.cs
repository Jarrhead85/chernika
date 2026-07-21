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

    public EquipmentService(AppDbContext db, AuditService audit, ICurrentUserService currentUser, TimeProvider time, UserManager<ApplicationUser> userManager, IPermissionService permissions)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
        _time = time;
        _userManager = userManager;
        _permissions = permissions;
    }

    public Task<List<EquipmentModel>> GetModelsAsync() =>
        _db.EquipmentModels.OrderBy(m => m.Index).ToListAsync();

    public Task<List<Node>> GetNodesAsync() =>
        _db.Nodes.OrderBy(n => n.Code).ToListAsync();

    public Task<Node?> GetNodeAsync(Guid id) =>
        _db.Nodes.FirstOrDefaultAsync(n => n.Id == id);

    public async Task<Node> CreateNodeAsync(Node node)
    {
        node.Id = Guid.NewGuid();
        _db.Nodes.Add(node);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Node", node.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return node;
    }

    public async Task<Node> UpdateNodeAsync(Node node)
    {
        _db.Nodes.Update(node);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Node", node.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return node;
    }

    public async Task<bool> DeleteNodeAsync(Guid id)
    {
        var n = await _db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (n == null || n.IsDeleted) return false;
        n.IsDeleted = true;
        n.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Node", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
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

    public async Task<EquipmentModel> CreateModelAsync(EquipmentModel model)
    {
        model.Id = Guid.NewGuid();
        _db.EquipmentModels.Add(model);
        await _db.SaveChangesAsync();
        return model;
    }

    public async Task<EquipmentModel> UpdateModelAsync(EquipmentModel model)
    {
        _db.EquipmentModels.Update(model);
        await _db.SaveChangesAsync();
        return model;
    }

    public async Task<bool> UpdateModelPropertiesAsync(Guid id, EquipmentModel updated)
    {
        var model = await _db.EquipmentModels.FindAsync(id);
        if (model == null) return false;
        model.Index = updated.Index;
        model.Name = updated.Name;
        model.Type = updated.Type;
        model.Brand = updated.Brand;
        model.Modification = updated.Modification;
        model.Description = updated.Description;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteModelAsync(Guid id)
    {
        var m = await _db.EquipmentModels.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (m == null || m.IsDeleted) return false;
        m.IsDeleted = true;
        m.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
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
        inst.Id = Guid.NewGuid();
        _db.EquipmentInstances.Add(inst);
        await _db.SaveChangesAsync();
        return inst;
    }

    public async Task<EquipmentInstance> UpdateInstanceAsync(EquipmentInstance inst)
    {
        _db.EquipmentInstances.Update(inst);
        await _db.SaveChangesAsync();
        return inst;
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
        var i = await _db.EquipmentInstances.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (i == null || i.IsDeleted) return false;
        i.IsDeleted = true;
        i.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task EnsureCanEditCompositionAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId();
        if (!await _permissions.HasPermissionAsync(userId.ToString(), PermissionCodes.CompositionEdit))
            throw new UnauthorizedAccessException("Недостаточно прав для редактирования конструктивного состава.");
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
        await EnsureCanEditCompositionAsync(ct);
        if (request.EquipmentModelId == Guid.Empty)
            throw new ArgumentException("EquipmentModelId is required.");

        var now = _time.GetUtcNow();
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
            IsActive = false
        };

        _db.ProductCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductComposition", comp.Id.ToString(), "CreateDraft", _currentUser.GetRequiredUserId());
        return comp;
    }

    public async Task<bool> UpdateCompositionDraftAsync(UpdateCompositionDraftRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ProductCompositions.FindAsync(new object[] { request.Id }, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.EffectiveDate.HasValue && request.ExpirationDate.HasValue && request.ExpirationDate < request.EffectiveDate)
            throw new InvalidOperationException("Дата окончания действия не может быть раньше даты начала.");

        comp.Comment = request.Comment;
        comp.EffectiveDate = request.EffectiveDate;
        comp.ExpirationDate = request.ExpirationDate;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductComposition", comp.Id.ToString(), "UpdateDraft", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> DeleteCompositionDraftAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ProductCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        if (comp.Status == ProductCompositionStatus.Approved || comp.Status == ProductCompositionStatus.Archived)
            throw new InvalidOperationException("Нельзя удалить утверждённый или архивный состав.");
        if (comp.Status == ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Нельзя удалить состав на проверке. Верните его в черновик.");

        _db.ProductCompositions.Remove(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductComposition", id.ToString(), "DeleteDraft", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> SubmitForReviewAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        return await ChangeCompositionStatusInternalAsync(id, ProductCompositionStatus.OnReview, null, ct);
    }

    public async Task<bool> ReturnToDraftAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        return await ChangeCompositionStatusInternalAsync(id, ProductCompositionStatus.Draft, comment, ct);
    }

    public async Task<bool> ApproveCompositionAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ProductCompositions
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Утверждение возможно только для состава в статусе «На проверке».");
        if (!comp.Parts.Any() || !comp.Parts.SelectMany(p => p.Aggregates).Any())
            throw new InvalidOperationException("Нельзя утвердить пустой состав.");

        var now = _time.GetUtcNow().UtcDateTime;

        var previous = await _db.ProductCompositions
            .Where(c => c.EquipmentModelId == comp.EquipmentModelId
                     && c.Id != comp.Id
                     && c.IsActive)
            .ToListAsync(ct);
        foreach (var p in previous)
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
        await _audit.LogAsync("ProductComposition", id.ToString(), "Approve", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> ArchiveCompositionAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ProductCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.Approved)
            throw new InvalidOperationException("Архивирование разрешено только для утверждённого состава.");

        comp.Status = ProductCompositionStatus.Archived;
        comp.IsActive = false;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductComposition", id.ToString(), "Archive", _currentUser.GetRequiredUserId());
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

        var action = newStatus == ProductCompositionStatus.OnReview ? "SubmitForReview" : "ReturnToDraft";
        await _audit.LogAsync("ProductComposition", id.ToString(), action, _currentUser.GetRequiredUserId());
        return true;
    }

    // ── Parts ────────────────────────────────────────────────────────────

    public async Task<ProductCompositionPart> AddPartAsync(AddPartRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ProductCompositions.FindAsync(new object[] { request.CompositionId }, ct);
        if (comp == null) throw new InvalidOperationException("Состав не найден.");
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

        await _audit.LogAsync("ProductCompositionPart", part.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return await _db.ProductCompositionParts
            .Include(p => p.Aggregates).ThenInclude(a => a.Aggregate)
            .FirstAsync(p => p.Id == part.Id, ct);
    }

    public async Task<bool> UpdatePartAsync(UpdatePartRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var part = await _db.ProductCompositionParts
            .Include(p => p.ProductComposition)
            .FirstOrDefaultAsync(p => p.Id == request.PartId, ct);
        if (part == null) return false;
        if (part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        part.Name = request.Name.Trim();
        part.Description = request.Description;
        part.SortOrder = request.SortOrder;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductCompositionPart", request.PartId.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> RemovePartAsync(Guid partId, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var part = await _db.ProductCompositionParts
            .Include(p => p.ProductComposition)
            .FirstOrDefaultAsync(p => p.Id == partId, ct);
        if (part == null) return false;
        if (part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление частей разрешено только в статусе «Черновик».");

        _db.ProductCompositionParts.Remove(part);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductCompositionPart", partId.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return true;
    }

    // ── Aggregates ────────────────────────────────────────────────────────

    public async Task<ProductCompositionAggregate> AddAggregateAsync(AddProductCompositionAggregateRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
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
        if (part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        var aggregateExists = await _db.Aggregates.AnyAsync(a => a.Id == request.AggregateId && !a.IsDeleted, ct);
        if (!aggregateExists) throw new InvalidOperationException("Агрегат не найден.");

        if (part.Aggregates.Any(a => a.AggregateId == request.AggregateId))
            throw new InvalidOperationException("Агрегат уже добавлен в эту часть.");

        var pca = new ProductCompositionAggregate
        {
            Id = Guid.NewGuid(),
            PartId = request.PartId,
            AggregateId = request.AggregateId,
            Quantity = request.Quantity
        };

        _db.ProductCompositionAggregates.Add(pca);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("ProductCompositionAggregate", pca.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return await _db.ProductCompositionAggregates
            .Include(a => a.Aggregate)
            .FirstAsync(a => a.Id == pca.Id, ct);
    }

    public async Task<bool> UpdateAggregateQuantityAsync(UpdateProductCompositionAggregateRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        if (request.Quantity <= 0)
            throw new ArgumentException("Количество должно быть больше 0.");

        var pca = await _db.ProductCompositionAggregates
            .Include(a => a.Part).ThenInclude(p => p.ProductComposition)
            .FirstOrDefaultAsync(a => a.Id == request.Id, ct);
        if (pca == null) return false;
        if (pca.Part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        pca.Quantity = request.Quantity;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductCompositionAggregate", request.Id.ToString(), "UpdateQuantity", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> RemoveAggregateAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var pca = await _db.ProductCompositionAggregates
            .Include(a => a.Part).ThenInclude(p => p.ProductComposition)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (pca == null) return false;
        if (pca.Part.ProductComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление агрегатов разрешено только в статусе «Черновик».");

        _db.ProductCompositionAggregates.Remove(pca);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ProductCompositionAggregate", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> IsCompositionActiveByAggregateAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.ProductCompositionAggregates
            .Include(a => a.Part).ThenInclude(p => p.ProductComposition)
            .AnyAsync(a => a.Id == id && a.Part.ProductComposition.IsActive, ct);
    }

    public Task<List<Branch>> GetBranchesAsync() =>
        _db.Branches.OrderBy(b => b.Name).ToListAsync();

    public Task<Branch?> GetBranchAsync(Guid id) =>
        _db.Branches.FirstOrDefaultAsync(b => b.Id == id);

    public async Task<Branch> CreateBranchAsync(Branch branch)
    {
        branch.Id = Guid.NewGuid();
        _db.Branches.Add(branch);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Branch", branch.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return branch;
    }

    public async Task<bool> UpdateBranchAsync(Branch branch)
    {
        _db.Branches.Update(branch);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Branch", branch.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<(bool Deleted, string? Error)> DeleteBranchAsync(Guid id)
    {
        var b = await _db.Branches.FindAsync(id);
        if (b == null) return (false, null);

        var hasUsers = await _db.Users.AnyAsync(u => u.BranchId == id);
        if (hasUsers) return (false, "Нельзя удалить: к филиалу привязаны пользователи.");

        var hasCards = await _db.HKCards.AnyAsync(h => h.BranchId == id);
        if (hasCards) return (false, "Нельзя удалить: к филиалу привязаны карты.");

        _db.Branches.Remove(b);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Branch", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return (true, null);
    }

    public Task<List<Aggregate>> GetAggregatesAsync() =>
        _db.Aggregates.OrderBy(a => a.Code).ToListAsync();

    public Task<Aggregate?> GetAggregateAsync(Guid id) =>
        _db.Aggregates.FirstOrDefaultAsync(a => a.Id == id);

    public async Task<Aggregate> CreateAggregateAsync(CreateAggregateRequest request, CancellationToken ct = default)
    {
        var entity = new Aggregate
        {
            Id = Guid.NewGuid(),
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim()
        };
        _db.Aggregates.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("Aggregate", entity.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return entity;
    }

    public async Task<bool> UpdateAggregateAsync(UpdateAggregateRequest request, CancellationToken ct = default)
    {
        var a = await _db.Aggregates.FindAsync(new object[] { request.Id }, ct);
        if (a == null) return false;
        a.Code = request.Code.Trim();
        a.Name = request.Name.Trim();
        a.Description = request.Description?.Trim();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("Aggregate", request.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<(bool Deleted, string? Error)> DeleteAggregateAsync(Guid id, CancellationToken ct = default)
    {
        var a = await _db.Aggregates.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a == null || a.IsDeleted) return (false, null);
        var inActiveComposition = await _db.ProductCompositionAggregates
            .AnyAsync(pca => pca.AggregateId == id && pca.Part.ProductComposition.IsActive, ct);
        if (inActiveComposition) return (false, "Нельзя удалить: агрегат используется в активном составе изделия.");
        var inApprovedHK = await _db.AggregateCompositionNodes
            .AnyAsync(acn => acn.AggregateComposition.AggregateId == id
                          && acn.AggregateComposition.IsActive
                          && acn.Node.HKCards.Any(h => h.Status == HKCardStatus.Approved), ct);
        if (inApprovedHK) return (false, "Нельзя удалить: агрегат используется в утверждённой ХК (через узел).");
        a.IsDeleted = true;
        a.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("Aggregate", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return (true, null);
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

    public async Task<AggregateComposition> CreateAggregateCompositionAsync(CreateAggregateCompositionRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        if (request.AggregateId == Guid.Empty)
            throw new ArgumentException("AggregateId is required.");
        var aggregate = await _db.Aggregates.FindAsync(new object[] { request.AggregateId }, ct);
        if (aggregate == null) throw new InvalidOperationException("Агрегат не найден.");

        var now = _time.GetUtcNow();
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
            IsActive = false
        };
        _db.AggregateCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("AggregateComposition", comp.Id.ToString(), "CreateDraft", _currentUser.GetRequiredUserId());
        return comp;
    }

    public async Task<bool> UpdateAggregateCompositionDraftAsync(UpdateAggregateCompositionDraftRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.AggregateCompositions.FindAsync(new object[] { request.Id }, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.EffectiveDate.HasValue && request.ExpirationDate.HasValue && request.ExpirationDate < request.EffectiveDate)
            throw new InvalidOperationException("Дата окончания действия не может быть раньше даты начала.");

        comp.Comment = request.Comment;
        comp.EffectiveDate = request.EffectiveDate;
        comp.ExpirationDate = request.ExpirationDate;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("AggregateComposition", request.Id.ToString(), "UpdateDraft", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> DeleteAggregateCompositionDraftAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.AggregateCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        if (comp.Status == ProductCompositionStatus.Approved || comp.Status == ProductCompositionStatus.Archived)
            throw new InvalidOperationException("Нельзя удалить утверждённый или архивный состав.");
        if (comp.Status == ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Нельзя удалить состав на проверке. Верните его в черновик.");

        _db.AggregateCompositions.Remove(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("AggregateComposition", id.ToString(), "DeleteDraft", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> SubmitAggregateCompositionForReviewAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        return await ChangeCompositionStatusInternalAsync<AggregateComposition>(_db.AggregateCompositions, id,
            ProductCompositionStatus.OnReview, null, ct);
    }

    public async Task<bool> ReturnAggregateCompositionToDraftAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        return await ChangeCompositionStatusInternalAsync<AggregateComposition>(_db.AggregateCompositions, id,
            ProductCompositionStatus.Draft, comment, ct);
    }

    public async Task<bool> ApproveAggregateCompositionAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.AggregateCompositions
            .Include(c => c.Nodes)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Утверждение возможно только для состава в статусе «На проверке».");
        if (!comp.Nodes.Any())
            throw new InvalidOperationException("Нельзя утвердить пустой состав.");

        var now = _time.GetUtcNow().UtcDateTime;
        var previous = await _db.AggregateCompositions
            .Where(c => c.AggregateId == comp.AggregateId && c.Id != comp.Id && c.IsActive)
            .ToListAsync(ct);
        foreach (var p in previous)
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
        await _audit.LogAsync("AggregateComposition", id.ToString(), "Approve", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> ArchiveAggregateCompositionAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.AggregateCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.Approved)
            throw new InvalidOperationException("Архивирование разрешено только для утверждённого состава.");

        comp.Status = ProductCompositionStatus.Archived;
        comp.IsActive = false;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("AggregateComposition", id.ToString(), "Archive", _currentUser.GetRequiredUserId());
        return true;
    }

    // ── AggregateComposition Nodes ──────────────────────────────

    public async Task<AggregateCompositionNode> AddAggregateCompositionNodeAsync(AddAggregateCompositionNodeRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.AggregateCompositions
            .Include(c => c.Nodes)
            .FirstOrDefaultAsync(c => c.Id == request.AggregateCompositionId, ct);
        if (comp == null) throw new InvalidOperationException("Состав агрегата не найден.");
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
        await _audit.LogAsync("AggregateCompositionNode", acn.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return await _db.AggregateCompositionNodes.Include(n => n.Node).FirstAsync(n => n.Id == acn.Id, ct);
    }

    public async Task<bool> UpdateAggregateCompositionNodeAsync(UpdateAggregateCompositionNodeRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var acn = await _db.AggregateCompositionNodes
            .Include(n => n.AggregateComposition)
            .FirstOrDefaultAsync(n => n.Id == request.Id, ct);
        if (acn == null) return false;
        if (acn.AggregateComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        acn.Quantity = request.Quantity;
        acn.SortOrder = request.SortOrder;
        acn.Notes = request.Notes;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("AggregateCompositionNode", request.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> RemoveAggregateCompositionNodeAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var acn = await _db.AggregateCompositionNodes
            .Include(n => n.AggregateComposition)
            .FirstOrDefaultAsync(n => n.Id == id, ct);
        if (acn == null) return false;
        if (acn.AggregateComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление узлов разрешено только в статусе «Черновик».");

        _db.AggregateCompositionNodes.Remove(acn);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("AggregateCompositionNode", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return true;
    }

    // ── Complex CRUD ────────────────────────────────────────────

    public Task<List<Complex>> GetComplexesAsync() =>
        _db.Complexes.OrderBy(c => c.Code).ToListAsync();

    public Task<Complex?> GetComplexAsync(Guid id) =>
        _db.Complexes.FirstOrDefaultAsync(c => c.Id == id);

    public async Task<Complex> CreateComplexAsync(CreateComplexRequest request, CancellationToken ct = default)
    {
        var entity = new Complex
        {
            Id = Guid.NewGuid(),
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim()
        };
        _db.Complexes.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("Complex", entity.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return entity;
    }

    public async Task<bool> UpdateComplexAsync(UpdateComplexRequest request, CancellationToken ct = default)
    {
        var c = await _db.Complexes.FindAsync(new object[] { request.Id }, ct);
        if (c == null) return false;
        c.Code = request.Code.Trim();
        c.Name = request.Name.Trim();
        c.Description = request.Description?.Trim();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("Complex", request.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<(bool Deleted, string? Error)> DeleteComplexAsync(Guid id, CancellationToken ct = default)
    {
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
        c.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("Complex", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return (true, null);
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
        await EnsureCanEditCompositionAsync(ct);
        if (request.ComplexId == Guid.Empty)
            throw new ArgumentException("ComplexId is required.");
        var complex = await _db.Complexes.FindAsync(new object[] { request.ComplexId }, ct);
        if (complex == null) throw new InvalidOperationException("Комплекс не найден.");

        var now = _time.GetUtcNow();
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
            IsActive = false
        };
        _db.ComplexCompositions.Add(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ComplexComposition", comp.Id.ToString(), "CreateDraft", _currentUser.GetRequiredUserId());
        return comp;
    }

    public async Task<bool> UpdateComplexCompositionDraftAsync(UpdateComplexCompositionDraftRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ComplexCompositions.FindAsync(new object[] { request.Id }, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");
        if (request.EffectiveDate.HasValue && request.ExpirationDate.HasValue && request.ExpirationDate < request.EffectiveDate)
            throw new InvalidOperationException("Дата окончания действия не может быть раньше даты начала.");

        comp.Comment = request.Comment;
        comp.EffectiveDate = request.EffectiveDate;
        comp.ExpirationDate = request.ExpirationDate;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ComplexComposition", request.Id.ToString(), "UpdateDraft", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> DeleteComplexCompositionDraftAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ComplexCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        if (comp.Status == ProductCompositionStatus.Approved || comp.Status == ProductCompositionStatus.Archived)
            throw new InvalidOperationException("Нельзя удалить утверждённый или архивный состав.");
        if (comp.Status == ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Нельзя удалить состав на проверке. Верните его в черновик.");

        _db.ComplexCompositions.Remove(comp);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ComplexComposition", id.ToString(), "DeleteDraft", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> SubmitComplexCompositionForReviewAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        return await ChangeCompositionStatusInternalAsync<ComplexComposition>(_db.ComplexCompositions, id,
            ProductCompositionStatus.OnReview, null, ct);
    }

    public async Task<bool> ReturnComplexCompositionToDraftAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        return await ChangeCompositionStatusInternalAsync<ComplexComposition>(_db.ComplexCompositions, id,
            ProductCompositionStatus.Draft, comment, ct);
    }

    public async Task<bool> ApproveComplexCompositionAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ComplexCompositions
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.OnReview)
            throw new InvalidOperationException("Утверждение возможно только для состава в статусе «На проверке».");
        if (!comp.Items.Any())
            throw new InvalidOperationException("Нельзя утвердить пустой состав.");

        var now = _time.GetUtcNow().UtcDateTime;
        var previous = await _db.ComplexCompositions
            .Where(c => c.ComplexId == comp.ComplexId && c.Id != comp.Id && c.IsActive)
            .ToListAsync(ct);
        foreach (var p in previous)
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
        await _audit.LogAsync("ComplexComposition", id.ToString(), "Approve", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> ArchiveComplexCompositionAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ComplexCompositions.FindAsync(new object[] { id }, ct);
        if (comp == null) return false;
        if (comp.Status != ProductCompositionStatus.Approved)
            throw new InvalidOperationException("Архивирование разрешено только для утверждённого состава.");

        comp.Status = ProductCompositionStatus.Archived;
        comp.IsActive = false;
        comp.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ComplexComposition", id.ToString(), "Archive", _currentUser.GetRequiredUserId());
        return true;
    }

    // ── ComplexComposition Items ────────────────────────────────

    public async Task<ComplexCompositionItem> AddComplexCompositionItemAsync(AddComplexCompositionItemRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var comp = await _db.ComplexCompositions
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == request.CompositionId, ct);
        if (comp == null) throw new InvalidOperationException("Состав комплекса не найден.");
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
        await _audit.LogAsync("ComplexCompositionItem", item.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return await _db.ComplexCompositionItems.Include(i => i.EquipmentModel).FirstAsync(i => i.Id == item.Id, ct);
    }

    public async Task<bool> UpdateComplexCompositionItemAsync(UpdateComplexCompositionItemRequest request, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var item = await _db.ComplexCompositionItems
            .Include(i => i.ComplexComposition)
            .FirstOrDefaultAsync(i => i.Id == request.Id, ct);
        if (item == null) return false;
        if (item.ComplexComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Редактирование разрешено только в статусе «Черновик».");

        item.Quantity = request.Quantity;
        item.SortOrder = request.SortOrder;
        item.Notes = request.Notes;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ComplexCompositionItem", request.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> RemoveComplexCompositionItemAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureCanEditCompositionAsync(ct);
        var item = await _db.ComplexCompositionItems
            .Include(i => i.ComplexComposition)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item == null) return false;
        if (item.ComplexComposition.Status != ProductCompositionStatus.Draft)
            throw new InvalidOperationException("Удаление строк разрешено только в статусе «Черновик».");

        _db.ComplexCompositionItems.Remove(item);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("ComplexCompositionItem", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
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

        var action = newStatus == ProductCompositionStatus.OnReview ? "SubmitForReview" : "ReturnToDraft";
        await _audit.LogAsync(entityName, id.ToString(), action, _currentUser.GetRequiredUserId());
        return true;
    }

    public Task<List<AssemblyUnit>> GetAssemblyUnitsAsync() =>
        _db.AssemblyUnits.OrderBy(a => a.Code).ToListAsync();

    public Task<AssemblyUnit?> GetAssemblyUnitAsync(Guid id) =>
        _db.AssemblyUnits.FirstOrDefaultAsync(a => a.Id == id);

    public async Task<AssemblyUnit> CreateAssemblyUnitAsync(AssemblyUnit unit)
    {
        unit.Id = Guid.NewGuid();
        _db.AssemblyUnits.Add(unit);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("AssemblyUnit", unit.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return unit;
    }

    public async Task<bool> UpdateAssemblyUnitAsync(AssemblyUnit unit)
    {
        _db.AssemblyUnits.Update(unit);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("AssemblyUnit", unit.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> DeleteAssemblyUnitAsync(Guid id)
    {
        var a = await _db.AssemblyUnits.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null || a.IsDeleted) return false;
        a.IsDeleted = true;
        a.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("AssemblyUnit", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return true;
    }

    public Task<List<GsmMaterial>> GetGsmMaterialsAsync() =>
        _db.GsmMaterials.OrderBy(m => m.Name).ToListAsync();

    public Task<GsmMaterial?> GetGsmMaterialAsync(Guid id) =>
        _db.GsmMaterials.FirstOrDefaultAsync(m => m.Id == id);

    public async Task<GsmMaterial> CreateGsmMaterialAsync(GsmMaterial material)
    {
        material.Id = Guid.NewGuid();
        _db.GsmMaterials.Add(material);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("GsmMaterial", material.Id.ToString(), "Create", _currentUser.GetRequiredUserId());
        return material;
    }

    public async Task<bool> UpdateGsmMaterialAsync(GsmMaterial material)
    {
        _db.GsmMaterials.Update(material);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("GsmMaterial", material.Id.ToString(), "Update", _currentUser.GetRequiredUserId());
        return true;
    }

    public async Task<bool> DeleteGsmMaterialAsync(Guid id)
    {
        var m = await _db.GsmMaterials.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (m == null || m.IsDeleted) return false;
        m.IsDeleted = true;
        m.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("GsmMaterial", id.ToString(), "Delete", _currentUser.GetRequiredUserId());
        return true;
    }
}
