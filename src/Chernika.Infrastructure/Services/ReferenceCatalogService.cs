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
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ReferenceCatalogService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ReferenceCatalogCounts> GetCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var models = await db.EquipmentModels.CountAsync(ct);
        var complexes = await db.Complexes.CountAsync(ct);
        var aggregates = await db.Aggregates.CountAsync(ct);
        var nodes = await db.Nodes.CountAsync(ct);
        var assemblyUnits = await db.AssemblyUnits.CountAsync(ct);
        var instances = await db.EquipmentInstances.CountAsync(ct);
        var gsm = await db.GsmMaterials.CountAsync(ct);
        var branches = await db.MilitaryBranches.CountAsync(ct);

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
