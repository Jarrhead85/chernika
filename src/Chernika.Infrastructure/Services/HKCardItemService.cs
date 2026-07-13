using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class HKCardItemService
{
    private readonly AppDbContext _db;

    public HKCardItemService(AppDbContext db) => _db = db;

    public async Task SaveMaterialsAsync(Guid hkCardItemId,
        IEnumerable<(Guid GsmMaterialId, GsmCategory Category)> materials)
    {
        var existing = await _db.HKCardItemMaterials
            .Where(m => m.HKCardItemId == hkCardItemId)
            .ToListAsync();
        _db.HKCardItemMaterials.RemoveRange(existing);

        var newMaterials = materials.Select(m => new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = hkCardItemId,
            GsmMaterialId = m.GsmMaterialId,
            Category = m.Category
        }).ToList();

        await _db.HKCardItemMaterials.AddRangeAsync(newMaterials);
        await _db.SaveChangesAsync();
    }
}
