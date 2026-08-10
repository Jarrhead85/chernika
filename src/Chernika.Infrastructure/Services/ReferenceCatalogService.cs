using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public sealed class ReferenceCatalogCounts
{
    public int EquipmentModels { get; init; }
    public int Complexes { get; init; }
    public int Aggregates { get; init; }
    public int Nodes { get; init; }
    public int AssemblyUnits { get; init; }
    public int EquipmentInstances { get; init; }
    public int GsmMaterials { get; init; }
    public int MilitaryBranches { get; init; }
}

public sealed class ReferenceCatalogService
{
    private readonly AppDbContext _db;

    public ReferenceCatalogService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ReferenceCatalogCounts> GetCountsAsync(CancellationToken ct = default)
    {
        var models = await _db.EquipmentModels.CountAsync(ct);
        var complexes = await _db.Complexes.CountAsync(ct);
        var aggregates = await _db.Aggregates.CountAsync(ct);
        var nodes = await _db.Nodes.CountAsync(ct);
        var assemblyUnits = await _db.AssemblyUnits.CountAsync(ct);
        var instances = await _db.EquipmentInstances.CountAsync(ct);
        var gsm = await _db.GsmMaterials.CountAsync(ct);
        var branches = await _db.MilitaryBranches.CountAsync(ct);

        return new ReferenceCatalogCounts
        {
            EquipmentModels = models,
            Complexes = complexes,
            Aggregates = aggregates,
            Nodes = nodes,
            AssemblyUnits = assemblyUnits,
            EquipmentInstances = instances,
            GsmMaterials = gsm,
            MilitaryBranches = branches,
        };
    }
}
