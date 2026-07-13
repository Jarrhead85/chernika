using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class EquipmentService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public EquipmentService(AppDbContext db, AuditService audit, ICurrentUserService currentUser)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
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
        _db.HKCards.Include(c => c.Node).OrderByDescending(c => c.CreatedAt).ToListAsync();

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

    public Task<List<ProductComposition>> GetCompositionsAsync() =>
        _db.ProductCompositions.Include(c => c.EquipmentModel)
            .Include(c => c.Parts).ThenInclude(p => p.Nodes).ThenInclude(n => n.Node)
            .OrderByDescending(c => c.CreatedAt).ToListAsync();

    public Task<ProductComposition?> GetCompositionAsync(Guid id) =>
        _db.ProductCompositions
            .Include(c => c.EquipmentModel)
            .Include(c => c.Parts.OrderBy(p => p.SortOrder))
                .ThenInclude(p => p.Nodes.OrderBy(n => n.Node.Code))
                    .ThenInclude(n => n.Node)
            .FirstOrDefaultAsync(c => c.Id == id);

    public Task<ProductCompositionPart?> GetCompositionPartAsync(Guid partId) =>
        _db.ProductCompositionParts
            .Include(p => p.Nodes.OrderBy(n => n.Node.Code))
                .ThenInclude(n => n.Node)
            .FirstOrDefaultAsync(p => p.Id == partId);

    public Task<ProductCompositionNode?> GetCompositionNodeAsync(Guid nodeId) =>
        _db.ProductCompositionNodes
            .Include(n => n.Node)
            .FirstOrDefaultAsync(n => n.Id == nodeId);

    public async Task<ProductComposition> CreateCompositionAsync(ProductComposition comp)
    {
        comp.Id = Guid.NewGuid();
        comp.CreatedAt = DateTime.UtcNow;
        comp.IsActive = true;
        var old = await _db.ProductCompositions
            .Where(pc => pc.EquipmentModelId == comp.EquipmentModelId && pc.IsActive)
            .ToListAsync();
        foreach (var pc in old) pc.IsActive = false;
        _db.ProductCompositions.Add(comp);
        await _db.SaveChangesAsync();
        return await _db.ProductCompositions
            .Include(c => c.Parts).ThenInclude(p => p.Nodes).ThenInclude(n => n.Node)
            .FirstAsync(c => c.Id == comp.Id);
    }

    public async Task<bool> UpdateCompositionPropertiesAsync(Guid id, Guid equipmentModelId, string? comment)
    {
        var comp = await _db.ProductCompositions.FindAsync(id);
        if (comp == null) return false;
        comp.EquipmentModelId = equipmentModelId;
        comp.Comment = comment;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetActiveCompositionAsync(Guid id)
    {
        var target = await _db.ProductCompositions.FindAsync(id);
        if (target == null) return false;
        var old = await _db.ProductCompositions
            .Where(pc => pc.EquipmentModelId == target.EquipmentModelId && pc.IsActive)
            .ToListAsync();
        foreach (var pc in old) pc.IsActive = false;
        target.IsActive = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteCompositionAsync(Guid id)
    {
        var c = await _db.ProductCompositions.FindAsync(id);
        if (c == null) return false;
        _db.ProductCompositions.Remove(c);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<ProductCompositionPart> AddPartAsync(Guid compositionId, string name, int sortOrder = 0, string? description = null)
    {
        var part = new ProductCompositionPart
        {
            Id = Guid.NewGuid(),
            ProductCompositionId = compositionId,
            Name = name,
            Description = description,
            SortOrder = sortOrder
        };
        _db.ProductCompositionParts.Add(part);
        await _db.SaveChangesAsync();
        return await _db.ProductCompositionParts
            .Include(p => p.Nodes).ThenInclude(n => n.Node)
            .FirstAsync(p => p.Id == part.Id);
    }

    public async Task<ProductCompositionNode> AddNodeAsync(Guid partId, Guid nodeId, int quantity)
    {
        var node = new ProductCompositionNode
        {
            Id = Guid.NewGuid(),
            PartId = partId,
            NodeId = nodeId,
            Quantity = quantity
        };
        _db.ProductCompositionNodes.Add(node);
        await _db.SaveChangesAsync();
        return await _db.ProductCompositionNodes
            .Include(n => n.Node)
            .FirstAsync(n => n.Id == node.Id);
    }

    public async Task<bool> UpdateNodeAsync(ProductCompositionNode node)
    {
        _db.ProductCompositionNodes.Update(node);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> IsCompositionActiveByNodeAsync(Guid nodeId)
    {
        return await _db.ProductCompositionNodes
            .Include(n => n.Part)
                .ThenInclude(p => p.ProductComposition)
            .AnyAsync(n => n.Id == nodeId
                        && n.Part.ProductComposition.IsActive);
    }

    public async Task<bool> UpdateNodeQuantityAsync(Guid nodeId, int quantity)
    {
        var n = await _db.ProductCompositionNodes.FindAsync(nodeId);
        if (n == null) return false;
        n.Quantity = quantity;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveNodeAsync(Guid nodeId)
    {
        var n = await _db.ProductCompositionNodes.FindAsync(nodeId);
        if (n == null) return false;
        _db.ProductCompositionNodes.Remove(n);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemovePartAsync(Guid partId)
    {
        var p = await _db.ProductCompositionParts.FindAsync(partId);
        if (p == null) return false;
        _db.ProductCompositionParts.Remove(p);
        await _db.SaveChangesAsync();
        return true;
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
