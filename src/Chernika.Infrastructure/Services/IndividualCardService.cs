using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class IndividualCardService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _time;
    private readonly IPermissionService _permissions;

    public IndividualCardService(AppDbContext db, AuditService audit, ICurrentUserService currentUser, TimeProvider time, IPermissionService permissions)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
        _time = time;
        _permissions = permissions;
    }

    public Task<PagedResult<IndividualCard>> GetPagedAsync(int page = 1, int pageSize = 50, Guid? instanceId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.IndividualCards
            .Include(c => c.EquipmentInstance).ThenInclude(i => i.EquipmentModel)
            .Include(c => c.Node)
            .Include(c => c.HKCard)
            .AsQueryable();

        if (instanceId.HasValue)
            query = query.Where(c => c.EquipmentInstanceId == instanceId.Value);

        return GetPagedInternalAsync(query, page, pageSize);
    }

    public async Task<List<IndividualCard>> GetCardsAsync() =>
        await _db.IndividualCards
            .Include(c => c.EquipmentInstance).ThenInclude(i => i.EquipmentModel)
            .Include(c => c.Node)
            .Include(c => c.HKCard)
            .ToListAsync();

    public Task<IndividualCard?> GetCardAsync(Guid id) =>
        _db.IndividualCards
            .Include(c => c.EquipmentInstance).ThenInclude(i => i.EquipmentModel)
            .Include(c => c.Node)
            .Include(c => c.HKCard).ThenInclude(h => h.Items).ThenInclude(hi => hi.AssemblyUnit)
            .Include(c => c.HKCard).ThenInclude(h => h.Items).ThenInclude(hi => hi.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(c => c.Items).ThenInclude(i => i.HKCardItem).ThenInclude(h => h.AssemblyUnit)
            .Include(c => c.Items).ThenInclude(i => i.HKCardItem).ThenInclude(h => h.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(c => c.AppliedCoefficients)
            .FirstOrDefaultAsync(c => c.Id == id);

    public Task<List<IndividualCard>> GetCardsByInstanceAsync(Guid instanceId) =>
        _db.IndividualCards
            .Include(c => c.Node)
            .Include(c => c.HKCard)
            .Include(c => c.AppliedCoefficients)
            .Where(c => c.EquipmentInstanceId == instanceId)
            .OrderBy(c => c.Node.Code).ToListAsync();

    public async Task<IndividualCard> CreateCardAsync(IndividualCard card, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardGenerate);
        card.Id = Guid.NewGuid();
        if (card.RevisionNumber < 1) card.RevisionNumber = 1;
        if (card.Status == 0) card.Status = IndividualCardStatus.Draft;
        card.CreatedAt = _time.GetUtcNow().UtcDateTime;
        _db.IndividualCards.Add(card);
        await _db.SaveChangesAsync(ct);
        return card;
    }

    public async Task<IndividualCard> UpdateCardAsync(IndividualCard card)
    {
        _db.IndividualCards.Update(card);
        await _db.SaveChangesAsync();
        return card;
    }

    public async Task<bool> UpdateNotesAsync(Guid id, string? notes)
    {
        var card = await _db.IndividualCards.FindAsync(id);
        if (card == null) return false;
        card.Notes = notes;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteCardAsync(Guid id)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardGenerate);
        var card = await _db.IndividualCards.FindAsync(id);
        if (card == null) return false;
        _db.IndividualCards.Remove(card);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<IndividualCard>> GenerateCardsForInstanceAsync(Guid instanceId, List<Guid> coefficientIds, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardGenerate);
        var instance = await _db.EquipmentInstances
            .Include(i => i.EquipmentModel)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);

        if (instance == null)
            throw new InvalidOperationException($"Экземпляр {instanceId} не найден");

        var composition = await _db.ProductCompositions
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates).ThenInclude(a => a.Aggregate)
            .Where(c => c.EquipmentModelId == instance.EquipmentModelId
                     && c.IsActive
                     && c.Status == ProductCompositionStatus.Approved)
            .FirstOrDefaultAsync(ct);

        if (composition == null)
            throw new InvalidOperationException(
                $"Для модели «{instance.EquipmentModel.Name}» нет действующего утверждённого конструктивного состава. " +
                "Создайте и утвердите состав перед генерацией индивидуальных карт.");

        var now = _time.GetUtcNow().UtcDateTime;

        var equipmentModelHK = await _db.HKCards.AnyAsync(h =>
            h.ObjectLevel == HKObjectLevel.EquipmentModel &&
            h.EquipmentModelId == instance.EquipmentModelId &&
            h.Status == HKCardStatus.Approved &&
            (!h.EffectiveDate.HasValue || h.EffectiveDate.Value <= now) &&
            (!h.ExpirationDate.HasValue || h.ExpirationDate.Value >= now), ct);

        if (!equipmentModelHK)
            throw new InvalidOperationException(
                $"Для изделия «{instance.EquipmentModel.Name}» не найдена действующая утверждённая ХК на уровне изделия. " +
                "Создайте и утвердите ХК изделия перед генерацией индивидуальных карт.");

        var aggregateIds = composition.Parts.SelectMany(p => p.Aggregates).Select(a => a.AggregateId).ToList();
        if (aggregateIds.Count != 0)
        {
            var approvedAggregateHKCount = await _db.HKCards.CountAsync(h =>
                h.ObjectLevel == HKObjectLevel.Aggregate &&
                aggregateIds.Contains(h.AggregateId!.Value) &&
                h.Status == HKCardStatus.Approved &&
                (!h.EffectiveDate.HasValue || h.EffectiveDate.Value <= now) &&
                (!h.ExpirationDate.HasValue || h.ExpirationDate.Value >= now), ct);

            if (approvedAggregateHKCount != aggregateIds.Count)
            {
                var aggregatesWithHK = await _db.HKCards
                    .Where(h => h.ObjectLevel == HKObjectLevel.Aggregate && aggregateIds.Contains(h.AggregateId!.Value)
                        && h.Status == HKCardStatus.Approved)
                    .Select(h => h.AggregateId!.Value)
                    .ToListAsync(ct);
                var missingAggregateIds = aggregateIds.Except(aggregatesWithHK).ToList();
                var missingNames = await _db.Aggregates
                    .Where(a => missingAggregateIds.Contains(a.Id))
                    .Select(a => $"{a.Code} ({a.Name})")
                    .ToListAsync(ct);
                throw new InvalidOperationException(
                    "Не для всех агрегатов конструктивного состава найдена действующая утверждённая ХК на уровне агрегата. " +
                    "Отсутствуют ХК для агрегатов: " + string.Join(", ", missingNames) + ". " +
                    "Создайте и утвердите ХК агрегатов перед генерацией индивидуальных карт.");
            }
        }

        var coefficientProduct = await GetCoefficientProductAsync(coefficientIds);
        var appliedCoefficients = await LoadActiveCoefficientsAsync(coefficientIds);
        var version = "v" + _time.GetUtcNow().ToString("MMyy");
        var newCards = new List<IndividualCard>();        var compositionNodes = await _db.AggregateCompositionNodes
            .Include(acn => acn.AggregateComposition)
            .Include(acn => acn.Node)
            .Where(acn => aggregateIds.Contains(acn.AggregateComposition.AggregateId)
                       && acn.AggregateComposition.IsActive)
            .ToListAsync(ct);

        var aggregatesWithActiveComposition = compositionNodes
            .Select(acn => acn.AggregateComposition.AggregateId)
            .Distinct()
            .ToHashSet();
        var aggregatesWithoutComposition = aggregateIds
            .Where(id => !aggregatesWithActiveComposition.Contains(id))
            .ToList();
        if (aggregatesWithoutComposition.Any())
        {
            var missingNames = await _db.Aggregates
                .Where(a => aggregatesWithoutComposition.Contains(a.Id))
                .Select(a => $"{a.Code} ({a.Name})")
                .ToListAsync(ct);
            throw new InvalidOperationException(
                "Не для всех агрегатов конструктивного состава утверждён состав узлов. " +
                "Отсутствуют утверждённые составы для агрегатов: " + string.Join(", ", missingNames) + ". " +
                "Создайте и утвердите составы агрегатов перед генерацией индивидуальных карт.");
        }

        var nodeGroups = compositionNodes.GroupBy(acn => acn.AggregateComposition.AggregateId);

        foreach (var group in nodeGroups)
        {
            foreach (var acn in group)
            {
                var node = acn.Node;
                var hkCard = await _db.HKCards
                    .Include(h => h.Items).ThenInclude(hi => hi.AssemblyUnit)
                    .Include(h => h.Items).ThenInclude(hi => hi.Materials).ThenInclude(m => m.GsmMaterial)
                    .Where(h =>
                        h.ObjectLevel == Domain.Enums.HKObjectLevel.Node &&
                        h.NodeId == node.Id &&
                        h.Status == HKCardStatus.Approved &&
                        (!h.EffectiveDate.HasValue || h.EffectiveDate.Value <= now) &&
                        (!h.ExpirationDate.HasValue || h.ExpirationDate.Value >= now))
                    .OrderByDescending(h => h.ApprovedDate ?? h.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                if (hkCard == null)
                    throw new InvalidOperationException(
                        $"Для узла {node.Code ?? node.Id.ToString()} не найдена действующая утверждённая ХК.");

                var card = new IndividualCard
                {
                    Id = Guid.NewGuid(),
                    ObjectLevel = IndividualCardObjectLevel.EquipmentInstance,
                    Status = IndividualCardStatus.Draft,
                    RevisionNumber = 1,
                    EquipmentInstanceId = instanceId,
                    // Legacy D0 links: NodeId must stay NULL for an EquipmentInstance-level
                    // card (target FK check constraint); the legacy per-node grouping is
                    // retained through hkCard until the D6 registry replaces this path.
                    ProductCompositionId = composition.Id,
                    HKCardId = hkCard.Id,
                    BranchId = hkCard.BranchId,
                    CreatedByUserId = _currentUser.GetRequiredUserId().ToString(),
                    Version = version,
                    CreatedAt = now,
                    AppliedCoefficients = appliedCoefficients
                };

            var totalNorm = SumCalculatedNorms(hkCard.Items, coefficientProduct);
            foreach (var hkItem in hkCard.Items)
            {
                var calculatedVolume = NormCalculation.RoundToGrams(hkItem.Volume * coefficientProduct);
                card.Items.Add(new IndividualCardItem
                {
                    Id = Guid.NewGuid(),
                    HKCardItemId = hkItem.Id,
                    BaseVolume = hkItem.Volume,
                    CalculatedVolume = calculatedVolume,
                    Quantity = hkItem.Quantity
                });
            }

            card.TotalNorm = NormCalculation.RoundToGrams(totalNorm * acn.Quantity);
            newCards.Add(card);
                }
            }

        if (newCards.Count != 0)
        {
            // Legacy path code assignment: the D1 unique (Code, Version) index requires
            // distinguishable codes for the legacy one-card-per-node batch. The exact
            // ИК-ЭКЗ-{Serial}-{YYYY} format is kept for the first card; extra batch cards
            // and repeated runs get a numeric suffix. A dedicated generator replaces
            // this in the D3+ services.
            var baseCode = $"ИК-ЭКЗ-{instance.SerialNumber}-{now.Year}";
            var existingCodes = await _db.IndividualCards
                .Where(c => c.Code == baseCode || c.Code.StartsWith(baseCode + "-"))
                .Select(c => c.Code)
                .ToListAsync(ct);
            var usedCodes = existingCodes.ToHashSet(StringComparer.Ordinal);
            var suffix = 1;
            foreach (var card in newCards)
            {
                string code;
                do
                {
                    code = suffix == 1 ? baseCode : $"{baseCode}-{suffix}";
                    suffix++;
                } while (!usedCodes.Add(code));
                card.Code = code;
            }

            _db.IndividualCards.AddRange(newCards);
            await _db.SaveChangesAsync(ct);
        }

        return newCards;
    }

    public async Task<decimal> CalculateNormAsync(Guid hkCardId, List<Guid> coefficientIds)
    {
        var hkCard = await _db.HKCards
            .Include(h => h.Items)
            .FirstOrDefaultAsync(h => h.Id == hkCardId);

        if (hkCard == null) return 0;

        var coefficientProduct = await GetCoefficientProductAsync(coefficientIds);
        var totalNorm = SumCalculatedNorms(hkCard.Items, coefficientProduct);
        return NormCalculation.RoundToGrams(totalNorm);
    }

    public async Task<decimal> GetCoefficientProductAsync(List<Guid> coefficientIds)
    {
        if (coefficientIds.Count == 0) return 1.0m;

        var coefficients = await LoadActiveCoefficientsAsync(coefficientIds);
        var product = 1.0m;
        foreach (var coeff in coefficients)
            product *= coeff.Value;

        return product;
    }

    private Task<List<Coefficient>> LoadActiveCoefficientsAsync(List<Guid> coefficientIds)
    {
        if (coefficientIds.Count == 0)
            return Task.FromResult(new List<Coefficient>());

        return _db.Coefficients
            .Where(c => coefficientIds.Contains(c.Id) && c.IsActive)
            .ToListAsync();
    }

    private static async Task<PagedResult<IndividualCard>> GetPagedInternalAsync(IQueryable<IndividualCard> query, int page, int pageSize)
    {
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<IndividualCard>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<List<CoefficientType>> GetCoefficientTypesAsync() =>
        _db.CoefficientTypes.OrderBy(t => t.SortOrder).ToListAsync();

    public Task<CoefficientType?> GetCoefficientTypeAsync(Guid id) =>
        _db.CoefficientTypes.FirstOrDefaultAsync(t => t.Id == id);

    public async Task<CoefficientType> CreateCoefficientTypeAsync(CoefficientType type)
    {
        type.Id = Guid.NewGuid();
        _db.CoefficientTypes.Add(type);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("CoefficientType", type.Id.ToString(), "Create", _currentUser.GetRequiredUserId()));
        return type;
    }

    public async Task<bool> UpdateCoefficientTypeAsync(CoefficientType type)
    {
        _db.CoefficientTypes.Update(type);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("CoefficientType", type.Id.ToString(), "Update", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<(bool Deleted, string? Error)> DeleteCoefficientTypeAsync(Guid id)
    {
        var t = await _db.CoefficientTypes.FindAsync(id);
        if (t == null) return (false, null);

        var hasCoefficients = await _db.Coefficients.IgnoreQueryFilters().AnyAsync(c => c.CoefficientTypeId == id);
        if (hasCoefficients) return (false, "Нельзя удалить: тип содержит коэффициенты.");

        _db.CoefficientTypes.Remove(t);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("CoefficientType", id.ToString(), "Delete", _currentUser.GetRequiredUserId()));
        return (true, null);
    }

    public Task<List<Coefficient>> GetAvailableCoefficientsAsync() =>
        _db.Coefficients
            .Include(c => c.CoefficientType)
            .Where(c => c.IsActive)
            .OrderBy(c => c.CoefficientType.SortOrder)
            .ThenBy(c => c.SortOrder)
            .ToListAsync();

    public Task<List<Coefficient>> GetAllCoefficientsAsync() =>
        _db.Coefficients
            .Include(c => c.CoefficientType)
            .OrderBy(c => c.CoefficientType.SortOrder)
            .ThenBy(c => c.SortOrder)
            .ToListAsync();

    public Task<Coefficient?> GetCoefficientAsync(Guid id) =>
        _db.Coefficients.Include(c => c.CoefficientType).FirstOrDefaultAsync(c => c.Id == id);

    public async Task<Coefficient> CreateCoefficientAsync(Coefficient coefficient)
    {
        coefficient.Id = Guid.NewGuid();
        _db.Coefficients.Add(coefficient);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("Coefficient", coefficient.Id.ToString(), "Create", _currentUser.GetRequiredUserId()));
        return coefficient;
    }

    public async Task<bool> UpdateCoefficientAsync(Coefficient coefficient)
    {
        _db.Coefficients.Update(coefficient);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("Coefficient", coefficient.Id.ToString(), "Update", _currentUser.GetRequiredUserId()));
        return true;
    }

    public async Task<bool> DeleteCoefficientAsync(Guid id)
    {
        var c = await _db.Coefficients.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null || c.IsDeleted) return false;
        c.IsDeleted = true;
        c.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(new AuditWriteRequest("Coefficient", id.ToString(), "Delete", _currentUser.GetRequiredUserId()));
        return true;
    }

    private static decimal SumCalculatedNorms(IEnumerable<HKCardItem> items, decimal coefficientProduct)
    {
        var total = 0m;
        foreach (var item in items)
        {
            var calculatedVolume = NormCalculation.RoundToGrams(item.Volume * coefficientProduct);
            total += calculatedVolume * item.Quantity;
        }
        return total;
    }
}
