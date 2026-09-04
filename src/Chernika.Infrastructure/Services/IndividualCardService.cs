using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class IndividualCardService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _time;
    private readonly IPermissionService _permissions;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndividualCardService(
        AppDbContext db,
        AuditService audit,
        ICurrentUserService currentUser,
        TimeProvider time,
        IPermissionService permissions,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
        _time = time;
        _permissions = permissions;
        _userManager = userManager;
    }

    public Task<PagedResult<IndividualCard>> GetPagedAsync(int page = 1, int pageSize = 50, Guid? instanceId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.IndividualCards
            .Include(c => c.EquipmentInstance!).ThenInclude(i => i.EquipmentModel)
            .Include(c => c.Node)
            .Include(c => c.HKCard)
            .AsQueryable();

        if (instanceId.HasValue)
            query = query.Where(c => c.EquipmentInstanceId == instanceId.Value);

        return GetPagedInternalAsync(query, page, pageSize);
    }

    public async Task<List<IndividualCard>> GetCardsAsync() =>
        await _db.IndividualCards
            .Include(c => c.EquipmentInstance!).ThenInclude(i => i.EquipmentModel)
            .Include(c => c.Node)
            .Include(c => c.HKCard)
            .ToListAsync();

    public Task<IndividualCard?> GetCardAsync(Guid id) =>
        _db.IndividualCards
            .Include(c => c.EquipmentInstance!).ThenInclude(i => i.EquipmentModel)
            .Include(c => c.Node)
            .Include(c => c.HKCard!).ThenInclude(h => h.Items).ThenInclude(hi => hi.AssemblyUnit)
            .Include(c => c.HKCard!).ThenInclude(h => h.Items).ThenInclude(hi => hi.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(c => c.Items).ThenInclude(i => i.HKCardItem!).ThenInclude(h => h.AssemblyUnit)
            .Include(c => c.Items).ThenInclude(i => i.HKCardItem!).ThenInclude(h => h.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(c => c.AppliedCoefficients)
            .FirstOrDefaultAsync(c => c.Id == id);

    public Task<List<IndividualCard>> GetCardsByInstanceAsync(Guid instanceId) =>
        _db.IndividualCards
            .Include(c => c.Node)
            .Include(c => c.HKCard)
            .Include(c => c.AppliedCoefficients)
            .Where(c => c.EquipmentInstanceId == instanceId)
            .OrderBy(c => c.Node!.Code).ToListAsync();

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

    /// <summary>
    /// Legacy D0 generation path (one card per node, no preflight, no snapshots).
    /// Locked in D2: new IndividualCards must be created through the D3+ workflow.
    /// </summary>
    public async Task<List<IndividualCard>> GenerateCardsForInstanceAsync(Guid instanceId, List<Guid> coefficientIds, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardGenerate);
        throw new InvalidOperationException(
            "Формирование ИК временно недоступно до завершения предварительной проверки нормативной цепочки.");
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


    // ── D2: Preflight and normative chain resolver ─────────────────────────

    private sealed record ChildRequirement(Guid ObjectId, string Code, string Name);

    private sealed record TargetInfo(
        string Code, string Name,
        IndividualCardObjectLevel RootLevel, Guid RootObjectId,
        string RootObjectCode, string RootObjectName,
        Guid? LinkedEquipmentModelId);

    private sealed record ComponentEdge(HKCardComponent Component, HKCard Child);

    private sealed class LevelMatch
    {
        public List<IndividualCardPreflightHKSourceDto> Sources { get; } = new();
        public List<IndividualCardNormativeGapDto> Gaps { get; } = new();
        public Dictionary<Guid, HKCard> ResolvedByObject { get; } = new();
    }

    private sealed record CompositionData(
        IndividualCardPreflightCompositionDto Dto,
        ProductComposition? ProductComposition,
        AggregateComposition? AggregateComposition,
        IReadOnlyList<ChildRequirement> Requirements);

    private static Guid? GetHKObjectId(HKCard hk, IndividualCardObjectLevel level) => level switch
    {
        IndividualCardObjectLevel.Complex => hk.ComplexId,
        IndividualCardObjectLevel.EquipmentModel => hk.EquipmentModelId,
        IndividualCardObjectLevel.Aggregate => hk.AggregateId,
        IndividualCardObjectLevel.Node => hk.NodeId,
        _ => null,
    };

    private static (string Code, string Name) GetHKObjectDisplay(HKCard hk, IndividualCardObjectLevel level) => level switch
    {
        IndividualCardObjectLevel.Complex => (hk.Complex?.Code ?? string.Empty, hk.Complex?.Name ?? string.Empty),
        IndividualCardObjectLevel.EquipmentModel => (hk.EquipmentModel?.Index ?? string.Empty, hk.EquipmentModel?.Name ?? string.Empty),
        IndividualCardObjectLevel.Aggregate => (hk.Aggregate?.Code ?? string.Empty, hk.Aggregate?.Name ?? string.Empty),
        IndividualCardObjectLevel.Node => (hk.Node?.Code ?? string.Empty, hk.Node?.Name ?? string.Empty),
        _ => (string.Empty, string.Empty),
    };

    public async Task<IndividualCardPreflightResult> BuildPreflightAsync(
        IndividualCardPreflightRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardCreateDraft, ct);

        if (request.ObjectLevel == 0 || !Enum.IsDefined(request.ObjectLevel))
            throw new InvalidOperationException("Укажите корректный уровень цели ИК.");

        var actorId = _currentUser.GetRequiredUserId();
        var isSystemAdmin = await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig, ct);

        Guid? actorBranchId = null;
        if (!isSystemAdmin)
        {
            var actor = await _userManager.FindByIdAsync(actorId.ToString())
                ?? throw new UnauthorizedAccessException("Пользователь не найден.");
            if (actor.BranchId is null || actor.BranchId == Guid.Empty)
                throw new UnauthorizedAccessException("У пользователя не указан филиал.");
            actorBranchId = actor.BranchId;
        }

        var target = await ResolveTargetAsync(request.ObjectLevel, request.ObjectId, ct)
            ?? throw new InvalidOperationException(
                $"Объект цели ИК не найден или архивирован: {IndividualCardDisplay.ObjectLevel(request.ObjectLevel)}.");

        var gaps = new List<IndividualCardNormativeGapDto>();
        var gapOrder = 0;

        // ── Root candidates ──
        var candidates = await LoadRootCandidatesAsync(target, actorBranchId, ct);

        HKCard? selectedRoot = null;
        var rootState = IndividualCardPreflightRootState.Missing;

        if (request.RootHKCardId.HasValue)
        {
            selectedRoot = await ValidateExplicitRootAsync(
                request.RootHKCardId.Value, target, actorBranchId, isSystemAdmin, ct);
            rootState = IndividualCardPreflightRootState.ExplicitlySelected;
        }
        else if (candidates.Count == 0)
        {
            rootState = IndividualCardPreflightRootState.Missing;
            gaps.Add(new IndividualCardNormativeGapDto(
                IndividualCardNormativeGapKind.MissingRootHKCard,
                target.RootLevel, target.RootObjectId,
                IndividualCardDisplay.ObjectLevel(target.RootLevel),
                target.RootObjectCode, target.RootObjectName, null,
                $"Для {IndividualCardDisplay.ObjectLevel(target.RootLevel).ToLowerInvariant()} «{target.RootObjectName}» не найдено утверждённых ХК, пригодных для использования в качестве источника ИК.",
                gapOrder++));
        }
        else if (candidates.Count == 1)
        {
            rootState = IndividualCardPreflightRootState.AutomaticallySelected;
            selectedRoot = await _db.HKCards.AsNoTracking()
                .FirstOrDefaultAsync(h => h.Id == candidates[0].HKCardId, ct);
        }
        else
        {
            rootState = IndividualCardPreflightRootState.SelectionRequired;
            gaps.Add(new IndividualCardNormativeGapDto(
                IndividualCardNormativeGapKind.RootSelectionRequired,
                target.RootLevel, target.RootObjectId,
                IndividualCardDisplay.ObjectLevel(target.RootLevel),
                target.RootObjectCode, target.RootObjectName, null,
                $"Для {IndividualCardDisplay.ObjectLevel(target.RootLevel).ToLowerInvariant()} «{target.RootObjectName}» найдено несколько утверждённых ХК. Выберите ХК вручную.",
                gapOrder++));
        }

        // ── Constructive compositions ──
        var (compositions, compositionData, compositionGaps) = await ResolveCompositionsAsync(
            request.ObjectLevel, target, ct);
        foreach (var g in compositionGaps)
            gaps.Add(g with { SortOrder = gapOrder++ });
        gapOrder += compositionGaps.Count;

        // ── Normative chain ──
        var hkSources = new List<IndividualCardPreflightHKSourceDto>();
        if (selectedRoot is not null)
        {
            hkSources.Add(ToSourceDto(selectedRoot, null, target.RootLevel,
                target.RootObjectId, target.RootObjectCode, target.RootObjectName, 0, isComplete: true));

            await ResolveChainAsync(
                selectedRoot, request.ObjectLevel, selectedRoot.BranchId,
                compositions, compositionData, hkSources, gaps, gapOrder, ct);
        }

        return new IndividualCardPreflightResult
        {
            ObjectLevel = request.ObjectLevel,
            ObjectId = request.ObjectId,
            ObjectCode = target.Code,
            ObjectName = target.Name,
            ObjectDisplayType = IndividualCardDisplay.ObjectLevel(request.ObjectLevel),
            BranchId = selectedRoot?.BranchId,
            RootState = rootState,
            RootCandidates = candidates,
            SelectedRoot = selectedRoot is null
                ? null
                : ToCandidateDto(selectedRoot, target.RootLevel, target.RootObjectId,
                    target.RootObjectCode, target.RootObjectName, 0),
            Compositions = compositions,
            HKSources = hkSources,
            NormativeGaps = gaps.OrderBy(g => g.SortOrder).ToList(),
        };
    }

    private async Task<TargetInfo?> ResolveTargetAsync(
        IndividualCardObjectLevel level, Guid objectId, CancellationToken ct)
    {
        switch (level)
        {
            case IndividualCardObjectLevel.Complex:
            {
                var row = await _db.Complexes.AsNoTracking()
                    .Where(c => c.Id == objectId && !c.IsDeleted)
                    .Select(c => new { c.Code, c.Name })
                    .FirstOrDefaultAsync(ct);
                return row is null ? null : new TargetInfo(
                    row.Code, row.Name, IndividualCardObjectLevel.Complex, objectId,
                    row.Code, row.Name, null);
            }
            case IndividualCardObjectLevel.EquipmentModel:
            {
                var row = await _db.EquipmentModels.AsNoTracking()
                    .Where(m => m.Id == objectId && !m.IsDeleted)
                    .Select(m => new { m.Index, m.Name })
                    .FirstOrDefaultAsync(ct);
                return row is null ? null : new TargetInfo(
                    row.Index, row.Name, IndividualCardObjectLevel.EquipmentModel, objectId,
                    row.Index, row.Name, objectId);
            }
            case IndividualCardObjectLevel.Aggregate:
            {
                var row = await _db.Aggregates.AsNoTracking()
                    .Where(a => a.Id == objectId && !a.IsDeleted)
                    .Select(a => new { a.Code, a.Name })
                    .FirstOrDefaultAsync(ct);
                return row is null ? null : new TargetInfo(
                    row.Code, row.Name, IndividualCardObjectLevel.Aggregate, objectId,
                    row.Code, row.Name, null);
            }
            case IndividualCardObjectLevel.Node:
            {
                var row = await _db.Nodes.AsNoTracking()
                    .Where(n => n.Id == objectId && !n.IsDeleted)
                    .Select(n => new { n.Code, n.Name })
                    .FirstOrDefaultAsync(ct);
                return row is null ? null : new TargetInfo(
                    row.Code, row.Name, IndividualCardObjectLevel.Node, objectId,
                    row.Code, row.Name, null);
            }
            case IndividualCardObjectLevel.EquipmentInstance:
            {
                var row = await _db.EquipmentInstances.AsNoTracking()
                    .Where(i => i.Id == objectId && !i.IsDeleted)
                    .Select(i => new { i.SerialNumber, i.Name, ModelId = i.EquipmentModelId })
                    .FirstOrDefaultAsync(ct);
                if (row is null) return null;
                var model = await _db.EquipmentModels.AsNoTracking()
                    .Where(m => m.Id == row.ModelId && !m.IsDeleted)
                    .Select(m => new { m.Index, m.Name })
                    .FirstOrDefaultAsync(ct);
                return new TargetInfo(
                    row.SerialNumber, row.Name,
                    IndividualCardObjectLevel.EquipmentModel, row.ModelId,
                    model?.Index ?? string.Empty, model?.Name ?? string.Empty,
                    row.ModelId);
            }
            default:
                return null;
        }
    }

    private async Task<List<IndividualCardHKCandidateDto>> LoadRootCandidatesAsync(
        TargetInfo target, Guid? actorBranchId, CancellationToken ct)
    {
        var query = _db.HKCards.AsNoTracking()
            .Where(h => h.Status == HKCardStatus.Approved
                && h.ObjectLevel == MapToHKLevel(target.RootLevel));

        query = target.RootLevel switch
        {
            IndividualCardObjectLevel.Complex => query.Where(h => h.ComplexId == target.RootObjectId),
            IndividualCardObjectLevel.EquipmentModel => query.Where(h => h.EquipmentModelId == target.RootObjectId),
            IndividualCardObjectLevel.Aggregate => query.Where(h => h.AggregateId == target.RootObjectId),
            IndividualCardObjectLevel.Node => query.Where(h => h.NodeId == target.RootObjectId),
            _ => query.Where(h => false),
        };

        if (actorBranchId.HasValue)
            query = query.Where(h => h.BranchId == actorBranchId.Value);

        var rows = await query
            .OrderBy(h => h.Code).ThenBy(h => h.Version).ThenBy(h => h.Id)
            .Take(50)
            .Select(h => new
            {
                h.Id, h.Code, h.Version, h.BranchId,
                h.ApprovedDate, h.EffectiveDate, h.ExpirationDate,
            })
            .ToListAsync(ct);

        // EffectiveDate/ExpirationDate are intentionally NOT used for filtering —
        // informational only, per the approved D2 rule.
        return rows.Select((h, index) => new IndividualCardHKCandidateDto(
            h.Id, h.Code, h.Version, target.RootLevel, target.RootObjectId,
            target.RootObjectCode, target.RootObjectName,
            h.BranchId, h.ApprovedDate, h.EffectiveDate, h.ExpirationDate, index + 1)).ToList();
    }

    private async Task<HKCard?> ValidateExplicitRootAsync(
        Guid rootHKCardId, TargetInfo target, Guid? actorBranchId, bool isSystemAdmin, CancellationToken ct)
    {
        var root = await _db.HKCards.AsNoTracking()
            .Include(h => h.Complex)
            .Include(h => h.EquipmentModel)
            .Include(h => h.Aggregate)
            .Include(h => h.Node)
            .FirstOrDefaultAsync(h => h.Id == rootHKCardId, ct);

        var objectMatches = target.RootLevel switch
        {
            IndividualCardObjectLevel.Complex => root?.ComplexId == target.RootObjectId,
            IndividualCardObjectLevel.EquipmentModel => root?.EquipmentModelId == target.RootObjectId,
            IndividualCardObjectLevel.Aggregate => root?.AggregateId == target.RootObjectId,
            IndividualCardObjectLevel.Node => root?.NodeId == target.RootObjectId,
            _ => false,
        };

        if (root is null
            || root.Status != HKCardStatus.Approved
            || root.ObjectLevel != MapToHKLevel(target.RootLevel)
            || !objectMatches
            || (!isSystemAdmin && root.BranchId != actorBranchId))
        {
            throw new InvalidOperationException(
                $"Выбранная ХК не является допустимым утверждённым источником для ИК «{target.RootObjectName}».");
        }

        return root;
    }

    private static HKObjectLevel MapToHKLevel(IndividualCardObjectLevel level) => level switch
    {
        IndividualCardObjectLevel.Complex => HKObjectLevel.Complex,
        IndividualCardObjectLevel.EquipmentModel => HKObjectLevel.EquipmentModel,
        IndividualCardObjectLevel.Aggregate => HKObjectLevel.Aggregate,
        IndividualCardObjectLevel.Node => HKObjectLevel.Node,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private static IndividualCardObjectLevel MapFromHKLevel(HKObjectLevel level) => level switch
    {
        HKObjectLevel.Complex => IndividualCardObjectLevel.Complex,
        HKObjectLevel.EquipmentModel => IndividualCardObjectLevel.EquipmentModel,
        HKObjectLevel.Aggregate => IndividualCardObjectLevel.Aggregate,
        HKObjectLevel.Node => IndividualCardObjectLevel.Node,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private static IndividualCardHKCandidateDto ToCandidateDto(
        HKCard hk, IndividualCardObjectLevel level, Guid objectId,
        string objectCode, string objectName, int sortOrder) =>
        new(hk.Id, hk.Code, hk.Version, level, objectId, objectCode, objectName,
            hk.BranchId, hk.ApprovedDate, hk.EffectiveDate, hk.ExpirationDate, sortOrder);

    private static IndividualCardPreflightHKSourceDto ToSourceDto(
        HKCard hk, Guid? parentHKCardId, IndividualCardObjectLevel level,
        Guid objectId, string objectCode, string objectName, int sortOrder, bool isComplete)
    {
        var (code, name) = GetHKObjectDisplay(hk, level);
        return new IndividualCardPreflightHKSourceDto(
            hk.Id, parentHKCardId, level, objectId, code, name,
            hk.Code, hk.Version, hk.BranchId,
            hk.ApprovedDate, hk.EffectiveDate, hk.ExpirationDate, sortOrder, isComplete);
    }

    // ── Constructive compositions ──

    private async Task<(
        IReadOnlyList<IndividualCardPreflightCompositionDto> Dtos,
        List<CompositionData> Data,
        List<IndividualCardNormativeGapDto> Gaps)> ResolveCompositionsAsync(
        IndividualCardObjectLevel level, TargetInfo target, CancellationToken ct)
    {
        var dtos = new List<IndividualCardPreflightCompositionDto>();
        var data = new List<CompositionData>();
        var gaps = new List<IndividualCardNormativeGapDto>();
        var gapOrder = 0;
        var aggregateDtoGroups = new List<List<IndividualCardPreflightAggregateDto>>();

        switch (level)
        {
            case IndividualCardObjectLevel.Node:
                break;

            case IndividualCardObjectLevel.Aggregate:
            {
                var aggComp = await _db.AggregateCompositions.AsNoTracking()
                    .Include(c => c.Nodes).ThenInclude(n => n.Node)
                    .Where(c => c.AggregateId == target.RootObjectId
                        && c.Status == ProductCompositionStatus.Approved
                        && c.IsActive)
                    .FirstOrDefaultAsync(ct);

                if (aggComp is null)
                {
                    gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.MissingApprovedComposition,
                        level, target.RootObjectId, "Агрегат", target.Code, target.Name, null,
                        $"Для агрегата «{target.Name}» отсутствует действующий утверждённый состав агрегата.",
                        gapOrder++));
                    break;
                }

                var aggregateDto = new IndividualCardPreflightAggregateDto(
                    target.RootObjectId, target.Code, target.Name, 1, 0, aggComp.Id, aggComp.Version,
                    aggComp.Nodes.OrderBy(n => n.SortOrder).Select(n =>
                        new IndividualCardPreflightNodeDto(
                            n.NodeId, n.Node.Code, n.Node.Name, n.Quantity, n.SortOrder)).ToList());

                var dto = new IndividualCardPreflightCompositionDto(
                    level, aggComp.Id, aggComp.Version, aggComp.ApprovedAt,
                    target.RootObjectId, target.Code, target.Name, 1, new[] { aggregateDto });
                dtos.Add(dto);
                data.Add(new CompositionData(
                    dto, null, aggComp,
                    aggComp.Nodes.OrderBy(n => n.SortOrder)
                        .Select(n => new ChildRequirement(n.NodeId, n.Node.Code, n.Node.Name)).ToList()));
                break;
            }

            case IndividualCardObjectLevel.EquipmentModel:
            case IndividualCardObjectLevel.EquipmentInstance:
            {
                var productComp = await _db.ProductCompositions.AsNoTracking()
                    .Include(c => c.Aggregates).ThenInclude(a => a.Aggregate)
                    .Where(c => c.EquipmentModelId == target.RootObjectId
                        && c.Status == ProductCompositionStatus.Approved
                        && c.IsActive)
                    .FirstOrDefaultAsync(ct);

                if (productComp is null)
                {
                    gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.MissingApprovedComposition,
                        IndividualCardObjectLevel.EquipmentModel, target.RootObjectId,
                        "Изделие", target.RootObjectCode, target.RootObjectName, null,
                        $"Для изделия «{target.RootObjectName}» отсутствует действующий утверждённый конструктивный состав.",
                        gapOrder++));
                    break;
                }

                var aggregateDtos = new List<IndividualCardPreflightAggregateDto>();
                var requirements = new List<ChildRequirement>();
                foreach (var a in productComp.Aggregates.OrderBy(a => a.SortOrder))
                {
                    aggregateDtos.Add(new IndividualCardPreflightAggregateDto(
                        a.AggregateId, a.Aggregate.Code, a.Aggregate.Name, a.Quantity, a.SortOrder,
                        null, null, Array.Empty<IndividualCardPreflightNodeDto>()));
                    requirements.Add(new ChildRequirement(a.AggregateId, a.Aggregate.Code, a.Aggregate.Name));
                }

                var dto = new IndividualCardPreflightCompositionDto(
                    IndividualCardObjectLevel.EquipmentModel,
                    productComp.Id, productComp.Version, productComp.ApprovedAt,
                    target.RootObjectId, target.RootObjectCode, target.RootObjectName,
                    1, aggregateDtos);
                dtos.Add(dto);
                data.Add(new CompositionData(dto, productComp, null, requirements));
                aggregateDtoGroups.Add(aggregateDtos);

                await FillAggregateCompositionsAsync(aggregateDtoGroups, gaps, gapOrder, ct);
                gapOrder += gaps.Count;
                break;
            }

            case IndividualCardObjectLevel.Complex:
            {
                var complexComp = await _db.ComplexCompositions.AsNoTracking()
                    .Include(c => c.Items).ThenInclude(i => i.EquipmentModel)
                    .Where(c => c.ComplexId == target.RootObjectId
                        && c.Status == ProductCompositionStatus.Approved
                        && c.IsActive)
                    .FirstOrDefaultAsync(ct);

                if (complexComp is null)
                {
                    gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.MissingApprovedComposition,
                        level, target.RootObjectId, "Комплекс", target.Code, target.Name, null,
                        $"Для комплекса «{target.Name}» отсутствует действующий утверждённый состав комплекса.",
                        gapOrder++));
                    break;
                }

                var itemIds = complexComp.Items.OrderBy(i => i.SortOrder)
                    .Select(i => i.EquipmentModelId).ToList();
                var productComps = await _db.ProductCompositions.AsNoTracking()
                    .Include(c => c.Aggregates).ThenInclude(a => a.Aggregate)
                    .Where(c => itemIds.Contains(c.EquipmentModelId)
                        && c.Status == ProductCompositionStatus.Approved
                        && c.IsActive)
                    .ToDictionaryAsync(c => c.EquipmentModelId, ct);

                foreach (var item in complexComp.Items.OrderBy(i => i.SortOrder))
                {
                    if (!productComps.TryGetValue(item.EquipmentModelId, out var productComp))
                    {
                        gaps.Add(new IndividualCardNormativeGapDto(
                            IndividualCardNormativeGapKind.MissingApprovedComposition,
                            IndividualCardObjectLevel.EquipmentModel, item.EquipmentModelId,
                            "Изделие", item.EquipmentModel.Index, item.EquipmentModel.Name, null,
                            $"Для изделия «{item.EquipmentModel.Name}», входящего в состав комплекса «{target.Name}», отсутствует действующий утверждённый конструктивный состав.",
                            gapOrder++));
                        continue;
                    }

                    var aggregateDtos = new List<IndividualCardPreflightAggregateDto>();
                    var requirements = new List<ChildRequirement>();
                    foreach (var a in productComp.Aggregates.OrderBy(a => a.SortOrder))
                    {
                        aggregateDtos.Add(new IndividualCardPreflightAggregateDto(
                            a.AggregateId, a.Aggregate.Code, a.Aggregate.Name, a.Quantity, a.SortOrder,
                            null, null, Array.Empty<IndividualCardPreflightNodeDto>()));
                        requirements.Add(new ChildRequirement(a.AggregateId, a.Aggregate.Code, a.Aggregate.Name));
                    }

                    var dto = new IndividualCardPreflightCompositionDto(
                        level, complexComp.Id, complexComp.Version, complexComp.ApprovedAt,
                        item.EquipmentModelId, item.EquipmentModel.Index, item.EquipmentModel.Name,
                        item.Quantity, aggregateDtos);
                    dtos.Add(dto);
                    data.Add(new CompositionData(dto, productComp, null, requirements));
                    aggregateDtoGroups.Add(aggregateDtos);
                }

                await FillAggregateCompositionsAsync(aggregateDtoGroups, gaps, gapOrder, ct);
                gapOrder += gaps.Count;
                break;
            }
        }

        return (dtos, data, gaps);
    }

    /// <summary>
    /// Fills AggregateCompositionId/Version/Nodes for all aggregate DTO groups
    /// with one batched query. Items are replaced in the caller-owned lists,
    /// which are the same list instances referenced by the composition DTOs.
    /// </summary>
    private async Task FillAggregateCompositionsAsync(
        List<List<IndividualCardPreflightAggregateDto>> aggregateDtoGroups,
        List<IndividualCardNormativeGapDto> gaps, int gapOrderStart, CancellationToken ct)
    {
        var aggregateIds = aggregateDtoGroups
            .SelectMany(g => g)
            .Select(a => a.AggregateId)
            .Distinct()
            .ToList();
        if (aggregateIds.Count == 0) return;

        var compositions = await _db.AggregateCompositions.AsNoTracking()
            .Include(c => c.Nodes).ThenInclude(n => n.Node)
            .Where(c => aggregateIds.Contains(c.AggregateId)
                && c.Status == ProductCompositionStatus.Approved
                && c.IsActive)
            .ToDictionaryAsync(c => c.AggregateId, ct);

        var gapOrder = gapOrderStart;
        foreach (var group in aggregateDtoGroups)
        {
            for (var i = 0; i < group.Count; i++)
            {
                var dto = group[i];
                if (!compositions.TryGetValue(dto.AggregateId, out var aggComp))
                {
                    gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.MissingApprovedComposition,
                        IndividualCardObjectLevel.Aggregate, dto.AggregateId,
                        "Агрегат", dto.Code, dto.Name, null,
                        $"Для агрегата «{dto.Name}» отсутствует действующий утверждённый состав агрегата.",
                        gapOrder++));
                    continue;
                }

                group[i] = dto with
                {
                    AggregateCompositionId = aggComp.Id,
                    AggregateCompositionVersion = aggComp.Version,
                    Nodes = aggComp.Nodes.OrderBy(n => n.SortOrder).Select(n =>
                        new IndividualCardPreflightNodeDto(
                            n.NodeId, n.Node.Code, n.Node.Name, n.Quantity, n.SortOrder)).ToList(),
                };
            }
        }
    }

    // ── Normative chain resolution ──

    private async Task ResolveChainAsync(
        HKCard root, IndividualCardObjectLevel level, Guid branchId,
        IReadOnlyList<IndividualCardPreflightCompositionDto> compositions,
        List<CompositionData> compositionData,
        List<IndividualCardPreflightHKSourceDto> hkSources,
        List<IndividualCardNormativeGapDto> gaps,
        int gapOrderStart, CancellationToken ct)
    {
        var gapOrder = gapOrderStart;

        // Without resolved constructive compositions there is nothing to match
        // the normative chain against; the composition gap is already reported.
        if (compositionData.Count == 0)
            return;

        switch (level)
        {
            case IndividualCardObjectLevel.Node:
                // Node root requires no children; the chain is complete with the root alone.
                break;

            case IndividualCardObjectLevel.Aggregate:
            {
                var data = compositionData[0];
                var match = await MatchChildLevelAsync(
                    IndividualCardObjectLevel.Node,
                    new[] { (Parent: root, Requirements: data.Requirements) }.ToList(),
                    branchId, hkSources, gaps, gapOrder, ct);
                gapOrder += match.Gaps.Count;
                break;
            }

            case IndividualCardObjectLevel.EquipmentModel:
            case IndividualCardObjectLevel.EquipmentInstance:
            {
                var data = compositionData[0];

                // Aggregate level under the model root.
                var aggregateMatch = await MatchChildLevelAsync(
                    IndividualCardObjectLevel.Aggregate,
                    new[] { (Parent: root, Requirements: data.Requirements) }.ToList(),
                    branchId, hkSources, gaps, gapOrder, ct);
                gapOrder += aggregateMatch.Gaps.Count;

                // Node level under each resolved aggregate HK.
                var nodeParentRequirements = new List<(HKCard Parent, IReadOnlyList<ChildRequirement> Requirements)>();
                foreach (var aggregateDto in data.Dto.Aggregates)
                {
                    if (!aggregateMatch.ResolvedByObject.TryGetValue(aggregateDto.AggregateId, out var aggregateHk))
                        continue;
                    nodeParentRequirements.Add((aggregateHk, NodesOf(aggregateDto)));
                }

                if (nodeParentRequirements.Count > 0)
                {
                    var nodeMatch = await MatchChildLevelAsync(
                        IndividualCardObjectLevel.Node, nodeParentRequirements,
                        branchId, hkSources, gaps, gapOrder, ct);
                    gapOrder += nodeMatch.Gaps.Count;
                }

                break;
            }

            case IndividualCardObjectLevel.Complex:
            {
                // Изделие level under the complex root.
                var modelRequirements = compositions
                    .Select(d => new ChildRequirement(d.TargetObjectId, d.TargetObjectCode, d.TargetObjectName))
                    .ToList();
                var modelMatch = await MatchChildLevelAsync(
                    IndividualCardObjectLevel.EquipmentModel,
                    new[] { (Parent: root, Requirements: (IReadOnlyList<ChildRequirement>)modelRequirements) }.ToList(),
                    branchId, hkSources, gaps, gapOrder, ct);
                gapOrder += modelMatch.Gaps.Count;

                // Aggregate level under each resolved изделие HK.
                var aggregateParentRequirements = new List<(HKCard Parent, IReadOnlyList<ChildRequirement> Requirements)>();
                foreach (var composition in compositions)
                {
                    if (!modelMatch.ResolvedByObject.TryGetValue(composition.TargetObjectId, out var modelHk))
                        continue;
                    aggregateParentRequirements.Add((modelHk, AggregatesOf(composition)));
                }

                LevelMatch? aggregateMatch = aggregateParentRequirements.Count > 0
                    ? await MatchChildLevelAsync(
                        IndividualCardObjectLevel.Aggregate, aggregateParentRequirements,
                        branchId, hkSources, gaps, gapOrder, ct)
                    : null;
                if (aggregateMatch is not null)
                    gapOrder += aggregateMatch.Gaps.Count;

                // Node level under each resolved aggregate HK.
                var nodeParentRequirements = new List<(HKCard Parent, IReadOnlyList<ChildRequirement> Requirements)>();
                foreach (var composition in compositions)
                {
                    foreach (var aggregateDto in composition.Aggregates)
                    {
                        if (aggregateMatch is null
                            || !aggregateMatch.ResolvedByObject.TryGetValue(aggregateDto.AggregateId, out var aggregateHk))
                            continue;
                        nodeParentRequirements.Add((aggregateHk, NodesOf(aggregateDto)));
                    }
                }

                if (nodeParentRequirements.Count > 0)
                {
                    var nodeMatch = await MatchChildLevelAsync(
                        IndividualCardObjectLevel.Node, nodeParentRequirements,
                        branchId, hkSources, gaps, gapOrder, ct);
                    gapOrder += nodeMatch.Gaps.Count;
                }

                break;
            }
        }
    }

    private static IReadOnlyList<ChildRequirement> AggregatesOf(IndividualCardPreflightCompositionDto composition) =>
        composition.Aggregates
            .OrderBy(a => a.SortOrder)
            .Select(a => new ChildRequirement(a.AggregateId, a.Code, a.Name))
            .ToList();

    private static IReadOnlyList<ChildRequirement> NodesOf(IndividualCardPreflightAggregateDto aggregate) =>
        aggregate.Nodes
            .OrderBy(n => n.SortOrder)
            .Select(n => new ChildRequirement(n.NodeId, n.Code, n.Name))
            .ToList();

    private async Task<LevelMatch> MatchChildLevelAsync(
        IndividualCardObjectLevel expectedChildLevel,
        IReadOnlyList<(HKCard Parent, IReadOnlyList<ChildRequirement> Requirements)> parentRequirements,
        Guid branchId,
        List<IndividualCardPreflightHKSourceDto> hkSources,
        List<IndividualCardNormativeGapDto> gaps,
        int gapOrderStart, CancellationToken ct)
    {
        var match = new LevelMatch();
        var gapOrder = gapOrderStart;
        var expectedHKLevel = MapToHKLevel(expectedChildLevel);

        var parentIds = parentRequirements.Select(p => p.Parent.Id).Distinct().ToList();
        var edges = await LoadComponentEdgesAsync(parentIds, ct);

        foreach (var (parent, requirements) in parentRequirements)
        {
            var parentEdges = edges
                .Where(e => e.Component.ParentHKCardId == parent.Id)
                .ToList();

            foreach (var requirement in requirements)
            {
                var candidates = parentEdges
                    .Where(e => e.Child.ObjectLevel == expectedHKLevel
                        && GetHKObjectId(e.Child, expectedChildLevel) == requirement.ObjectId)
                    .ToList();

                if (candidates.Count == 0)
                {
                    match.Gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.MissingLinkedHKCard,
                        expectedChildLevel, requirement.ObjectId,
                        IndividualCardDisplay.ObjectLevel(expectedChildLevel),
                        requirement.Code, requirement.Name, parent.Id,
                        $"В ХК «{parent.Code}», {parent.Version} отсутствует связанная ХК {IndividualCardDisplay.ObjectLevel(expectedChildLevel).ToLowerInvariant()} «{requirement.Name}».",
                        gapOrder++));
                    continue;
                }

                var approved = candidates.FirstOrDefault(e => e.Child.Status == HKCardStatus.Approved);
                if (approved is null)
                {
                    var invalid = candidates[0].Child;
                    match.Gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.LinkedHKCardNotApproved,
                        expectedChildLevel, requirement.ObjectId,
                        IndividualCardDisplay.ObjectLevel(expectedChildLevel),
                        requirement.Code, requirement.Name, invalid.Id,
                        $"Связанная ХК «{invalid.Code}», {invalid.Version} не имеет статуса «Approved».",
                        gapOrder++));
                    continue;
                }

                if (approved.Child.BranchId != branchId)
                {
                    match.Gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.LinkedHKCardWrongBranch,
                        expectedChildLevel, requirement.ObjectId,
                        IndividualCardDisplay.ObjectLevel(expectedChildLevel),
                        requirement.Code, requirement.Name, approved.Child.Id,
                        "Связанная ХК относится к другому филиалу.",
                        gapOrder++));
                    continue;
                }

                if (match.ResolvedByObject.TryAdd(requirement.ObjectId, approved.Child))
                {
                    var (objCode, objName) = GetHKObjectDisplay(approved.Child, expectedChildLevel);
                    match.Sources.Add(ToSourceDto(
                        approved.Child, parent.Id, expectedChildLevel,
                        requirement.ObjectId, objCode, objName,
                        approved.Component.SortOrder, isComplete: true));
                }
            }

            var requirementIds = requirements.Select(r => r.ObjectId).ToHashSet();
            var leftoverEdges = parentEdges
                .Where(e => e.Child.ObjectLevel == expectedHKLevel
                    && !requirementIds.Contains(GetHKObjectId(e.Child, expectedChildLevel) ?? Guid.Empty))
                .ToList();

            foreach (var edge in leftoverEdges)
            {
                var extra = edge.Child;
                var (extraCode, extraName) = GetHKObjectDisplay(extra, expectedChildLevel);
                var allResolved = requirements.All(r => match.ResolvedByObject.ContainsKey(r.ObjectId));
                match.Gaps.Add(new IndividualCardNormativeGapDto(
                    allResolved
                        ? IndividualCardNormativeGapKind.InconsistentNormativeChain
                        : IndividualCardNormativeGapKind.LinkedHKCardWrongObject,
                    expectedChildLevel, GetHKObjectId(extra, expectedChildLevel),
                    IndividualCardDisplay.ObjectLevel(expectedChildLevel),
                    extraCode, extraName, extra.Id,
                    allResolved
                        ? $"Связанная ХК «{extra.Code}», {extra.Version} относится к объекту «{extraName}», отсутствующему в действующем составе."
                        : $"Связанная ХК «{extra.Code}», {extra.Version} относится к объекту «{extraName}», не соответствующему действующему составу.",
                    gapOrder++));
            }

            var wrongLevelEdges = parentEdges
                .Where(e => e.Child.ObjectLevel != expectedHKLevel)
                .ToList();
            foreach (var edge in wrongLevelEdges)
            {
                var childLevel = MapFromHKLevel(edge.Child.ObjectLevel);
                var (objCode, objName) = GetHKObjectDisplay(edge.Child, childLevel);
                match.Gaps.Add(new IndividualCardNormativeGapDto(
                    IndividualCardNormativeGapKind.LinkedHKCardWrongLevel,
                    childLevel, GetHKObjectId(edge.Child, childLevel),
                    IndividualCardDisplay.ObjectLevel(childLevel),
                    objCode, objName, edge.Child.Id,
                    $"Связанная ХК «{edge.Child.Code}», {edge.Child.Version} имеет уровень «{IndividualCardDisplay.ObjectLevel(childLevel)}» вместо ожидаемого «{IndividualCardDisplay.ObjectLevel(expectedChildLevel)}».",
                    gapOrder++));
            }
        }

        hkSources.AddRange(match.Sources);
        gaps.AddRange(match.Gaps);
        return match;
    }

    private async Task<List<ComponentEdge>> LoadComponentEdgesAsync(
        IReadOnlyCollection<Guid> parentIds, CancellationToken ct)
    {
        if (parentIds.Count == 0) return new List<ComponentEdge>();

        var components = await _db.HKCardComponents.AsNoTracking()
            .Where(c => parentIds.Contains(c.ParentHKCardId))
            .Include(c => c.ChildHKCard)
                .ThenInclude(h => h.Complex)
            .Include(c => c.ChildHKCard)
                .ThenInclude(h => h.EquipmentModel)
            .Include(c => c.ChildHKCard)
                .ThenInclude(h => h.Aggregate)
            .Include(c => c.ChildHKCard)
                .ThenInclude(h => h.Node)
            .OrderBy(c => c.ParentHKCardId).ThenBy(c => c.SortOrder)
            .ToListAsync(ct);

        return components
            .Select(c => new ComponentEdge(c, c.ChildHKCard))
            .ToList();
    }
}
