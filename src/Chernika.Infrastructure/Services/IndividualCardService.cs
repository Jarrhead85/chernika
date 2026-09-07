using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

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
        // Per-parent resolution: the same required object may resolve under
        // several parents (repeated aggregate under different Изделие chains).
        public Dictionary<Guid, Dictionary<Guid, HKCard>> ResolvedByParentAndObject { get; } = new();
        // Tree-position occurrence identity: parent card id → object id → the
        // occurrence id of the resolved source at that position. The next level
        // uses it as its parent occurrence, keeping repeated sources distinct.
        public Dictionary<Guid, Dictionary<Guid, Guid>> OccurrenceByParentAndObject { get; } = new();
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

    /// <summary>
    /// Actor scope for IndividualCard operations: cross-branch access is decided
    /// by the actual SystemAdmin role only — an individual SystemConfig permission
    /// override must not unlock foreign branches.
    /// </summary>
    private sealed record ActorScope(ApplicationUser Actor, bool IsSystemAdmin, Guid? BranchId);

    private async Task<ActorScope> ResolveActorScopeAsync(CancellationToken ct)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString())
            ?? throw new UnauthorizedAccessException("Пользователь не найден.");
        var isSystemAdmin = await _userManager.IsInRoleAsync(actor, UserRole.SystemAdmin.ToString());

        if (!isSystemAdmin)
        {
            if (actor.BranchId is null || actor.BranchId == Guid.Empty)
                throw new UnauthorizedAccessException("У пользователя не указан филиал.");
            return new ActorScope(actor, false, actor.BranchId);
        }

        return new ActorScope(actor, true, null);
    }

    /// <param name="demandCreateDraftPermission">
    /// Public entry points demand IndividualCard.CreateDraft; the internal
    /// refresh path does not (a Draft editor may be only an EditDraft holder).
    /// </param>
    public async Task<IndividualCardPreflightResult> BuildPreflightAsync(
        IndividualCardPreflightRequest request,
        bool demandCreateDraftPermission = true,
        CancellationToken ct = default)
    {
        if (demandCreateDraftPermission)
            await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardCreateDraft, ct);

        if (request.ObjectLevel == 0 || !Enum.IsDefined(request.ObjectLevel))
            throw new InvalidOperationException("Укажите корректный уровень цели ИК.");

        var actorId = _currentUser.GetRequiredUserId();
        var actorScope = await ResolveActorScopeAsync(ct);
        var isSystemAdmin = actorScope.IsSystemAdmin;
        var actorBranchId = actorScope.BranchId;

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
                .Include(h => h.Complex)
                .Include(h => h.EquipmentModel)
                .Include(h => h.Aggregate)
                .Include(h => h.Node)
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
            // Occurrence identity: one entry per tree POSITION, so the same
            // source HKCardId may appear in several branches.
            var occurrenceByHKCardId = new Dictionary<Guid, Guid>();
            var rootOccurrenceId = Guid.NewGuid();
            occurrenceByHKCardId[selectedRoot.Id] = rootOccurrenceId;

            hkSources.Add(ToSourceDto(rootOccurrenceId, null, selectedRoot, null, target.RootLevel,
                target.RootObjectId, target.RootObjectCode, target.RootObjectName, 0, isComplete: true));

            await ResolveChainAsync(
                selectedRoot, request.ObjectLevel, selectedRoot.BranchId,
                compositions, compositionData, hkSources, gaps, gapOrder,
                rootOccurrenceId, ct);

            MarkSourceCompleteness(hkSources, gaps);
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
        Guid occurrenceId, Guid? parentOccurrenceId,
        HKCard hk, Guid? parentHKCardId, IndividualCardObjectLevel level,
        Guid objectId, string fallbackObjectCode, string fallbackObjectName, int sortOrder, bool isComplete)
    {
        var (loadedCode, loadedName) = GetHKObjectDisplay(hk, level);

        var objectCode = string.IsNullOrWhiteSpace(loadedCode) ? fallbackObjectCode : loadedCode;
        var objectName = string.IsNullOrWhiteSpace(loadedName) ? fallbackObjectName : loadedName;

        return new IndividualCardPreflightHKSourceDto(
            occurrenceId, parentOccurrenceId,
            hk.Id, parentHKCardId, level, objectId, objectCode, objectName,
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

    /// <summary>
    /// Bottom-up completeness propagation over resolved positions: a source is
    /// complete when its own required children all resolved (no gaps attached
    /// to its HK card) and every child position is complete. The root position
    /// additionally requires the preflight to be gap-free overall, because
    /// composition gaps attach to no HK card. Gap→HK mapping is by HKCardId,
    /// so repeated occurrences of the same broken HK are conservatively
    /// incomplete together.
    /// </summary>
    private static void MarkSourceCompleteness(
        List<IndividualCardPreflightHKSourceDto> hkSources,
        List<IndividualCardNormativeGapDto> gaps)
    {
        if (hkSources.Count == 0)
            return;

        var byOccurrence = hkSources.ToDictionary(s => s.PreflightOccurrenceId);
        var childrenByParent = hkSources
            .Where(s => s.ParentPreflightOccurrenceId.HasValue)
            .GroupBy(s => s.ParentPreflightOccurrenceId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var brokenHkIds = gaps
            .Where(g => g.RelatedHKCardId.HasValue)
            .Select(g => g.RelatedHKCardId!.Value)
            .ToHashSet();

        var completeByOccurrence = new Dictionary<Guid, bool>();
        bool IsComplete(Guid occurrenceId)
        {
            if (completeByOccurrence.TryGetValue(occurrenceId, out var complete))
                return complete;
            var source = byOccurrence[occurrenceId];
            var result = !brokenHkIds.Contains(source.HKCardId)
                && (!childrenByParent.TryGetValue(occurrenceId, out var children)
                    || children.All(child => IsComplete(child.PreflightOccurrenceId)));
            completeByOccurrence[occurrenceId] = result;
            return result;
        }

        for (var i = 0; i < hkSources.Count; i++)
        {
            var source = hkSources[i];
            var isComplete = IsComplete(source.PreflightOccurrenceId);
            if (source.ParentPreflightOccurrenceId is null)
                isComplete &= gaps.Count == 0;
            hkSources[i] = source with { IsComplete = isComplete };
        }
    }

    private async Task ResolveChainAsync(
        HKCard root, IndividualCardObjectLevel level, Guid branchId,
        IReadOnlyList<IndividualCardPreflightCompositionDto> compositions,
        List<CompositionData> compositionData,
        List<IndividualCardPreflightHKSourceDto> hkSources,
        List<IndividualCardNormativeGapDto> gaps,
        int gapOrderStart,
        Guid rootOccurrenceId, CancellationToken ct)
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
                    new[] { (Parent: root, ParentOccurrenceId: rootOccurrenceId, Requirements: data.Requirements) }.ToList(),
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
                    new[] { (Parent: root, ParentOccurrenceId: rootOccurrenceId, Requirements: data.Requirements) }.ToList(),
                    branchId, hkSources, gaps, gapOrder, ct);
                gapOrder += aggregateMatch.Gaps.Count;

                // Node level under each resolved aggregate occurrence.
                var nodeParentRequirements = new List<(HKCard Parent, Guid ParentOccurrenceId, IReadOnlyList<ChildRequirement> Requirements)>();
                foreach (var aggregateDto in data.Dto.Aggregates)
                {
                    if (!aggregateMatch.OccurrenceByParentAndObject.TryGetValue(root.Id, out var underRoot)
                        || !underRoot.TryGetValue(aggregateDto.AggregateId, out var aggregateOccurrence))
                        continue;
                    nodeParentRequirements.Add(
                        (aggregateMatch.ResolvedByParentAndObject[root.Id][aggregateDto.AggregateId],
                         aggregateOccurrence, NodesOf(aggregateDto)));
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
                    new[] { (Parent: root, ParentOccurrenceId: rootOccurrenceId, Requirements: (IReadOnlyList<ChildRequirement>)modelRequirements) }.ToList(),
                    branchId, hkSources, gaps, gapOrder, ct);
                gapOrder += modelMatch.Gaps.Count;

                // Aggregate level under each resolved изделие occurrence.
                var aggregateParentRequirements = new List<(HKCard Parent, Guid ParentOccurrenceId, IReadOnlyList<ChildRequirement> Requirements)>();
                foreach (var composition in compositions)
                {
                    if (!modelMatch.OccurrenceByParentAndObject.TryGetValue(root.Id, out var underRoot)
                        || !underRoot.TryGetValue(composition.TargetObjectId, out var modelOccurrence))
                        continue;
                    aggregateParentRequirements.Add(
                        (modelMatch.ResolvedByParentAndObject[root.Id][composition.TargetObjectId],
                         modelOccurrence, AggregatesOf(composition)));
                }

                LevelMatch? aggregateMatch = aggregateParentRequirements.Count > 0
                    ? await MatchChildLevelAsync(
                        IndividualCardObjectLevel.Aggregate, aggregateParentRequirements,
                        branchId, hkSources, gaps, gapOrder, ct)
                    : null;
                if (aggregateMatch is not null)
                    gapOrder += aggregateMatch.Gaps.Count;

                // Node level under each resolved aggregate occurrence. The same
                // aggregate HK may be resolved under several Изделие chains —
                // each occurrence gets its own node-level parent requirements.
                var nodeParentRequirements = new List<(HKCard Parent, Guid ParentOccurrenceId, IReadOnlyList<ChildRequirement> Requirements)>();
                if (aggregateMatch is not null)
                {
                    foreach (var composition in compositions)
                    {
                        foreach (var aggregateDto in composition.Aggregates)
                        {
                            if (!modelMatch.OccurrenceByParentAndObject.TryGetValue(root.Id, out var modelsUnderRoot)
                                || !modelsUnderRoot.TryGetValue(composition.TargetObjectId, out var modelOccurrence))
                                continue;
                            var modelHk = modelMatch.ResolvedByParentAndObject[root.Id][composition.TargetObjectId];
                            if (!aggregateMatch.OccurrenceByParentAndObject.TryGetValue(modelHk.Id, out var aggregatesUnderModel)
                                || !aggregatesUnderModel.TryGetValue(aggregateDto.AggregateId, out var aggregateOccurrence))
                                continue;
                            nodeParentRequirements.Add(
                                (aggregateMatch.ResolvedByParentAndObject[modelHk.Id][aggregateDto.AggregateId],
                                 aggregateOccurrence, NodesOf(aggregateDto)));
                        }
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
        IReadOnlyList<(HKCard Parent, Guid ParentOccurrenceId, IReadOnlyList<ChildRequirement> Requirements)> parentRequirements,
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

        foreach (var (parent, parentOccurrenceId, requirements) in parentRequirements)
        {
            var parentEdges = edges
                .Where(e => e.Component.ParentHKCardId == parent.Id)
                .ToList();
            var resolvedForParent = match.ResolvedByParentAndObject[parent.Id] = new Dictionary<Guid, HKCard>();

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

                var approvedCandidates = candidates
                    .Where(e => e.Child.Status == HKCardStatus.Approved)
                    .ToList();

                if (approvedCandidates.Count == 0)
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

                // Automatic selection is only allowed when exactly one valid variant
                // exists; several Approved linked children of the same object are a
                // normative inconsistency and must never be silently resolved.
                if (approvedCandidates.Count > 1)
                {
                    match.Gaps.Add(new IndividualCardNormativeGapDto(
                        IndividualCardNormativeGapKind.InconsistentNormativeChain,
                        expectedChildLevel, requirement.ObjectId,
                        IndividualCardDisplay.ObjectLevel(expectedChildLevel),
                        requirement.Code, requirement.Name, approvedCandidates[0].Child.Id,
                        $"В ХК «{parent.Code}», {parent.Version} найдено несколько связанных утверждённых ХК {IndividualCardDisplay.ObjectLevel(expectedChildLevel).ToLowerInvariant()} «{requirement.Name}». Устраните противоречие в нормативной цепочке.",
                        gapOrder++));
                    continue;
                }

                var approved = approvedCandidates[0];

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

                if (resolvedForParent.TryAdd(requirement.ObjectId, approved.Child))
                {
                    // A tree position: every resolved source gets a fresh
                    // occurrence id, so the same source HKCardId under another
                    // parent — or even under the same parent in another
                    // parentRequirements entry — stays a distinct tree position.
                    var childOccurrenceId = Guid.NewGuid();
                    if (!match.OccurrenceByParentAndObject.TryGetValue(parent.Id, out var underParent))
                        underParent = match.OccurrenceByParentAndObject[parent.Id] = new Dictionary<Guid, Guid>();
                    underParent[requirement.ObjectId] = childOccurrenceId;

                    match.Sources.Add(ToSourceDto(
                        childOccurrenceId, parentOccurrenceId,
                        approved.Child, parent.Id, expectedChildLevel,
                        requirement.ObjectId, requirement.Code, requirement.Name,
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
                var allResolved = resolvedForParent.Count == requirements.Count;
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

    // ── D3: Draft workflow ─────────────────────────────────────────────────

    private static Guid? GetTargetObjectId(IndividualCard card) => card.ObjectLevel switch
    {
        IndividualCardObjectLevel.Complex => card.ComplexId,
        IndividualCardObjectLevel.EquipmentModel => card.EquipmentModelId,
        IndividualCardObjectLevel.Aggregate => card.AggregateId,
        IndividualCardObjectLevel.Node => card.NodeId,
        IndividualCardObjectLevel.EquipmentInstance => card.EquipmentInstanceId,
        _ => null,
    };

    private static string BuildCardCode(IndividualCardObjectLevel level, string objectCode, int year) => level switch
    {
        IndividualCardObjectLevel.Complex => $"ИК-КОМП-{objectCode}-{year}",
        IndividualCardObjectLevel.EquipmentModel => $"ИК-ИЗД-{objectCode}-{year}",
        IndividualCardObjectLevel.Aggregate => $"ИК-АГР-{objectCode}-{year}",
        IndividualCardObjectLevel.Node => $"ИК-УЗЛ-{objectCode}-{year}",
        IndividualCardObjectLevel.EquipmentInstance => $"ИК-ЭКЗ-{objectCode}-{year}",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private void ApplyTargetFk(IndividualCard draft, IndividualCardObjectLevel level, Guid objectId)
    {
        switch (level)
        {
            case IndividualCardObjectLevel.Complex: draft.ComplexId = objectId; break;
            case IndividualCardObjectLevel.EquipmentModel: draft.EquipmentModelId = objectId; break;
            case IndividualCardObjectLevel.Aggregate: draft.AggregateId = objectId; break;
            case IndividualCardObjectLevel.Node: draft.NodeId = objectId; break;
            case IndividualCardObjectLevel.EquipmentInstance: draft.EquipmentInstanceId = objectId; break;
            default: throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }

    private async Task EnsureDraftAccessibleAsync(IndividualCard draft, ActorScope scope, string notFoundMessage, CancellationToken ct)
    {
        if (!scope.IsSystemAdmin && draft.BranchId != scope.BranchId)
            throw new UnauthorizedAccessException(notFoundMessage);
    }

    private async Task EnsureDraftEditorAsync(
        IndividualCard draft, ActorScope scope, string deniedMessage, CancellationToken ct)
    {
        var actorId = _currentUser.GetRequiredUserId().ToString();
        var isAuthor = draft.CreatedByUserId == actorId;
        if (!isAuthor)
        {
            var canEdit = await _permissions.HasPermissionAsync(
                actorId, PermissionCodes.IndividualCardEditDraft, ct);
            if (!canEdit)
                throw new UnauthorizedAccessException(deniedMessage);
        }

        if (!scope.IsSystemAdmin && draft.BranchId != scope.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к черновику ИК другого филиала.");
    }

    private void CopyDraftSnapshots(
        IndividualCard draft, IndividualCardPreflightResult preflight, DateTime now)
    {
        // New snapshot entities are attached explicitly: a dependent discovered
        // by DetectChanges through a tracked (Unchanged) principal with a client
        // key set would be tracked as Modified instead of Added.
        var newSnapshots = new List<object>();

        // Compositions with aggregates and nodes.
        foreach (var composition in preflight.Compositions)
        {
            var compositionSnapshot = new IndividualCardCompositionSnapshot
            {
                Id = Guid.NewGuid(),
                IndividualCardId = draft.Id,
                SourceLevel = composition.SourceLevel,
                SourceCompositionId = composition.CompositionId,
                SourceCompositionVersion = composition.CompositionVersion,
                SourceApprovedAt = composition.ApprovedAt,
                TargetObjectId = composition.TargetObjectId,
                TargetObjectCode = composition.TargetObjectCode,
                TargetObjectName = composition.TargetObjectName,
                Quantity = composition.Quantity,
                CapturedAt = now,
            };
            newSnapshots.Add(compositionSnapshot);

            foreach (var aggregate in composition.Aggregates)
            {
                var aggregateSnapshot = new IndividualCardAggregateSnapshot
                {
                    Id = Guid.NewGuid(),
                    IndividualCardCompositionSnapshotId = compositionSnapshot.Id,
                    AggregateId = aggregate.AggregateId,
                    AggregateCode = aggregate.Code,
                    AggregateName = aggregate.Name,
                    Quantity = aggregate.Quantity,
                    SortOrder = aggregate.SortOrder,
                };
                newSnapshots.Add(aggregateSnapshot);

                foreach (var node in aggregate.Nodes)
                {
                    newSnapshots.Add(new IndividualCardNodeSnapshot
                    {
                        Id = Guid.NewGuid(),
                        IndividualCardAggregateSnapshotId = aggregateSnapshot.Id,
                        NodeId = node.NodeId,
                        NodeCode = node.Code,
                        NodeName = node.Name,
                        Quantity = node.Quantity,
                        SortOrder = node.SortOrder,
                    });
                }

                compositionSnapshot.Aggregates.Add(aggregateSnapshot);
            }

            draft.CompositionSnapshots.Add(compositionSnapshot);
        }

        // HK source chain: the same source HKCardId may appear in several
        // branches, so parent mapping uses preflight occurrence identity —
        // never the source HKCardId.
        var snapshotByOccurrenceId = new Dictionary<Guid, IndividualCardHKSourceSnapshot>();
        foreach (var source in preflight.HKSources)
        {
            var snapshot = new IndividualCardHKSourceSnapshot
            {
                Id = Guid.NewGuid(),
                IndividualCardId = draft.Id,
                PreflightOccurrenceId = source.PreflightOccurrenceId,
                SourceHKCardId = source.HKCardId,
                ObjectLevel = source.ObjectLevel,
                SourceObjectId = source.ObjectId,
                SourceObjectCode = source.ObjectCode,
                SourceObjectName = source.ObjectName,
                HKCardCode = source.HKCardCode,
                HKCardVersion = source.HKCardVersion,
                BranchId = source.BranchId,
                HKCardApprovedAt = source.ApprovedAt,
                HKCardEffectiveDate = source.EffectiveDate,
                HKCardExpirationDate = source.ExpirationDate,
                SortOrder = source.SortOrder,
                CapturedAt = now,
                IsComplete = source.IsComplete,
            };
            snapshotByOccurrenceId[source.PreflightOccurrenceId] = snapshot;
            newSnapshots.Add(snapshot);
        }

        foreach (var source in preflight.HKSources)
        {
            if (source.ParentPreflightOccurrenceId.HasValue
                && snapshotByOccurrenceId.TryGetValue(source.ParentPreflightOccurrenceId.Value, out var parentSnapshot))
            {
                snapshotByOccurrenceId[source.PreflightOccurrenceId].ParentHKSourceSnapshotId = parentSnapshot.Id;
            }
        }

        foreach (var snapshot in snapshotByOccurrenceId.Values)
            draft.HKSourceSnapshots.Add(snapshot);

        // Normative gaps: historical explanation of a partial Draft.
        foreach (var gap in preflight.NormativeGaps)
        {
            var snapshot = new IndividualCardNormativeGapSnapshot
            {
                Id = Guid.NewGuid(),
                IndividualCardId = draft.Id,
                Kind = gap.Kind,
                RelatedLevel = gap.RelatedLevel,
                RelatedObjectId = gap.RelatedObjectId,
                RelatedObjectType = gap.RelatedObjectType,
                RelatedObjectCode = gap.RelatedObjectCode,
                RelatedObjectName = gap.RelatedObjectName,
                RelatedHKCardId = gap.RelatedHKCardId,
                Message = gap.Message,
                SortOrder = gap.SortOrder,
                CapturedAt = now,
            };
            newSnapshots.Add(snapshot);
            draft.NormativeGapSnapshots.Add(snapshot);
        }

        if (newSnapshots.Count > 0)
            _db.AddRange(newSnapshots);
    }

    private static IndividualCardDraftDto ToDraftDto(IndividualCard draft)
    {
        var objectId = GetTargetObjectId(draft) ?? Guid.Empty;
        var objectCode = string.Empty;
        var objectName = string.Empty;

        switch (draft.ObjectLevel)
        {
            case IndividualCardObjectLevel.Complex:
                objectCode = draft.Complex?.Code ?? string.Empty;
                objectName = draft.Complex?.Name ?? string.Empty;
                break;
            case IndividualCardObjectLevel.EquipmentModel:
                objectCode = draft.EquipmentModel?.Index ?? string.Empty;
                objectName = draft.EquipmentModel?.Name ?? string.Empty;
                break;
            case IndividualCardObjectLevel.Aggregate:
                objectCode = draft.Aggregate?.Code ?? string.Empty;
                objectName = draft.Aggregate?.Name ?? string.Empty;
                break;
            case IndividualCardObjectLevel.Node:
                objectCode = draft.Node?.Code ?? string.Empty;
                objectName = draft.Node?.Name ?? string.Empty;
                break;
            case IndividualCardObjectLevel.EquipmentInstance:
                objectCode = draft.EquipmentInstance?.SerialNumber ?? string.Empty;
                objectName = draft.EquipmentInstance?.Name ?? string.Empty;
                break;
        }

        return new IndividualCardDraftDto
        {
            Id = draft.Id,
            Code = draft.Code,
            Version = draft.Version,
            RevisionNumber = draft.RevisionNumber,
            ObjectLevel = draft.ObjectLevel,
            ObjectLevelDisplay = IndividualCardDisplay.ObjectLevel(draft.ObjectLevel),
            ObjectId = objectId,
            ObjectCode = objectCode,
            ObjectName = objectName,
            BranchId = draft.BranchId,
            Status = draft.Status,
            Notes = draft.Notes,
            CreatedByUserId = draft.CreatedByUserId,
            CreatedAt = draft.CreatedAt,
            Compositions = draft.CompositionSnapshots
                .OrderBy(cs => cs.CapturedAt).ThenBy(cs => cs.TargetObjectCode)
                .Select(cs => new IndividualCardCompositionSnapshotDto
                {
                    Id = cs.Id,
                    SourceLevel = cs.SourceLevel,
                    SourceCompositionId = cs.SourceCompositionId,
                    SourceCompositionVersion = cs.SourceCompositionVersion,
                    SourceApprovedAt = cs.SourceApprovedAt,
                    TargetObjectId = cs.TargetObjectId,
                    TargetObjectCode = cs.TargetObjectCode,
                    TargetObjectName = cs.TargetObjectName,
                    Quantity = cs.Quantity,
                    CapturedAt = cs.CapturedAt,
                    Aggregates = cs.Aggregates
                        .OrderBy(a => a.SortOrder)
                        .Select(a => new IndividualCardAggregateSnapshotDto
                        {
                            Id = a.Id,
                            AggregateId = a.AggregateId,
                            AggregateCode = a.AggregateCode,
                            AggregateName = a.AggregateName,
                            Quantity = a.Quantity,
                            SortOrder = a.SortOrder,
                            Nodes = a.Nodes
                                .OrderBy(n => n.SortOrder)
                                .Select(n => new IndividualCardNodeSnapshotDto
                                {
                                    Id = n.Id,
                                    NodeId = n.NodeId,
                                    NodeCode = n.NodeCode,
                                    NodeName = n.NodeName,
                                    Quantity = n.Quantity,
                                    SortOrder = n.SortOrder,
                                }).ToList(),
                        }).ToList(),
                }).ToList(),
            HKSources = draft.HKSourceSnapshots
                .OrderBy(s => s.SortOrder)
                .Select(s => new IndividualCardHKSourceSnapshotDto
                {
                    Id = s.Id,
                    ParentHKSourceSnapshotId = s.ParentHKSourceSnapshotId,
                    SourceHKCardId = s.SourceHKCardId,
                    ObjectLevel = s.ObjectLevel,
                    SourceObjectId = s.SourceObjectId,
                    SourceObjectCode = s.SourceObjectCode,
                    SourceObjectName = s.SourceObjectName,
                    HKCardCode = s.HKCardCode,
                    HKCardVersion = s.HKCardVersion,
                    BranchId = s.BranchId,
                    HKCardApprovedAt = s.HKCardApprovedAt,
                    HKCardEffectiveDate = s.HKCardEffectiveDate,
                    HKCardExpirationDate = s.HKCardExpirationDate,
                    SortOrder = s.SortOrder,
                    CapturedAt = s.CapturedAt,
                    IsComplete = s.IsComplete,
                }).ToList(),
            NormativeGaps = draft.NormativeGapSnapshots
                .OrderBy(g => g.SortOrder)
                .Select(g => new IndividualCardNormativeGapSnapshotDto
                {
                    Id = g.Id,
                    Kind = g.Kind,
                    RelatedLevel = g.RelatedLevel,
                    RelatedObjectId = g.RelatedObjectId,
                    RelatedObjectType = g.RelatedObjectType,
                    RelatedObjectCode = g.RelatedObjectCode,
                    RelatedObjectName = g.RelatedObjectName,
                    RelatedHKCardId = g.RelatedHKCardId,
                    Message = g.Message,
                    SortOrder = g.SortOrder,
                    CapturedAt = g.CapturedAt,
                }).ToList(),
        };
    }

    public async Task<IndividualCardDraftDto> CreateDraftAsync(
        CreateIndividualCardDraftRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardCreateDraft, ct);

        if (request.ObjectLevel == 0 || !Enum.IsDefined(request.ObjectLevel))
            throw new InvalidOperationException("Укажите корректный уровень цели ИК.");

        // Preflight is the only source of the allowed normative chain.
        var preflight = await BuildPreflightAsync(
            new IndividualCardPreflightRequest(request.ObjectLevel, request.ObjectId, request.RootHKCardId),
            demandCreateDraftPermission: true, ct);

        if (preflight.SelectedRoot is null)
        {
            throw new InvalidOperationException(preflight.RootState == IndividualCardPreflightRootState.SelectionRequired
                ? "Для создания черновика ИК выберите утверждённую ХК вручную."
                : "Невозможно создать черновик ИК: не найдена утверждённая ХК верхнего уровня.");
        }

        var target = await ResolveTargetAsync(request.ObjectLevel, request.ObjectId, ct)
            ?? throw new InvalidOperationException(
                $"Объект цели ИК не найден или архивирован: {IndividualCardDisplay.ObjectLevel(request.ObjectLevel)}.");

        var actorId = _currentUser.GetRequiredUserId();
        var now = _time.GetUtcNow().UtcDateTime;
        var year = now.Year;
        var code = BuildCardCode(request.ObjectLevel, target.Code, year);
        var version = $"v{now:MMyy}.1";

        var duplicate = await _db.IndividualCards.AsNoTracking()
            .AnyAsync(c => c.Code == code, ct);
        if (duplicate)
        {
            throw new InvalidOperationException(
                $"Для объекта «{target.Name}» уже существует ИК «{code}». " +
                "Для создания следующей версии используйте действие «Создать новую версию».");
        }

        var draft = new IndividualCard
        {
            Id = Guid.NewGuid(),
            Code = code,
            Version = version,
            RevisionNumber = 1,
            ObjectLevel = request.ObjectLevel,
            Status = IndividualCardStatus.Draft,
            BranchId = preflight.BranchId!.Value,
            CreatedByUserId = actorId.ToString(),
            CreatedAt = now,
            Notes = request.Notes,
        };
        ApplyTargetFk(draft, request.ObjectLevel, request.ObjectId);

        CopyDraftSnapshots(draft, preflight, now);

        _db.IndividualCards.Add(draft);
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "IndividualCard", draft.Id.ToString(), "IndividualCard.DraftCreated",
            actorId, EntityDisplayName: $"{code} {version}",
            Details: $"ObjectLevel={request.ObjectLevel}; ObjectId={request.ObjectId}; BranchId={draft.BranchId}; SelectedRootHKCardId={preflight.SelectedRoot.HKCardId}; CompositionCount={preflight.Compositions.Count}; HKSourceCount={preflight.HKSources.Count}; NormativeGapCount={preflight.NormativeGaps.Count}"), ct);

        // The unique (Code, Version) index guards concurrent duplicate creation.
        await _db.SaveChangesAsync(ct);

        return (await LoadDraftDetailedAsync(draft.Id, ct)) is { } reloaded ? ToDraftDto(reloaded) : ToDraftDto(draft);
    }

    public async Task<IndividualCardDraftDto?> GetDraftByIdAsync(Guid individualCardId, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardView, ct);
        var scope = await ResolveActorScopeAsync(ct);

        var draft = await LoadDraftDetailedAsync(individualCardId, ct);

        if (draft is null)
            return null;

        if (!scope.IsSystemAdmin && draft.BranchId != scope.BranchId)
            return null;

        // Reading never rebuilds or refreshes snapshots: a Draft is historical
        // relative to its creation/last explicit refresh.
        return ToDraftDto(draft);
    }

    private async Task<IndividualCard?> LoadDraftDetailedAsync(Guid individualCardId, CancellationToken ct) =>
        await _db.IndividualCards.AsNoTracking()
            .Include(d => d.Complex)
            .Include(d => d.EquipmentModel)
            .Include(d => d.Aggregate)
            .Include(d => d.Node)
            .Include(d => d.EquipmentInstance)
            .Include(d => d.CompositionSnapshots)
                .ThenInclude(cs => cs.Aggregates)
                    .ThenInclude(a => a.Nodes)
            .Include(d => d.HKSourceSnapshots)
            .Include(d => d.NormativeGapSnapshots)
            .FirstOrDefaultAsync(d => d.Id == individualCardId && d.Status == IndividualCardStatus.Draft, ct);

    public async Task<IndividualCardDraftDto> RefreshDraftSourcesAsync(
        RefreshIndividualCardDraftSourcesRequest request, CancellationToken ct = default)
    {
        // Refresh is allowed to the Draft author or any IndividualCard.EditDraft
        // holder (with branch scope); IndividualCard.CreateDraft is NOT required.
        var scope = await ResolveActorScopeAsync(ct);

        var draft = await _db.IndividualCards
            .Include(d => d.CompositionSnapshots)
                .ThenInclude(cs => cs.Aggregates)
                    .ThenInclude(a => a.Nodes)
            .Include(d => d.HKSourceSnapshots)
            .Include(d => d.NormativeGapSnapshots)
            .FirstOrDefaultAsync(d => d.Id == request.IndividualCardId, ct)
            ?? throw new InvalidOperationException("Черновик ИК не найден.");

        if (draft.Status != IndividualCardStatus.Draft)
            throw new InvalidOperationException("Обновить нормативные источники можно только у черновика ИК.");

        await EnsureDraftEditorAsync(draft, scope, "Недостаточно прав для изменения черновика ИК.", ct);

        var objectId = GetTargetObjectId(draft)
            ?? throw new InvalidOperationException("Черновик ИК повреждён: не указан объект цели.");

        // Explicit command: a new preflight against current sources.
        var preflight = await BuildPreflightAsync(
            new IndividualCardPreflightRequest(draft.ObjectLevel, objectId, request.RootHKCardId),
            demandCreateDraftPermission: false, ct);

        // Rejects retain all previous snapshots unchanged.
        if (preflight.SelectedRoot is null)
        {
            throw new InvalidOperationException(preflight.RootState == IndividualCardPreflightRootState.SelectionRequired
                ? "Для обновления источников выберите утверждённую ХК вручную."
                : "Невозможно обновить источники черновика ИК: не найдена утверждённая ХК верхнего уровня.");
        }

        // The immutable branch identity of a Draft never changes on refresh.
        if (preflight.SelectedRoot.BranchId != draft.BranchId)
            throw new InvalidOperationException("Нельзя заменить нормативные источники черновика ИК на другой филиал.");

        var oldCompositionCount = draft.CompositionSnapshots.Count;
        var oldHKSourceCount = draft.HKSourceSnapshots.Count;
        var oldGapCount = draft.NormativeGapSnapshots.Count;
        var oldRootHKCardId = draft.HKSourceSnapshots
            .Where(s => s.ParentHKSourceSnapshotId == null)
            .OrderBy(s => s.SortOrder)
            .Select(s => s.SourceHKCardId)
            .FirstOrDefault();

        // The whole replace workflow is atomic: validation happened above,
        // so a failure after the first snapshot delete rolls everything back
        // and the previous snapshot set is fully restored.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Replace, never merge: delete the whole previous snapshot set with
            // deterministic bulk deletes (children first), then detach the stale
            // tracked graph so relationship fixup cannot interfere with the new set.
            var oldCompositionIds = draft.CompositionSnapshots.Select(c => c.Id).ToList();
            var oldAggregateIds = draft.CompositionSnapshots
                .SelectMany(c => c.Aggregates)
                .Select(a => a.Id)
                .ToList();

            await _db.IndividualCardNodeSnapshots
                .Where(n => oldAggregateIds.Contains(n.IndividualCardAggregateSnapshotId))
                .ExecuteDeleteAsync(ct);
            await _db.IndividualCardAggregateSnapshots
                .Where(a => oldCompositionIds.Contains(a.IndividualCardCompositionSnapshotId))
                .ExecuteDeleteAsync(ct);
            await _db.IndividualCardCompositionSnapshots
                .Where(c => c.IndividualCardId == draft.Id)
                .ExecuteDeleteAsync(ct);
            await _db.IndividualCardHKSourceSnapshots
                .Where(s => s.IndividualCardId == draft.Id)
                .ExecuteDeleteAsync(ct);
            await _db.IndividualCardNormativeGapSnapshots
                .Where(s => s.IndividualCardId == draft.Id)
                .ExecuteDeleteAsync(ct);

            var staleSnapshots = new List<object>();
            staleSnapshots.AddRange(draft.CompositionSnapshots.SelectMany(c => c.Aggregates).SelectMany(a => a.Nodes));
            staleSnapshots.AddRange(draft.CompositionSnapshots.SelectMany(c => c.Aggregates));
            staleSnapshots.AddRange(draft.CompositionSnapshots);
            staleSnapshots.AddRange(draft.HKSourceSnapshots);
            staleSnapshots.AddRange(draft.NormativeGapSnapshots);
            foreach (var stale in staleSnapshots)
                _db.Entry(stale).State = EntityState.Detached;
            draft.CompositionSnapshots.Clear();
            draft.HKSourceSnapshots.Clear();
            draft.NormativeGapSnapshots.Clear();

            var now = _time.GetUtcNow().UtcDateTime;
            CopyDraftSnapshots(draft, preflight, now);

            var actorId = _currentUser.GetRequiredUserId();
            await _audit.CreateLogAsync(new AuditWriteRequest(
                "IndividualCard", draft.Id.ToString(), "IndividualCard.SourcesRefreshed",
                actorId, EntityDisplayName: $"{draft.Code} {draft.Version}",
                Details: $"OldRootHKCardId={oldRootHKCardId}; NewRootHKCardId={preflight.SelectedRoot.HKCardId}; OldCompositionCount={oldCompositionCount}; NewCompositionCount={preflight.Compositions.Count}; OldHKSourceCount={oldHKSourceCount}; NewHKSourceCount={preflight.HKSources.Count}; OldNormativeGapCount={oldGapCount}; NewNormativeGapCount={preflight.NormativeGaps.Count}"), ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        return (await LoadDraftDetailedAsync(draft.Id, ct)) is { } reloaded ? ToDraftDto(reloaded) : ToDraftDto(draft);
    }

    public async Task DeleteDraftAsync(Guid individualCardId, CancellationToken ct = default)
    {
        // Deletion is allowed to the Draft author or any IndividualCard.EditDraft
        // holder (with branch scope); IndividualCard.CreateDraft is NOT required.
        var scope = await ResolveActorScopeAsync(ct);

        var draft = await _db.IndividualCards
            .FirstOrDefaultAsync(d => d.Id == individualCardId, ct)
            ?? throw new InvalidOperationException("Черновик ИК не найден.");

        if (draft.Status != IndividualCardStatus.Draft)
            throw new InvalidOperationException("Удалить можно только черновик ИК.");

        await EnsureDraftEditorAsync(draft, scope, "Недостаточно прав для удаления черновика ИК.", ct);

        var actorId = _currentUser.GetRequiredUserId();

        await _audit.CreateLogAsync(new AuditWriteRequest(
            "IndividualCard", draft.Id.ToString(), "IndividualCard.DraftDeleted",
            actorId, EntityDisplayName: $"{draft.Code} {draft.Version}",
            Details: $"Code={draft.Code}; Version={draft.Version}; ObjectLevel={draft.ObjectLevel}; ObjectId={GetTargetObjectId(draft)}; BranchId={draft.BranchId}; CreatedByUserId={draft.CreatedByUserId}; DeletedByUserId={actorId}"), ct);

        // Cascade removes all snapshots; the audit row is independent and survives.
        _db.IndividualCards.Remove(draft);
        await _db.SaveChangesAsync(ct);
    }

    // ── D4: coefficients, calculation, Form ────────────────────────────────

    public async Task<IndividualCardCalculationDto?> GetDraftCalculationAsync(
        Guid individualCardId, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardView, ct);
        var scope = await ResolveActorScopeAsync(ct);

        var draft = await LoadDraftForCalculationAsync(individualCardId, ct: ct);
        if (draft is null)
            return null;
        if (!scope.IsSystemAdmin && draft.BranchId != scope.BranchId)
            return null;

        return BuildCalculationDto(draft);
    }

    public async Task<IReadOnlyList<CoefficientListItemDto>> GetWorkingCoefficientsForDraftSelectAsync(
        Guid individualCardId, string? searchText = null, CancellationToken ct = default)
    {
        var scope = await ResolveActorScopeAsync(ct);

        var draft = await _db.IndividualCards.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == individualCardId && d.Status == IndividualCardStatus.Draft, ct)
            ?? throw new InvalidOperationException("Черновик ИК не найден.");

        // Draft author OR IndividualCard.EditDraft with branch scope; same rule
        // as refresh/delete. IndividualCard.CreateDraft is NOT required.
        await EnsureDraftEditorAsync(draft, scope, "Недостаточно прав для работы с черновиком ИК.", ct);

        var query = _db.Coefficients.AsNoTracking()
            .Where(c => !c.IsDeleted && !c.CoefficientType.IsDeleted);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.CoefficientType.Name, pattern) ||
                EF.Functions.ILike(c.Name, pattern) ||
                (c.ConditionDescription != null && EF.Functions.ILike(c.ConditionDescription, pattern)) ||
                (c.NormativeBasis != null && EF.Functions.ILike(c.NormativeBasis, pattern)));
        }

        return await query
            .OrderBy(c => c.CoefficientType.SortOrder).ThenBy(c => c.CoefficientType.Name).ThenBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CoefficientListItemDto
            {
                Id = c.Id,
                CoefficientTypeId = c.CoefficientTypeId,
                CoefficientTypeName = c.CoefficientType.Name,
                Name = c.Name,
                Value = c.Value,
                ConditionDescription = c.ConditionDescription,
                NormativeBasis = c.NormativeBasis,
                SortOrder = c.SortOrder,
                IsDeleted = c.IsDeleted,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                DeletedAt = c.DeletedAt,
            }).ToListAsync(ct);
    }

    public async Task<IndividualCardCalculationDto> RecalculateDraftAsync(
        RecalculateIndividualCardDraftRequest request, CancellationToken ct = default)
    {
        var scope = await ResolveActorScopeAsync(ct);

        var draft = await _db.IndividualCards
            .Include(d => d.CompositionSnapshots).ThenInclude(cs => cs.Aggregates)
            .Include(d => d.CompositionSnapshots).ThenInclude(cs => cs.Aggregates).ThenInclude(a => a.Nodes)
            .Include(d => d.HKSourceSnapshots)
            .Include(d => d.NormativeGapSnapshots)
            .Include(d => d.Items).ThenInclude(i => i.MaterialSnapshots)
            .Include(d => d.CoefficientSnapshots)
            .Include(d => d.CalculationProblemSnapshots)
            .FirstOrDefaultAsync(d => d.Id == request.IndividualCardId, ct)
            ?? throw new InvalidOperationException("Черновик ИК не найден.");

        if (draft.Status != IndividualCardStatus.Draft)
            throw new InvalidOperationException("Пересчитать можно только черновик ИК.");

        // Author OR IndividualCard.EditDraft with branch scope, plus the
        // dedicated IndividualCard.RecalculateDraft right; hidden UI is not
        // security, so every required right is checked server-side.
        await EnsureDraftEditorAsync(draft, scope, "Недостаточно прав для изменения черновика ИК.", ct);
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardRecalculateDraft, ct);

        // Coefficient selection validation.
        var requestedIds = request.CoefficientIds ?? new List<Guid>();
        if (requestedIds.Distinct().Count() != requestedIds.Count)
            throw new InvalidOperationException("Коэффициенты выбраны с повторами.");

        var coefficients = await _db.Coefficients.AsNoTracking()
            .Include(c => c.CoefficientType)
            .Where(c => requestedIds.Contains(c.Id))
            .ToListAsync(ct);

        if (coefficients.Count != requestedIds.Count)
            throw new InvalidOperationException("Один или несколько выбранных коэффициентов не найдены.");

        var archived = coefficients.FirstOrDefault(c => c.IsDeleted || c.CoefficientType.IsDeleted);
        if (archived is not null)
            throw new InvalidOperationException(
                $"Коэффициент «{archived.Name}» или его тип архивирован и не может применяться.");

        var duplicateType = coefficients.GroupBy(c => c.CoefficientTypeId).FirstOrDefault(g => g.Count() > 1);
        if (duplicateType is not null)
            throw new InvalidOperationException(
                $"Для типа коэффициентов «{duplicateType.First().CoefficientType.Name}» выбрано несколько коэффициентов.");

        decimal totalCoefficient = 1m;
        foreach (var coefficient in coefficients.OrderBy(c => c.CoefficientType.SortOrder).ThenBy(c => c.SortOrder))
            totalCoefficient *= coefficient.Value;

        var (items, problems, totalNorm, primaryMaterialCount, _) =
            await CalculateDraftRowsAsync(draft, totalCoefficient, ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var actorId = _currentUser.GetRequiredUserId();
        var oldItemIds = draft.Items.Select(i => i.Id).ToList();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await _db.IndividualCardItemMaterialSnapshots
                .Where(m => oldItemIds.Contains(m.IndividualCardItemId))
                .ExecuteDeleteAsync(ct);
            await _db.IndividualCardItems
                .Where(i => i.IndividualCardId == draft.Id)
                .ExecuteDeleteAsync(ct);
            await _db.IndividualCardCoefficientSnapshots
                .Where(s => s.IndividualCardId == draft.Id)
                .ExecuteDeleteAsync(ct);
            await _db.IndividualCardCalculationProblemSnapshots
                .Where(p => p.IndividualCardId == draft.Id)
                .ExecuteDeleteAsync(ct);

            // Detach the stale tracked graph so relationship fixup cannot
            // interfere with the replaced calculation set.
            foreach (var stale in draft.Items.SelectMany(i => i.MaterialSnapshots).Cast<object>()
                         .Concat(draft.Items).Concat(draft.CoefficientSnapshots)
                         .Concat(draft.CalculationProblemSnapshots))
                _db.Entry(stale).State = EntityState.Detached;
            draft.Items.Clear();
            draft.CoefficientSnapshots.Clear();
            draft.CalculationProblemSnapshots.Clear();

            var newRows = new List<object>();
            var sortOrder = 0;
            foreach (var coefficient in coefficients.OrderBy(c => c.CoefficientType.SortOrder).ThenBy(c => c.SortOrder))
            {
                var snapshot = new IndividualCardCoefficientSnapshot
                {
                    Id = Guid.NewGuid(),
                    IndividualCardId = draft.Id,
                    SourceCoefficientId = coefficient.Id,
                    SourceCoefficientTypeId = coefficient.CoefficientTypeId,
                    CoefficientTypeName = coefficient.CoefficientType.Name,
                    CoefficientName = coefficient.Name,
                    Value = coefficient.Value,
                    ConditionDescription = coefficient.ConditionDescription,
                    NormativeBasis = coefficient.NormativeBasis,
                    SortOrder = sortOrder++,
                    CapturedAt = now,
                };
                newRows.Add(snapshot);
                draft.CoefficientSnapshots.Add(snapshot);
            }

            foreach (var item in items)
            {
                item.IndividualCardId = draft.Id;
                newRows.Add(item);
                draft.Items.Add(item);
            }

            // ALL validation problems become immutable snapshots — they must
            // survive reloads and block Form until the next recalculation.
            var problemSortOrder = 0;
            foreach (var problem in problems)
            {
                var problemSnapshot = new IndividualCardCalculationProblemSnapshot
                {
                    Id = Guid.NewGuid(),
                    IndividualCardId = draft.Id,
                    Code = problem.Code,
                    Message = problem.Message,
                    HKCardId = problem.HKCardId,
                    HKCardItemId = problem.HKCardItemId,
                    NodeSnapshotId = problem.NodeSnapshotId,
                    SortOrder = problemSortOrder++,
                    CapturedAt = now,
                };
                newRows.Add(problemSnapshot);
                draft.CalculationProblemSnapshots.Add(problemSnapshot);
            }

            if (newRows.Count > 0)
                _db.AddRange(newRows);

            draft.TotalNorm = totalNorm;

            await _audit.CreateLogAsync(new AuditWriteRequest(
                "IndividualCard", draft.Id.ToString(), "IndividualCard.Recalculated",
                actorId, EntityDisplayName: $"{draft.Code} {draft.Version}",
                Details: $"CoefficientCount={coefficients.Count}; TotalCoefficient={totalCoefficient.ToString("F6", CultureInfo.InvariantCulture)}; CalculationItemCount={items.Count}; PrimaryMaterialSnapshotCount={primaryMaterialCount}; CalculationProblemCount={problems.Count}"), ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        var reloaded = await LoadDraftForCalculationAsync(draft.Id, ct: ct);
        return BuildCalculationDto(reloaded!);
    }

    public async Task<IndividualCardCalculationDto> FormDraftAsync(
        FormIndividualCardRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardForm, ct);
        var scope = await ResolveActorScopeAsync(ct);

        var draft = await LoadDraftForCalculationAsync(request.IndividualCardId, tracked: true, ct: ct)
            ?? throw new InvalidOperationException("Черновик ИК не найден.");

        if (draft.Status != IndividualCardStatus.Draft)
            throw new InvalidOperationException("Сформировать можно только черновик ИК.");

        if (!scope.IsSystemAdmin && draft.BranchId != scope.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к черновику ИК другого филиала.");

        // Form is blocked by D3 normative gaps or by ANY persisted calculation
        // problem snapshot; per-item validation happens at recalculation time
        // and its results are stored immutably.
        var blockers = new List<string>();

        if (draft.NormativeGapSnapshots.Count > 0)
            blockers.Add("Черновик ИК содержит нормативные разрывы цепочки.");

        if (draft.Items.Count == 0)
            blockers.Add("Расчёт не выполнялся: строки расчёта отсутствуют.");

        foreach (var problem in draft.CalculationProblemSnapshots.OrderBy(p => p.SortOrder))
            blockers.Add(problem.Message);

        if (blockers.Count > 0)
        {
            throw new InvalidOperationException(
                "Формирование ИК заблокировано: " + string.Join(" ", blockers));
        }

        var actorId = _currentUser.GetRequiredUserId();
        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            draft.Status = IndividualCardStatus.Formed;
            draft.FormedAt = now;
            draft.FormedByUserId = actorId.ToString();

            await _audit.CreateLogAsync(new AuditWriteRequest(
                "IndividualCard", draft.Id.ToString(), "IndividualCard.Formed",
                actorId, EntityDisplayName: $"{draft.Code} {draft.Version}",
                Details: $"ObjectLevel={draft.ObjectLevel}; ObjectId={GetTargetObjectId(draft)}; BranchId={draft.BranchId}; CoefficientCount={draft.CoefficientSnapshots.Count}; TotalCoefficient={BuildTotalCoefficient(draft.CoefficientSnapshots).ToString("F6", CultureInfo.InvariantCulture)}; CalculationItemCount={draft.Items.Count}; PrimaryMaterialSnapshotCount={draft.Items.Sum(i => i.MaterialSnapshots.Count(m => m.Category == GsmCategory.Primary))}"), ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        // The card is Formed now; the in-memory draft already carries the full
        // loaded graph (items, coefficient snapshots, source identities).
        return BuildCalculationDto(draft);
    }

    // ── D5: new version, comparison, archive ───────────────────────────────

    public async Task<IndividualCardActionHeaderDto?> GetIndividualCardActionHeaderAsync(
        Guid individualCardId, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardView, ct);
        var scope = await ResolveActorScopeAsync(ct);

        var card = await _db.IndividualCards.AsNoTracking()
            .Include(d => d.Complex)
            .Include(d => d.EquipmentModel)
            .Include(d => d.Aggregate)
            .Include(d => d.Node)
            .Include(d => d.EquipmentInstance)
            .FirstOrDefaultAsync(d => d.Id == individualCardId, ct);

        if (card is null
            || (card.Status != IndividualCardStatus.Formed && card.Status != IndividualCardStatus.Archived))
            return null;

        if (!scope.IsSystemAdmin && card.BranchId != scope.BranchId)
            return null;

        return new IndividualCardActionHeaderDto(
            card.Id,
            card.Code,
            card.Version,
            card.ObjectLevel,
            IndividualCardDisplay.ObjectLevel(card.ObjectLevel),
            IndividualCardDisplaySourceObjectName(card),
            card.BranchId,
            card.Status);
    }

    private async Task<IndividualCard?> LoadIndividualCardWithSnapshotsAsync(Guid id, CancellationToken ct) =>
        await _db.IndividualCards.AsNoTracking()
            .Include(d => d.Complex)
            .Include(d => d.EquipmentModel)
            .Include(d => d.Aggregate)
            .Include(d => d.Node)
            .Include(d => d.EquipmentInstance)
            .Include(d => d.CompositionSnapshots).ThenInclude(cs => cs.Aggregates)
            .Include(d => d.CompositionSnapshots).ThenInclude(cs => cs.Aggregates).ThenInclude(a => a.Nodes)
            .Include(d => d.HKSourceSnapshots)
            .Include(d => d.NormativeGapSnapshots)
            .Include(d => d.Items).ThenInclude(i => i.MaterialSnapshots)
            .Include(d => d.CoefficientSnapshots)
            .Include(d => d.CalculationProblemSnapshots)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

    private async Task<IndividualCard> LoadFormedSourceAsync(Guid id, ActorScope scope, CancellationToken ct)
    {
        var source = await LoadIndividualCardWithSnapshotsAsync(id, ct)
            ?? throw new InvalidOperationException("ИК не найдена.");

        if (source.Status != IndividualCardStatus.Formed)
            throw new InvalidOperationException("Новая версия создаётся только для сформированной ИК.");

        if (!scope.IsSystemAdmin && source.BranchId != scope.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к ИК другого филиала.");

        return source;
    }

    public async Task<IndividualCardVersionComparisonDto> BuildNewVersionComparisonAsync(
        IndividualCardVersionPreflightRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardCreateVersion, ct);
        var scope = await ResolveActorScopeAsync(ct);

        var source = await LoadFormedSourceAsync(request.SourceIndividualCardId, scope, ct);
        var objectId = GetTargetObjectId(source)
            ?? throw new InvalidOperationException("ИК повреждена: не указан объект цели.");

        // Fresh preflight of CURRENT sources — the source card's own snapshots
        // are the "was" side, live sources are the "now" side.
        var preflight = await BuildPreflightAsync(
            new IndividualCardPreflightRequest(source.ObjectLevel, objectId, request.RootHKCardId),
            demandCreateDraftPermission: false, ct);

        var compositionChanges = BuildCompositionDiff(source, preflight);
        var hkChanges = BuildHKSourceDiff(source, preflight);

        var previousTotals = source.Items
            .SelectMany(i => i.MaterialSnapshots.Where(m => m.Category == GsmCategory.Primary)
                .Select(m => (Item: i, Material: m)))
            .GroupBy(x => (x.Material.MaterialName, x.Material.Gost, x.Material.UnitOfMeasure))
            .OrderBy(g => g.Key.MaterialName)
            .Select(g => new IndividualCardPrimaryTotalComparisonDto(
                g.Key.MaterialName,
                g.Key.Gost ?? string.Empty,
                g.Key.UnitOfMeasure,
                g.Sum(x => x.Item.CalculatedVolume)))
            .ToList();

        return new IndividualCardVersionComparisonDto(
            source.Id,
            source.Code,
            source.Version,
            IndividualCardDisplay.ObjectLevel(source.ObjectLevel),
            IndividualCardDisplaySourceObjectName(source),
            source.BranchId,
            preflight.RootState,
            preflight.RootCandidates,
            preflight.SelectedRoot,
            preflight.SelectedRoot is not null && preflight.SelectedRoot.BranchId == source.BranchId,
            preflight.NormativeGaps,
            compositionChanges,
            hkChanges,
            source.CoefficientSnapshots.OrderBy(s => s.SortOrder)
                .Select(s => new IndividualCardCoefficientSnapshotDto(
                    s.Id, s.SourceCoefficientId, s.SourceCoefficientTypeId, s.CoefficientTypeName,
                    s.CoefficientName, s.Value, s.ConditionDescription, s.NormativeBasis, s.SortOrder))
                .ToList(),
            source.Items.Any(),
            previousTotals);
    }

    private static string IndividualCardDisplaySourceObjectName(IndividualCard card) => card.ObjectLevel switch
    {
        IndividualCardObjectLevel.Complex => card.Complex?.Name ?? string.Empty,
        IndividualCardObjectLevel.EquipmentModel => card.EquipmentModel?.Name ?? string.Empty,
        IndividualCardObjectLevel.Aggregate => card.Aggregate?.Name ?? string.Empty,
        IndividualCardObjectLevel.Node => card.Node?.Name ?? string.Empty,
        IndividualCardObjectLevel.EquipmentInstance => card.EquipmentInstance?.Name ?? string.Empty,
        _ => string.Empty,
    };

    private static string CompositionWhatForLevel(IndividualCardObjectLevel level) => level switch
    {
        IndividualCardObjectLevel.Complex => "Изделие",
        IndividualCardObjectLevel.EquipmentModel => "Агрегат",
        IndividualCardObjectLevel.Aggregate => "Узел",
        _ => "Состав",
    };

    private static string DescribeComposition(IndividualCardCompositionSnapshot cs) =>
        $"состав {cs.SourceCompositionVersion} ×{cs.Quantity}, агрегатов: {cs.Aggregates.Count}";

    private static string DescribePreflightComposition(IndividualCardPreflightCompositionDto dto) =>
        $"состав {dto.CompositionVersion} ×{dto.Quantity}, агрегатов: {dto.Aggregates.Count}";

    private static List<IndividualCardDiffEntryDto> BuildCompositionDiff(
        IndividualCard source, IndividualCardPreflightResult preflight)
    {
        var changes = new List<IndividualCardDiffEntryDto>();

        var beforeByTarget = source.CompositionSnapshots
            .GroupBy(cs => cs.TargetObjectId)
            .ToDictionary(g => g.Key, g => g.First());
        var afterByTarget = preflight.Compositions
            .GroupBy(c => c.TargetObjectId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var targetId in beforeByTarget.Keys.Union(afterByTarget.Keys))
        {
            var before = beforeByTarget.GetValueOrDefault(targetId);
            var after = afterByTarget.GetValueOrDefault(targetId);
            var what = before is not null ? CompositionWhatForLevel(before.SourceLevel) : CompositionWhatForLevel(after!.SourceLevel);
            var name = before?.TargetObjectCode ?? after!.TargetObjectCode;

            if (before is null)
            {
                changes.Add(new IndividualCardDiffEntryDto("Added", what, name, null, DescribePreflightComposition(after!)));
                continue;
            }
            if (after is null)
            {
                changes.Add(new IndividualCardDiffEntryDto("Removed", what, name, DescribeComposition(before), null));
                continue;
            }

            changes.Add(new IndividualCardDiffEntryDto(
                before.SourceCompositionId == after.CompositionId
                    && before.SourceCompositionVersion == after.CompositionVersion
                    && before.Quantity == after.Quantity
                    ? "Unchanged" : "Changed",
                what, name, DescribeComposition(before), DescribePreflightComposition(after)));

            // Aggregate quantities (было / стало).
            var beforeAggregates = before.Aggregates.GroupBy(a => a.AggregateId).ToDictionary(g => g.Key, g => g.First());
            var afterAggregates = after.Aggregates.GroupBy(a => a.AggregateId).ToDictionary(g => g.Key, g => g.First());
            foreach (var aggregateId in beforeAggregates.Keys.Union(afterAggregates.Keys))
            {
                var b = beforeAggregates.GetValueOrDefault(aggregateId);
                var a = afterAggregates.GetValueOrDefault(aggregateId);
                var aggregateName = b?.AggregateCode ?? a!.Code;
                if (b is null)
                    changes.Add(new IndividualCardDiffEntryDto("Added", "Агрегат", aggregateName, null, $"×{a!.Quantity}"));
                else if (a is null)
                    changes.Add(new IndividualCardDiffEntryDto("Removed", "Агрегат", aggregateName, $"×{b.Quantity}", null));
                else
                    changes.Add(new IndividualCardDiffEntryDto(
                        b.Quantity == a.Quantity ? "Unchanged" : "Changed",
                        "Агрегат", aggregateName, $"×{b.Quantity}", $"×{a.Quantity}"));

                // Node quantities under the aggregate occurrence path.
                var beforeNodes = (b?.Nodes ?? Enumerable.Empty<IndividualCardNodeSnapshot>())
                    .GroupBy(n => n.NodeId).ToDictionary(g => g.Key, g => g.First());
                var afterNodes = (a?.Nodes ?? Enumerable.Empty<IndividualCardPreflightNodeDto>())
                    .GroupBy(n => n.NodeId).ToDictionary(g => g.Key, g => g.First());
                foreach (var nodeId in beforeNodes.Keys.Union(afterNodes.Keys))
                {
                    var bn = beforeNodes.GetValueOrDefault(nodeId);
                    var an = afterNodes.GetValueOrDefault(nodeId);
                    var nodeName = bn?.NodeCode ?? an!.Code;
                    if (bn is null)
                        changes.Add(new IndividualCardDiffEntryDto("Added", "Узел", nodeName, null, $"×{an!.Quantity}"));
                    else if (an is null)
                        changes.Add(new IndividualCardDiffEntryDto("Removed", "Узел", nodeName, $"×{bn.Quantity}", null));
                    else
                        changes.Add(new IndividualCardDiffEntryDto(
                            bn.Quantity == an.Quantity ? "Unchanged" : "Changed",
                            "Узел", nodeName, $"×{bn.Quantity}", $"×{an.Quantity}"));
                }
            }
        }

        return changes;
    }

    /// <summary>HK source diff keyed by the tree position (ObjectId path),
    /// so the same source HKCardId in different branches is compared per
    /// occurrence context.</summary>
    private static List<IndividualCardDiffEntryDto> BuildHKSourceDiff(
        IndividualCard source, IndividualCardPreflightResult preflight)
    {
        var changes = new List<IndividualCardDiffEntryDto>();

        var sourceBySnapshotId = source.HKSourceSnapshots.ToDictionary(s => s.Id);
        string SourceKey(IndividualCardHKSourceSnapshot s)
        {
            var parts = new List<string> { $"{(int)s.ObjectLevel}:{s.SourceObjectId}" };
            var cursor = s.ParentHKSourceSnapshotId;
            while (cursor.HasValue && sourceBySnapshotId.TryGetValue(cursor.Value, out var parent))
            {
                parts.Insert(0, $"{(int)parent.ObjectLevel}:{parent.SourceObjectId}");
                cursor = parent.ParentHKSourceSnapshotId;
            }
            return string.Join(">", parts);
        }

        var preflightByOccurrence = preflight.HKSources.ToDictionary(s => s.PreflightOccurrenceId);
        string AfterKey(IndividualCardPreflightHKSourceDto s)
        {
            var parts = new List<string> { $"{(int)s.ObjectLevel}:{s.ObjectId}" };
            var cursor = s.ParentPreflightOccurrenceId;
            while (cursor.HasValue && preflightByOccurrence.TryGetValue(cursor.Value, out var parent))
            {
                parts.Insert(0, $"{(int)parent.ObjectLevel}:{parent.ObjectId}");
                cursor = parent.ParentPreflightOccurrenceId;
            }
            return string.Join(">", parts);
        }

        var before = source.HKSourceSnapshots.ToDictionary(SourceKey);
        var after = preflight.HKSources.ToDictionary(AfterKey);

        foreach (var key in before.Keys.Union(after.Keys))
        {
            var b = before.GetValueOrDefault(key);
            var a = after.GetValueOrDefault(key);
            var what = IndividualCardDisplay.ObjectLevel(b?.ObjectLevel ?? a!.ObjectLevel);
            var name = b?.SourceObjectName ?? a!.ObjectName;

            if (b is null)
                changes.Add(new IndividualCardDiffEntryDto("Added", what, name, null, $"{a!.HKCardCode} {a.HKCardVersion}"));
            else if (a is null)
                changes.Add(new IndividualCardDiffEntryDto("Removed", what, name, $"{b.HKCardCode} {b.HKCardVersion}", null));
            else
                changes.Add(new IndividualCardDiffEntryDto(
                    b.HKCardCode == a.HKCardCode && b.HKCardVersion == a.HKCardVersion ? "Unchanged" : "Changed",
                    what, name, $"{b.HKCardCode} {b.HKCardVersion}", $"{a.HKCardCode} {a.HKCardVersion}"));
        }

        return changes;
    }

    public async Task<IndividualCardDraftDto> CreateNewVersionAsync(
        CreateIndividualCardVersionRequest request, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardCreateVersion, ct);
        var scope = await ResolveActorScopeAsync(ct);

        var source = await LoadFormedSourceAsync(request.SourceIndividualCardId, scope, ct);
        var objectId = GetTargetObjectId(source)
            ?? throw new InvalidOperationException("ИК повреждена: не указан объект цели.");

        // Fresh preflight; no latest/newest fallback, no legacy links.
        var preflight = await BuildPreflightAsync(
            new IndividualCardPreflightRequest(source.ObjectLevel, objectId, request.RootHKCardId),
            demandCreateDraftPermission: false, ct);

        if (preflight.SelectedRoot is null)
        {
            throw new InvalidOperationException(preflight.RootState == IndividualCardPreflightRootState.SelectionRequired
                ? "Для создания новой версии ИК выберите утверждённую ХК вручную."
                : "Невозможно создать новую версию ИК: не найдена утверждённая ХК верхнего уровня.");
        }

        // A new version must live in the same branch as the source, even for a
        // SystemAdmin.
        if (preflight.SelectedRoot.BranchId != source.BranchId)
            throw new InvalidOperationException("Нельзя создать новую версию ИК по ХК другого филиала.");

        var successorExists = await _db.IndividualCards.AsNoTracking()
            .AnyAsync(c => c.SupersedesIndividualCardId == source.Id, ct);
        if (successorExists)
            throw new InvalidOperationException($"Для ИК «{source.Code} {source.Version}» новая версия уже создана.");

        var actorId = _currentUser.GetRequiredUserId();
        var now = _time.GetUtcNow().UtcDateTime;
        var newRevision = source.RevisionNumber + 1;

        // Deliberately NOT via public CreateDraftAsync: it rejects an existing
        // Code, while a new version intentionally reuses the source Code.
        var draft = new IndividualCard
        {
            Id = Guid.NewGuid(),
            Code = source.Code,
            Version = $"v{now:MMyy}.{newRevision}",
            RevisionNumber = newRevision,
            ObjectLevel = source.ObjectLevel,
            Status = IndividualCardStatus.Draft,
            BranchId = source.BranchId,
            CreatedByUserId = actorId.ToString(),
            CreatedAt = now,
            SupersedesIndividualCardId = source.Id,
        };
        ApplyTargetFk(draft, source.ObjectLevel, objectId);

        // Fresh composition/HK/gap snapshots only; no calculation rows,
        // materials, coefficient snapshots or TotalNorm are copied.
        CopyDraftSnapshots(draft, preflight, now);

        _db.IndividualCards.Add(draft);
        // The audit is written against the SOURCE card: the action performed
        // is "create a successor version of this Formed card".
        await _audit.CreateLogAsync(new AuditWriteRequest(
            "IndividualCard", source.Id.ToString(), "IndividualCard.NewVersionCreated",
            actorId, EntityDisplayName: $"{source.Code} {source.Version}",
            Details: $"SourceIndividualCardId={source.Id}; SourceVersion={source.Version}; NewIndividualCardId={draft.Id}; NewVersion={draft.Version}; NewRevision={newRevision}; BranchId={draft.BranchId}; RootHKCardId={preflight.SelectedRoot.HKCardId}; CompositionCount={preflight.Compositions.Count}; HKSourceCount={preflight.HKSources.Count}; NormativeGapCount={preflight.NormativeGaps.Count}"), ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException?.Message.Contains("UX_IndividualCards_SupersedesIndividualCardId") == true
            || ex.Message.Contains("UX_IndividualCards_SupersedesIndividualCardId"))
        {
            throw new InvalidOperationException($"Для ИК «{source.Code} {source.Version}» новая версия уже создана.");
        }

        return (await LoadDraftDetailedAsync(draft.Id, ct)) is { } reloaded ? ToDraftDto(reloaded) : ToDraftDto(draft);
    }

    public async Task ArchiveIndividualCardAsync(Guid individualCardId, CancellationToken ct = default)
    {
        await _permissions.DemandPermissionAsync(PermissionCodes.IndividualCardArchive, ct);
        var scope = await ResolveActorScopeAsync(ct);

        var card = await _db.IndividualCards
            .FirstOrDefaultAsync(d => d.Id == individualCardId, ct)
            ?? throw new InvalidOperationException("ИК не найдена.");

        if (card.Status != IndividualCardStatus.Formed)
            throw new InvalidOperationException("Архивировать можно только сформированную ИК.");

        if (!scope.IsSystemAdmin && card.BranchId != scope.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к ИК другого филиала.");

        var actorId = _currentUser.GetRequiredUserId();
        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            card.Status = IndividualCardStatus.Archived;
            card.ArchivedAt = now;
            card.ArchivedByUserId = actorId.ToString();

            await _audit.CreateLogAsync(new AuditWriteRequest(
                "IndividualCard", card.Id.ToString(), "IndividualCard.Archived",
                actorId, EntityDisplayName: $"{card.Code} {card.Version}",
                Details: $"ObjectLevel={card.ObjectLevel}; ObjectId={GetTargetObjectId(card)}; BranchId={card.BranchId}; Code={card.Code}; Version={card.Version}; RevisionNumber={card.RevisionNumber}"), ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<IndividualCard?> LoadDraftForCalculationAsync(
        Guid individualCardId, bool tracked = false, CancellationToken ct = default)
    {
        IQueryable<IndividualCard> query = _db.IndividualCards;
        if (!tracked)
            query = query.AsNoTracking();
        return await query
            .Include(d => d.Complex)
            .Include(d => d.EquipmentModel)
            .Include(d => d.Aggregate)
            .Include(d => d.Node)
            .Include(d => d.EquipmentInstance)
            .Include(d => d.CompositionSnapshots).ThenInclude(cs => cs.Aggregates)
            .Include(d => d.CompositionSnapshots).ThenInclude(cs => cs.Aggregates).ThenInclude(a => a.Nodes)
            .Include(d => d.HKSourceSnapshots)
            .Include(d => d.NormativeGapSnapshots)
            .Include(d => d.Items).ThenInclude(i => i.MaterialSnapshots)
            .Include(d => d.CoefficientSnapshots)
            .Include(d => d.CalculationProblemSnapshots)
            .FirstOrDefaultAsync(d => d.Id == individualCardId && d.Status == IndividualCardStatus.Draft, ct);
    }

    /// <summary>Occurrence-aware leaf traversal of the snapshot HK tree.
    /// Every node occurrence reachable through a chain without normative gaps
    /// produces its own calculation rows with factors resolved through the
    /// actual parent occurrence path — never by global source object ids.</summary>
    private async Task<(List<IndividualCardItem> Items, List<IndividualCardCalculationProblemDto> Problems,
        decimal TotalNorm, int PrimaryMaterialCount, decimal PrimaryTotal)>
        CalculateDraftRowsAsync(IndividualCard draft, decimal totalCoefficient, CancellationToken ct)
    {
        var problems = new List<IndividualCardCalculationProblemDto>();

        if (draft.NormativeGapSnapshots.Count > 0)
        {
            problems.Add(new IndividualCardCalculationProblemDto(
                "IncompleteNormativeChain",
                "Черновик ИК содержит нормативные разрывы цепочки; рассчитаны только полные ветки.",
                null, null, null, 0));
        }

        var brokenHkIds = draft.NormativeGapSnapshots
            .Where(g => g.RelatedHKCardId.HasValue)
            .Select(g => g.RelatedHKCardId!.Value)
            .ToHashSet();

        // Parent links store snapshot ids (ParentHKSourceSnapshotId), so the
        // occurrence walk is keyed by snapshot identity.
        var sourceBySnapshotId = draft.HKSourceSnapshots.ToDictionary(s => s.Id);

        // Node-level leaf occurrences with a complete ancestor chain (the root
        // reflects whole-card completeness and is not part of the chain check).
        var nodeOccurrences = new List<IndividualCardHKSourceSnapshot>();
        foreach (var source in draft.HKSourceSnapshots.Where(s => s.ObjectLevel == IndividualCardObjectLevel.Node))
        {
            var chainBroken = false;
            var cursor = source.ParentHKSourceSnapshotId;
            while (cursor.HasValue && sourceBySnapshotId.TryGetValue(cursor.Value, out var parent))
            {
                if (brokenHkIds.Contains(parent.SourceHKCardId))
                {
                    chainBroken = true;
                    break;
                }
                cursor = parent.ParentHKSourceSnapshotId;
            }

            if (!chainBroken)
                nodeOccurrences.Add(source);
        }

        // Snapshot quantity maps: composition by target object, aggregate by
        // (composition, aggregate source), node by (aggregate snapshot, node source).
        var compositionByTarget = draft.CompositionSnapshots
            .GroupBy(cs => cs.TargetObjectId)
            .ToDictionary(g => g.Key, g => g.First());
        var aggregateByCompositionAndSource = draft.CompositionSnapshots
            .SelectMany(cs => cs.Aggregates.Select(a => (cs, a)))
            .GroupBy(x => (x.cs.Id, x.a.AggregateId))
            .ToDictionary(g => g.Key, g => g.First().a);
        var nodeByAggregateAndSource = draft.CompositionSnapshots
            .SelectMany(cs => cs.Aggregates)
            .SelectMany(a => a.Nodes.Select(n => (a, n)))
            .GroupBy(x => (x.a.Id, x.n.NodeId))
            .ToDictionary(g => g.Key, g => g.First().n);

        // HKCardItem rows of the node source HK cards, one bounded query.
        var nodeHkIds = nodeOccurrences.Select(o => o.SourceHKCardId).Distinct().ToList();
        var hkItems = await _db.HKCardItems.AsNoTracking()
            .Include(i => i.Materials).ThenInclude(m => m.GsmMaterial)
            .Include(i => i.AssemblyUnit)
            .Where(i => nodeHkIds.Contains(i.HKCardId))
            .ToListAsync(ct);
        var itemsByHK = hkItems.ToLookup(i => i.HKCardId);

        var items = new List<IndividualCardItem>();
        var sortOrder = 0;

        foreach (var nodeOcc in nodeOccurrences.OrderBy(o => o.SortOrder))
        {
            // Structural multipliers through the actual occurrence path.
            var nodeQuantity = 1;
            var aggregateQuantity = 1;
            var productQuantity = 1;

            var aggregateOcc = nodeOcc.ParentHKSourceSnapshotId.HasValue
                && sourceBySnapshotId.TryGetValue(nodeOcc.ParentHKSourceSnapshotId.Value, out var p1)
                && p1.ObjectLevel == IndividualCardObjectLevel.Aggregate
                    ? p1
                    : null;

            IndividualCardCompositionSnapshot? composition = null;
            IndividualCardAggregateSnapshot? aggregateSnapshot = null;
            IndividualCardNodeSnapshot? nodeSnapshot = null;

            if (aggregateOcc is not null)
            {
                var productOcc = aggregateOcc.ParentHKSourceSnapshotId.HasValue
                    && sourceBySnapshotId.TryGetValue(aggregateOcc.ParentHKSourceSnapshotId.Value, out var p2)
                    && p2.ObjectLevel == IndividualCardObjectLevel.EquipmentModel
                        ? p2
                        : null;

                // Product occurrence: the model occurrence in a Complex tree or
                // the root itself for an Изделие target. Aggregate target: the
                // aggregate occurrence is the root and the composition matches it.
                var productObjectId = productOcc?.SourceObjectId ?? aggregateOcc.SourceObjectId;
                composition = compositionByTarget.GetValueOrDefault(productObjectId);

                if (composition is null)
                {
                    problems.Add(new IndividualCardCalculationProblemDto(
                        "MissingNodeHKSource",
                        $"Для узла «{nodeOcc.SourceObjectName}» не найден снимок состава в ветке расчёта.",
                        nodeOcc.SourceHKCardId, null, null, sortOrder));
                    continue;
                }

                aggregateSnapshot = aggregateByCompositionAndSource.GetValueOrDefault((composition.Id, aggregateOcc.SourceObjectId));
                if (aggregateSnapshot is null)
                {
                    problems.Add(new IndividualCardCalculationProblemDto(
                        "MissingNodeHKSource",
                        $"Для узла «{nodeOcc.SourceObjectName}» не найден снимок агрегата в ветке расчёта.",
                        nodeOcc.SourceHKCardId, null, null, sortOrder));
                    continue;
                }

                nodeSnapshot = nodeByAggregateAndSource.GetValueOrDefault((aggregateSnapshot.Id, nodeOcc.SourceObjectId));
                if (nodeSnapshot is null)
                {
                    problems.Add(new IndividualCardCalculationProblemDto(
                        "MissingNodeHKSource",
                        $"Для узла «{nodeOcc.SourceObjectName}» не найден снимок узла в ветке расчёта.",
                        nodeOcc.SourceHKCardId, null, null, sortOrder));
                    continue;
                }

                nodeQuantity = nodeSnapshot.Quantity;
                aggregateQuantity = aggregateSnapshot.Quantity;
                productQuantity = draft.ObjectLevel == IndividualCardObjectLevel.Complex
                    ? composition.Quantity
                    : 1;
            }

            var cardItems = itemsByHK[nodeOcc.SourceHKCardId].ToList();
            if (cardItems.Count == 0)
            {
                problems.Add(new IndividualCardCalculationProblemDto(
                    "MissingHKCardItem",
                    $"Узловая ХК «{nodeOcc.HKCardCode}», {nodeOcc.HKCardVersion} не содержит строк ГСМ.",
                    nodeOcc.SourceHKCardId, null, nodeSnapshot?.Id, sortOrder++));
                continue;
            }

            foreach (var hkItem in cardItems.OrderBy(i => i.SortOrder))
            {
                var unit = hkItem.UnitOfMeasure?.Trim() ?? string.Empty;
                var validUnit = unit.Equals("г", StringComparison.OrdinalIgnoreCase);
                var primaryMaterials = hkItem.Materials.Where(m => m.Category == GsmCategory.Primary).ToList();

                if (!validUnit)
                {
                    problems.Add(new IndividualCardCalculationProblemDto(
                        "InvalidUnitOfMeasure",
                        $"Строка «{hkItem.AssemblyUnit.Name}» ХК «{nodeOcc.HKCardCode}» имеет единицу измерения «{hkItem.UnitOfMeasure}»; формирование разрешено только в граммах.",
                        nodeOcc.SourceHKCardId, hkItem.Id, nodeSnapshot?.Id, sortOrder));
                }

                if (primaryMaterials.Count == 0)
                {
                    problems.Add(new IndividualCardCalculationProblemDto(
                        "MissingPrimaryMaterial",
                        $"Строка «{hkItem.AssemblyUnit.Name}» ХК «{nodeOcc.HKCardCode}» не имеет основного материала ГСМ.",
                        nodeOcc.SourceHKCardId, hkItem.Id, nodeSnapshot?.Id, sortOrder));
                }

                // BaseVolume = HKCardItem.Volume × HKCardItem.Quantity × Qnode ×
                // Qaggregate × Qproduct, decimal, no intermediate rounding.
                var baseVolume = hkItem.Volume * hkItem.Quantity
                    * nodeQuantity * aggregateQuantity * productQuantity;
                var calculatedVolume = decimal.Ceiling(baseVolume * totalCoefficient);

                var item = new IndividualCardItem
                {
                    Id = Guid.NewGuid(),
                    HKCardItemId = hkItem.Id,
                    NodeSnapshotId = nodeSnapshot?.Id,
                    SourceHKSourceSnapshotId = nodeOcc.Id,
                    SourceHKCardId = nodeOcc.SourceHKCardId,
                    SourceHKCardCode = nodeOcc.HKCardCode,
                    SourceHKCardVersion = nodeOcc.HKCardVersion,
                    SourceHKCardItemId = hkItem.Id,
                    AssemblyUnitCode = hkItem.AssemblyUnit.Code,
                    AssemblyUnitName = hkItem.AssemblyUnit.Name,
                    AssemblyUnitQuantity = hkItem.Quantity,
                    UnitOfMeasure = unit,
                    Periodicity = hkItem.Periodicity,
                    Notes = hkItem.Notes,
                    SourceVolume = hkItem.Volume,
                    BaseVolume = baseVolume,
                    CalculatedVolume = calculatedVolume,
                    SortOrder = sortOrder++,
                };

                var materialSortOrder = 0;
                foreach (var material in hkItem.Materials
                             .OrderBy(m => m.Category).ThenBy(m => m.GsmMaterial.Name))
                {
                    item.MaterialSnapshots.Add(new IndividualCardItemMaterialSnapshot
                    {
                        Id = Guid.NewGuid(),
                        IndividualCardItemId = item.Id,
                        SourceGsmMaterialId = material.GsmMaterialId,
                        MaterialName = material.GsmMaterial.Name,
                        MaterialType = material.GsmMaterial.Type,
                        Gost = material.GsmMaterial.Gost,
                        Category = material.Category,
                        CalculatedVolume = calculatedVolume,
                        UnitOfMeasure = unit,
                        SortOrder = materialSortOrder++,
                    });
                }

                items.Add(item);
            }
        }

        // Primary totals: only Primary materials participate; each parent row
        // contributes exactly once per material group.
        var primaryTotals = items
            .SelectMany(i => i.MaterialSnapshots.Where(m => m.Category == GsmCategory.Primary)
                .Select(m => (Item: i, Material: m)))
            .GroupBy(x => (x.Material.MaterialName, x.Material.Gost, x.Material.UnitOfMeasure))
            .OrderBy(g => g.Key.MaterialName)
            .Select(g => new
            {
                g.Key,
                TotalVolume = g.Sum(x => x.Item.CalculatedVolume),
                ItemCount = g.Select(x => x.Item.Id).Distinct().Count(),
            })
            .ToList();

        var totalNorm = items
            .Where(i => i.MaterialSnapshots.Any(m => m.Category == GsmCategory.Primary))
            .Sum(i => i.CalculatedVolume);
        var primaryMaterialCount = items.Sum(i => i.MaterialSnapshots.Count(m => m.Category == GsmCategory.Primary));

        return (items, problems, totalNorm, primaryMaterialCount, totalNorm);
    }

    private static decimal BuildTotalCoefficient(IEnumerable<IndividualCardCoefficientSnapshot> snapshots)
    {
        decimal total = 1m;
        foreach (var snapshot in snapshots)
            total *= snapshot.Value;
        return total;
    }

    private IndividualCardCalculationDto BuildCalculationDto(IndividualCard draft)
    {
        var coefficientSnapshots = draft.CoefficientSnapshots
            .OrderBy(s => s.SortOrder)
            .ToList();
        var totalCoefficient = BuildTotalCoefficient(coefficientSnapshots);

        var problems = new List<IndividualCardCalculationProblemDto>();

        // Problems are read from immutable recalculation snapshots; the D3
        // normative gap state is added live (gap-free refresh clears it).
        foreach (var snapshot in draft.CalculationProblemSnapshots
                     .Where(p => p.Code != "IncompleteNormativeChain")
                     .OrderBy(p => p.SortOrder))
        {
            problems.Add(new IndividualCardCalculationProblemDto(
                snapshot.Code, snapshot.Message,
                snapshot.HKCardId, snapshot.HKCardItemId, snapshot.NodeSnapshotId, snapshot.SortOrder));
        }

        if (draft.NormativeGapSnapshots.Count > 0)
        {
            problems.Add(new IndividualCardCalculationProblemDto(
                "IncompleteNormativeChain", "Черновик ИК содержит нормативные разрывы цепочки.",
                null, null, null, 0));
        }

        var rows = draft.Items.OrderBy(i => i.SortOrder)
            .Select(i =>
            {
                // Row factors resolved through the branch's node snapshot —
                // repeated node sources under different parents carry distinct
                // node snapshot ids, so factors stay per-branch.
                var nodeQuantity = 0;
                var aggregateQuantity = 0;
                var productQuantity = 0;
                if (i.NodeSnapshotId.HasValue)
                {
                    var nodeSnapshot = draft.CompositionSnapshots
                        .SelectMany(cs => cs.Aggregates)
                        .SelectMany(a => a.Nodes.Select(n => (a, n)))
                        .FirstOrDefault(x => x.n.Id == i.NodeSnapshotId.Value);
                    if (nodeSnapshot.n is not null && nodeSnapshot.a is not null)
                    {
                        var aggregateSnapshot = nodeSnapshot.a;
                        var composition = draft.CompositionSnapshots
                            .FirstOrDefault(cs => cs.Id == aggregateSnapshot.IndividualCardCompositionSnapshotId);
                        nodeQuantity = nodeSnapshot.n.Quantity;
                        aggregateQuantity = aggregateSnapshot.Quantity;
                        productQuantity = draft.ObjectLevel == IndividualCardObjectLevel.Complex
                            ? composition?.Quantity ?? 0
                            : 1;
                    }
                }

                return new IndividualCardCalculationRowDto(
                    i.Id,
                    i.NodeSnapshotId ?? Guid.Empty,
                    i.SourceHKCardId,
                    i.SourceHKCardCode,
                    i.SourceHKCardVersion,
                    i.AssemblyUnitCode,
                    i.AssemblyUnitName,
                    i.AssemblyUnitQuantity,
                    nodeQuantity,
                    aggregateQuantity,
                    productQuantity,
                    i.SourceVolume,
                    i.BaseVolume,
                    i.CalculatedVolume,
                    i.UnitOfMeasure,
                    i.SortOrder,
                    Materials: i.MaterialSnapshots.OrderBy(m => m.SortOrder)
                        .Select(m => new IndividualCardCalculationMaterialDto(
                            m.Id, m.SourceGsmMaterialId, m.MaterialName, m.MaterialType,
                            m.Gost, m.Category, m.CalculatedVolume, m.UnitOfMeasure, m.SortOrder))
                        .ToList());
            })
            .ToList();

        var primaryTotals = draft.Items
            .SelectMany(i => i.MaterialSnapshots.Where(m => m.Category == GsmCategory.Primary)
                .Select(m => (Item: i, Material: m)))
            .GroupBy(x => (x.Material.MaterialName, x.Material.Gost, x.Material.UnitOfMeasure))
            .OrderBy(g => g.Key.MaterialName)
            .Select(g => new IndividualCardPrimaryTotalDto(
                g.Key.MaterialName,
                g.Key.Gost ?? string.Empty,
                g.Key.UnitOfMeasure,
                g.Sum(x => x.Item.CalculatedVolume),
                g.Select(x => x.Item.Id).Distinct().Count()))
            .ToList();

        var isReadyToForm = draft.Status == IndividualCardStatus.Draft
            && draft.NormativeGapSnapshots.Count == 0
            && problems.Count == 0
            && draft.Items.Count > 0
            && IndividualCardStatusTransitions.IsAllowed(draft.Status, IndividualCardStatus.Formed);

        return new IndividualCardCalculationDto(
            draft.Id,
            draft.Code,
            draft.Version,
            draft.ObjectLevel,
            IndividualCardDisplay.ObjectLevel(draft.ObjectLevel),
            draft.Status,
            draft.BranchId,
            coefficientSnapshots.Select(s => new IndividualCardCoefficientSnapshotDto(
                s.Id, s.SourceCoefficientId, s.SourceCoefficientTypeId, s.CoefficientTypeName,
                s.CoefficientName, s.Value, s.ConditionDescription, s.NormativeBasis, s.SortOrder)).ToList(),
            totalCoefficient,
            draft.TotalNorm,
            rows,
            primaryTotals,
            problems,
            isReadyToForm);
    }
}
