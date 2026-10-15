using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class HKCardItemService
{
    private readonly AppDbContext _db;

    public HKCardItemService(AppDbContext db) => _db = db;

    public async Task ReplaceMaterialsAsync(Guid hkCardItemId,
        IEnumerable<(Guid GsmMaterialId, GsmCategory Category)> materials)
    {
        // Раньше проверок не было вовсе: замена норм и материалов обходила
        // блокировки этапов и работала с любой картой. Теперь принимается
        // во внимание состояние карты, а не только положение строки.
        var card = await _db.HKCardItems
            .AsNoTracking()
            .Where(i => i.Id == hkCardItemId)
            .Select(i => new { i.HKCardId })
            .FirstOrDefaultAsync()
            ?? throw new ArgumentException("Строка ХК не найдена.");

        var status = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == card.HKCardId)
            .Select(c => new { c.Status, c.CurrentAcceptedRevisionId })
            .FirstOrDefaultAsync()
            ?? throw new ArgumentException("ХК не найдена.");

        if (status.Status == HKCardStatus.Approved)
            throw new InvalidOperationException(
                "ХК принята: опубликованное содержание недоступно для изменения.");

        if (status.CurrentAcceptedRevisionId.HasValue)
            throw new InvalidOperationException(
                "У карты есть принятая структура: изменение заблокировано до начала доработки.");

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
    }
}
