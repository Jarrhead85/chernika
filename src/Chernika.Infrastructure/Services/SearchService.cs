using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class SearchService
{
    private readonly AppDbContext _db;

    public SearchService(AppDbContext db) => _db = db;

    public async Task<List<SearchResultItem>> SearchAsync(string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        var q = query.Trim();
        var results = new List<SearchResultItem>();

        var hkCards = await _db.HKCards
            .Include(c => c.Node)
            .Where(c => EF.Functions.ILike(c.Code, $"%{q}%") ||
                        EF.Functions.ILike(c.Version, $"%{q}%") ||
                        EF.Functions.ILike(c.Purpose ?? "", $"%{q}%") ||
                        EF.Functions.ILike(c.Notes ?? "", $"%{q}%") ||
                        EF.Functions.ILike(c.NormativeBasis ?? "", $"%{q}%"))
            .Take(maxResults)
            .ToListAsync();

        foreach (var c in hkCards)
        {
            var match = FindMatch(c.Code, c.Version, c.Purpose, c.Notes, c.NormativeBasis, q);
            results.Add(new SearchResultItem
            {
                EntityType = "HKCard",
                EntityTypeDisplay = "Химмотологическая карта",
                EntityId = c.Id,
                Title = $"{c.Code} (v{c.Version})",
                Subtitle = c.Node?.Name ?? "",
                ContextInfo = match != null ? $"Совпадение: {match}" : "",
                Url = $"/хк/{c.Id}"
            });
        }

        var nodes = await _db.Nodes
            .Include(n => n.HKCards)
            .Where(n => EF.Functions.ILike(n.Code, $"%{q}%") ||
                        EF.Functions.ILike(n.Name, $"%{q}%") ||
                        EF.Functions.ILike(n.Description ?? "", $"%{q}%"))
            .Take(maxResults)
            .ToListAsync();

        foreach (var n in nodes)
        {
            var hk = n.HKCards.FirstOrDefault();
            results.Add(new SearchResultItem
            {
                EntityType = "Node",
                EntityTypeDisplay = "Узел",
                EntityId = n.Id,
                Title = $"{n.Code} — {n.Name}",
                Subtitle = n.Description,
                ContextInfo = hk != null ? $"Используется в ХК: {hk.Code}" : "",
                Url = hk != null ? $"/хк/{hk.Id}" : "/справочник-узлов"
            });
        }

        var models = await _db.EquipmentModels
            .Include(m => m.ProductCompositions).ThenInclude(pc => pc.Parts).ThenInclude(p => p.Aggregates).ThenInclude(a => a.Aggregate)
            .Where(m => EF.Functions.ILike(m.Index, $"%{q}%") ||
                        EF.Functions.ILike(m.Name, $"%{q}%") ||
                        EF.Functions.ILike(m.Type ?? "", $"%{q}%") ||
                        EF.Functions.ILike(m.Brand ?? "", $"%{q}%") ||
                        EF.Functions.ILike(m.Modification ?? "", $"%{q}%"))
            .Take(maxResults)
            .ToListAsync();

        foreach (var m in models)
        {
            results.Add(new SearchResultItem
            {
                EntityType = "EquipmentModel",
                EntityTypeDisplay = "Модель техники",
                EntityId = m.Id,
                Title = $"{m.Index} — {m.Name}",
                Subtitle = $"{m.Brand} / {m.Type}",
                ContextInfo = "",
                Url = "/справочник-моделей"
            });
        }

        var instances = await _db.EquipmentInstances
            .Include(i => i.EquipmentModel).ThenInclude(m => m.ProductCompositions).ThenInclude(pc => pc.Parts).ThenInclude(p => p.Aggregates).ThenInclude(a => a.Aggregate)
            .Where(i => EF.Functions.ILike(i.SerialNumber, $"%{q}%") ||
                        EF.Functions.ILike(i.Index, $"%{q}%") ||
                        EF.Functions.ILike(i.Name, $"%{q}%") ||
                        EF.Functions.ILike(i.Description ?? "", $"%{q}%"))
            .Take(maxResults)
            .ToListAsync();

        foreach (var i in instances)
        {
            results.Add(new SearchResultItem
            {
                EntityType = "EquipmentInstance",
                EntityTypeDisplay = "Экземпляр техники",
                EntityId = i.Id,
                Title = $"{i.SerialNumber} — {i.Name}",
                Subtitle = i.EquipmentModel?.Index ?? "",
                ContextInfo = "",
                Url = $"/экземпляры/{i.Id}"
            });
        }

        var materials = await _db.GsmMaterials
            .Include(m => m.HKCardItemMaterials).ThenInclude(mim => mim.HKCardItem).ThenInclude(hi => hi.HKCard)
            .Where(m => EF.Functions.ILike(m.Name, $"%{q}%") ||
                        EF.Functions.ILike(m.Type, $"%{q}%") ||
                        EF.Functions.ILike(m.Gost ?? "", $"%{q}%"))
            .Take(maxResults)
            .ToListAsync();

        foreach (var mat in materials)
        {
            var hk = mat.HKCardItemMaterials?.Select(mim => mim.HKCardItem?.HKCard)
                .FirstOrDefault(h => h != null);
            results.Add(new SearchResultItem
            {
                EntityType = "GsmMaterial",
                EntityTypeDisplay = "Марка ГСМ",
                EntityId = mat.Id,
                Title = mat.Name,
                Subtitle = $"{mat.Type} ({mat.Gost})",
                ContextInfo = hk != null ? $"Применяется в ХК: {hk.Code}" : "",
                Url = hk != null ? $"/хк/{hk.Id}" : "/состав-изделия"
            });
        }

        var assemblyUnits = await _db.AssemblyUnits
            .Include(a => a.HKCardItems).ThenInclude(hi => hi.HKCard)
            .Where(a => EF.Functions.ILike(a.Code, $"%{q}%") ||
                        EF.Functions.ILike(a.Name, $"%{q}%") ||
                        EF.Functions.ILike(a.Description ?? "", $"%{q}%"))
            .Take(maxResults)
            .ToListAsync();

        foreach (var a in assemblyUnits)
        {
            var hk = a.HKCardItems?.Select(hi => hi.HKCard).FirstOrDefault(h => h != null);
            results.Add(new SearchResultItem
            {
                EntityType = "AssemblyUnit",
                EntityTypeDisplay = "Сборочная единица",
                EntityId = a.Id,
                Title = $"{a.Code} — {a.Name}",
                Subtitle = a.Description,
                ContextInfo = hk != null ? $"Используется в ХК: {hk.Code}" : "",
                Url = hk != null ? $"/хк/{hk.Id}" : "/справочник-узлов"
            });
        }

        return results.Take(maxResults).ToList();
    }

    private static string? FindMatch(string code, string version, string? purpose, string? notes, string? normativeBasis, string query)
    {
        var q = query.ToLowerInvariant();
        if (code.ToLowerInvariant().Contains(q)) return "Код ХК";
        if (version.ToLowerInvariant().Contains(q)) return "Версия";
        if (purpose?.ToLowerInvariant().Contains(q) == true) return "Назначение";
        if (notes?.ToLowerInvariant().Contains(q) == true) return "Примечание";
        if (normativeBasis?.ToLowerInvariant().Contains(q) == true) return "Основание для разработки";
        return null;
    }
}
