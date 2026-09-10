using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class SearchService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TimeProvider _time;
    private readonly IPermissionService _permissions;

    /// <summary>Ограничение кандидатов на одну ветку поиска — предохраняет объём выборки.</summary>
    private const int ExtendedLimit = 200;

    public SearchService(
        AppDbContext db,
        ICurrentUserService currentUser,
        UserManager<ApplicationUser> userManager,
        TimeProvider time,
        IPermissionService permissions)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
        _time = time;
        _permissions = permissions;
    }

    public async Task<List<SearchResultItem>> SearchAsync(string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        var q = query.Trim();
        var qPattern = "%" + EscapeLikeFragment(q) + "%";
        var results = new List<SearchResultItem>();

        var hkCards = await _db.HKCards
            .Include(c => c.Node)
            .Where(c => EF.Functions.ILike(c.Code, qPattern) ||
                        EF.Functions.ILike(c.Version, qPattern) ||
                        EF.Functions.ILike(c.Purpose ?? "", qPattern) ||
                        EF.Functions.ILike(c.Notes ?? "", qPattern) ||
                        EF.Functions.ILike(c.NormativeBasis ?? "", qPattern) ||
                        EF.Functions.ILike(c.RequestOrganization ?? "", qPattern) ||
                        EF.Functions.ILike(c.RequestSenderFullName ?? "", qPattern) ||
                        EF.Functions.ILike(c.IncomingLetterNumber ?? "", qPattern) ||
                        EF.Functions.ILike(c.OutgoingLetterNumber ?? "", qPattern) ||
                        EF.Functions.ILike(c.RequestDetails ?? "", qPattern))
            .Take(maxResults)
            .ToListAsync();

        foreach (var c in hkCards)
        {
            var match = FindMatchExtended(c, q);
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
            .Where(n => !n.IsDraft &&
                        (EF.Functions.ILike(n.Code, qPattern) ||
                        EF.Functions.ILike(n.Name, qPattern) ||
                        EF.Functions.ILike(n.Description ?? "", qPattern)))
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
            .Where(m => EF.Functions.ILike(m.Index, qPattern) ||
                        EF.Functions.ILike(m.Name, qPattern) ||
                        EF.Functions.ILike(m.Type ?? "", qPattern) ||
                        EF.Functions.ILike(m.Brand ?? "", qPattern) ||
                        EF.Functions.ILike(m.Modification ?? "", qPattern))
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
            .Where(i => EF.Functions.ILike(i.SerialNumber, qPattern) ||
                        EF.Functions.ILike(i.Index, qPattern) ||
                        EF.Functions.ILike(i.Name, qPattern) ||
                        EF.Functions.ILike(i.Description ?? "", qPattern))
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
            .Where(m => !m.IsDraft &&
                        (EF.Functions.ILike(m.Name, qPattern) ||
                        EF.Functions.ILike(m.Type, qPattern) ||
                        EF.Functions.ILike(m.Gost ?? "", qPattern)))
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
            .Where(a => !a.IsDraft &&
                        (EF.Functions.ILike(a.Code, qPattern) ||
                        EF.Functions.ILike(a.Name, qPattern) ||
                        EF.Functions.ILike(a.Description ?? "", qPattern)))
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

    /// <summary>Экранирование метасимволов LIKE в пользовательском вводе.</summary>
    private static string EscapeLikeFragment(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>ILIKE-паттерн с экранированием для расширенного поиска.</summary>
    private static string Pattern(string value) => "%" + EscapeLikeFragment(value) + "%";

    /// <summary>сличение ILIKE с явным escape-символом.</summary>
    private static bool ILike(string column, string pattern) =>
        EF.Functions.ILike(column, pattern, @"\");

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

    private static string? FindMatchExtended(HKCard c, string q)
    {
        var qLower = q.ToLowerInvariant();
        if (!string.IsNullOrEmpty(c.RequestOrganization) && c.RequestOrganization.ToLowerInvariant().Contains(qLower)) return "Организация";
        if (!string.IsNullOrEmpty(c.RequestSenderFullName) && c.RequestSenderFullName.ToLowerInvariant().Contains(qLower)) return "ФИО отправителя";
        if (!string.IsNullOrEmpty(c.IncomingLetterNumber) && c.IncomingLetterNumber.ToLowerInvariant().Contains(qLower)) return "Входящий номер";
        if (!string.IsNullOrEmpty(c.OutgoingLetterNumber) && c.OutgoingLetterNumber.ToLowerInvariant().Contains(qLower)) return "Исходящий номер";
        if (!string.IsNullOrEmpty(c.RequestDetails) && c.RequestDetails.ToLowerInvariant().Contains(qLower)) return "Основание";
        return FindMatch(c.Code, c.Version, c.Purpose, c.Notes, c.NormativeBasis, q);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Расширенный поиск (страница /поиск и API /api/search)
    // ────────────────────────────────────────────────────────────────────────

    private sealed record CandidateRow(
        string EntityType,
        Guid EntityId,
        string Title,
        string? Subtitle,
        string? Code,
        string? Version,
        string StatusKey,
        Guid? BranchId,
        DateTime? CreatedAt,
        DateTime? ApprovedDate,
        string MatchContext,
        int Rank);

    private sealed record ActorScope(bool IsSystemAdmin, Guid? UserBranchId, string UserId);

    public async Task<SearchPageDto> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 10, 100);
        var page = Math.Max(1, query.Page);
        var text = (query.Text ?? string.Empty).Trim();
        var pattern = Pattern(text);
        var hasText = text.Length > 0;
        var hasFilters =
            query.EntityType is not null ||
            query.CreatedFrom is not null ||
            query.CreatedTo is not null ||
            query.HKCardStatus is not null ||
            query.HKObjectLevel is not null ||
            query.HKValidity is not null ||
            query.HasAttachment is not null ||
            query.TaskStatus is not null ||
            query.TaskPriority is not null ||
            query.BranchId is not null;

        // Полностью пустая страница не выполняет ни один запрос.
        if (!hasText && !hasFilters)
            return new SearchPageDto([], 0, page, pageSize, 0);

        var scope = await LoadActorScopeAsync(ct);
        var branchFilter = scope.IsSystemAdmin ? query.BranchId : scope.UserBranchId;

        var wanted = string.IsNullOrWhiteSpace(query.EntityType) || query.EntityType == "Все"
            ? null
            : query.EntityType
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Wanted(params string[] types) => wanted is null || types.Any(wanted.Contains);

        var canHK = Wanted("HKCard") && await _permissions.HasPermissionAsync(scope.UserId, Chernika.Domain.PermissionCodes.HKView);
        var canIC = Wanted("IndividualCard") && await _permissions.HasPermissionAsync(scope.UserId, Chernika.Domain.PermissionCodes.IndividualCardView);
        var canReference = Wanted(
                "Complex", "EquipmentModel", "Aggregate", "Node", "AssemblyUnit",
                "EquipmentInstance", "GsmMaterial", "Coefficient")
            && await _permissions.HasPermissionAsync(scope.UserId, Chernika.Domain.PermissionCodes.ReferenceView);
        var canTask = Wanted("WorkTask") &&
                      (await _permissions.HasPermissionAsync(scope.UserId, Chernika.Domain.PermissionCodes.TaskView) ||
                       await _permissions.HasPermissionAsync(scope.UserId, Chernika.Domain.PermissionCodes.TaskViewOwn));

        // Инвертированный период — контролируемая ошибка валидации.
        if (query.CreatedFrom is { } cf && query.CreatedTo is { } ct2 && cf.Date > ct2.Date)
            throw new ArgumentException("Период задан некорректно: «Создано с» позже «Создано по».");

        // Локальные календарные границы → UTC-интервал (CreatedTo включает весь день).
        DateTime? from = query.CreatedFrom is { } createdFrom ? DateTime.SpecifyKind(createdFrom.Date, DateTimeKind.Utc) : null;
        DateTime? to = query.CreatedTo is { } createdTo
            ? DateTime.SpecifyKind(createdTo.Date.AddDays(1), DateTimeKind.Utc).AddTicks(-1)
            : null;

        var candidates = new List<CandidateRow>();
        var now = _time.GetUtcNow().UtcDateTime;

        // ── Зависимые «якоря»: марки ГСМ и сборочные единицы по тексту ─────
        var materialIds = new List<Guid>();
        var unitIds = new List<Guid>();
        if (hasText && (canHK || canIC || canReference))
        {
            materialIds = await _db.GsmMaterials.AsNoTracking()
                .Where(m => !m.IsDraft && !m.IsDeleted)
                .Where(m => EF.Functions.ILike(m.Name, pattern, @"\") ||
                            EF.Functions.ILike(m.Type, pattern, @"\") ||
                            EF.Functions.ILike(m.Gost ?? "", pattern, @"\") ||
                            EF.Functions.ILike(m.Description ?? "", pattern, @"\"))
                .OrderBy(m => m.Name)
                .Take(ExtendedLimit)
                .Select(m => m.Id)
                .ToListAsync(ct);

            unitIds = await _db.AssemblyUnits.AsNoTracking()
                .Where(a => !a.IsDraft && !a.IsDeleted)
                .Where(a => EF.Functions.ILike(a.Code, pattern, @"\") ||
                            EF.Functions.ILike(a.Name, pattern, @"\") ||
                            EF.Functions.ILike(a.Description ?? "", pattern, @"\"))
                .OrderBy(a => a.Code)
                .Take(ExtendedLimit)
                .Select(a => a.Id)
                .ToListAsync(ct);
        }

        // ── Химмотологические карты: прямой + зависимый поиск ────────────
        var hkAnchorIds = new HashSet<Guid>();
        if (canHK)
        {
            var hk = HKCardsQuery(query, scope, branchFilter, from, to, now);

            var direct = await hk
                .Where(c => EF.Functions.ILike(c.Code, pattern, @"\") ||
                            EF.Functions.ILike(c.Version, pattern, @"\") ||
                            EF.Functions.ILike(c.Purpose ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.Notes ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.NormativeBasis ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.RequestOrganization ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.RequestSenderFullName ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.IncomingLetterNumber ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.OutgoingLetterNumber ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.RequestDetails ?? "", pattern, @"\"))
                .OrderByDescending(c => c.CreatedAt)
                .Take(ExtendedLimit)
                .Select(c => new
                {
                    c.Id,
                    c.Code,
                    c.Version,
                    Status = c.Status.ToString(),
                    c.BranchId,
                    c.CreatedAt,
                    c.ApprovedDate,
                    ObjectName = c.Node != null ? c.Node.Name :
                        c.Aggregate != null ? c.Aggregate.Name :
                        c.EquipmentModel != null ? c.EquipmentModel.Name :
                        c.Complex != null ? c.Complex.Name : (string?)null,
                    ObjectCode = c.Node != null ? c.Node.Code :
                        c.Aggregate != null ? c.Aggregate.Code :
                        c.EquipmentModel != null ? c.EquipmentModel.Index :
                        c.Complex != null ? c.Complex.Code : (string?)null,
                })
                .ToListAsync(ct);
            foreach (var c in direct)
            {
                candidates.Add(new CandidateRow(
                    "HKCard", c.Id, $"{c.Code} (v{c.Version})",
                    c.ObjectName is null ? null : $"{c.ObjectCode} — {c.ObjectName}",
                    c.Code, c.Version, c.Status.ToString(), c.BranchId, c.CreatedAt, c.ApprovedDate,
                    "Совпадение: реквизиты ХК", 0));
                hkAnchorIds.Add(c.Id);
            }

            if (materialIds.Count > 0)
            {
                var viaMaterial = await hk
                    .Where(c => c.Items.Any(i => i.Materials.Any(m => materialIds.Contains(m.GsmMaterialId))))
                    .OrderByDescending(c => c.CreatedAt)
                    .Take(ExtendedLimit)
                    .Select(c => new
                    {
                        c.Id, c.Code, c.Version, Status = c.Status.ToString(), c.BranchId, c.CreatedAt, c.ApprovedDate,
                    })
                    .ToListAsync(ct);
                foreach (var c in viaMaterial)
                {
                    candidates.Add(new CandidateRow(
                        "HKCard", c.Id, $"{c.Code} (v{c.Version})", null,
                        c.Code, c.Version, c.Status.ToString(), c.BranchId, c.CreatedAt, c.ApprovedDate,
                        "Совпадение: используемая марка ГСМ в строках ХК", 1));
                    hkAnchorIds.Add(c.Id);
                }
            }

            if (unitIds.Count > 0)
            {
                var viaUnit = await hk
                    .Where(c => c.Items.Any(i => unitIds.Contains(i.AssemblyUnitId)))
                    .OrderByDescending(c => c.CreatedAt)
                    .Take(ExtendedLimit)
                    .Select(c => new
                    {
                        c.Id, c.Code, c.Version, Status = c.Status.ToString(), c.BranchId, c.CreatedAt, c.ApprovedDate,
                    })
                    .ToListAsync(ct);
                foreach (var c in viaUnit)
                {
                    candidates.Add(new CandidateRow(
                        "HKCard", c.Id, $"{c.Code} (v{c.Version})", null,
                        c.Code, c.Version, c.Status.ToString(), c.BranchId, c.CreatedAt, c.ApprovedDate,
                        "Совпадение: используемая сборочная единица в строках ХК", 1));
                    hkAnchorIds.Add(c.Id);
                }
            }
        }

        // ── Объектные «якоря» из ХК-совпадений (включая родительские ХК) ───
        var objNodeIds = new List<Guid>();
        var objAggregateIds = new List<Guid>();
        var objModelIds = new List<Guid>();
        var objComplexIds = new List<Guid>();
        if (hkAnchorIds.Count > 0)
        {
            var parents = await _db.HKCardComponents.AsNoTracking()
                .Where(comp => hkAnchorIds.Contains(comp.ChildHKCardId))
                .Select(comp => comp.ParentHKCardId)
                .Distinct()
                .Take(ExtendedLimit)
                .ToListAsync(ct);

            var anchors = hkAnchorIds.Concat(parents).ToHashSet();
            objNodeIds = await _db.HKCards.AsNoTracking()
                .Where(c => anchors.Contains(c.Id) && c.NodeId != null)
                .Select(c => c.NodeId!.Value)
                .Distinct()
                .ToListAsync(ct);
            objAggregateIds = await _db.HKCards.AsNoTracking()
                .Where(c => anchors.Contains(c.Id) && c.AggregateId != null)
                .Select(c => c.AggregateId!.Value)
                .Distinct()
                .ToListAsync(ct);
            objModelIds = await _db.HKCards.AsNoTracking()
                .Where(c => anchors.Contains(c.Id) && c.EquipmentModelId != null)
                .Select(c => c.EquipmentModelId!.Value)
                .Distinct()
                .ToListAsync(ct);
            objComplexIds = await _db.HKCards.AsNoTracking()
                .Where(c => anchors.Contains(c.Id) && c.ComplexId != null)
                .Select(c => c.ComplexId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        // ── Справочники (прямой + зависимый поиск) ────────────────────────
        if (canReference)
        {
            if (Wanted("Complex"))
            {
                var rows = await _db.Complexes.AsNoTracking()
                    .Where(c => !c.IsDeleted)
                    .Where(c => !hasText ||
                                EF.Functions.ILike(c.Code, pattern, @"\") ||
                                EF.Functions.ILike(c.Name, pattern, @"\") ||
                                EF.Functions.ILike(c.Description ?? "", pattern, @"\") ||
                                (objComplexIds.Count > 0 && objComplexIds.Contains(c.Id)))
                    .OrderBy(c => c.Code)
                    .Take(ExtendedLimit)
                    .Select(c => new
                    {
                        c.Id, c.Code, c.Name, c.Description,
                        IsAnchor = objComplexIds.Contains(c.Id),
                    })
                    .ToListAsync(ct);
                foreach (var c in rows)
                {
                    candidates.Add(new CandidateRow(
                        "Complex", c.Id, $"{c.Code} — {c.Name}", c.Description,
                        c.Code, null, "Complex", null, null, null,
                        c.IsAnchor ? "Совпадение: нормативная ХК комплекса" : "Совпадение: реквизиты комплекса",
                        c.IsAnchor ? 1 : 0));
                }
            }

            if (Wanted("EquipmentModel"))
            {
                var rows = await _db.EquipmentModels.AsNoTracking()
                    .Where(m => !m.IsDeleted)
                    .Where(m => !hasText ||
                                EF.Functions.ILike(m.Index, pattern, @"\") ||
                                EF.Functions.ILike(m.Name, pattern, @"\") ||
                                EF.Functions.ILike(m.Type ?? "", pattern, @"\") ||
                                EF.Functions.ILike(m.Brand ?? "", pattern, @"\") ||
                                EF.Functions.ILike(m.Modification ?? "", pattern, @"\") ||
                                (objModelIds.Count > 0 && objModelIds.Contains(m.Id)))
                    .OrderBy(m => m.Index)
                    .Take(ExtendedLimit)
                    .Select(m => new
                    {
                        m.Id, m.Index, m.Name, m.Brand, m.Type,
                        IsAnchor = objModelIds.Contains(m.Id),
                    })
                    .ToListAsync(ct);
                foreach (var m in rows)
                {
                    candidates.Add(new CandidateRow(
                        "EquipmentModel", m.Id, $"{m.Index} — {m.Name}",
                        string.IsNullOrEmpty(m.Brand) ? m.Type : $"{m.Brand} / {m.Type}",
                        m.Index, null, "EquipmentModel", null, null, null,
                        m.IsAnchor ? "Совпадение: нормативная ХК изделия" : "Совпадение: реквизиты изделия",
                        m.IsAnchor ? 1 : 0));
                }
            }

            if (Wanted("Aggregate"))
            {
                var rows = await _db.Aggregates.AsNoTracking()
                    .Where(a => !a.IsDeleted)
                    .Where(a => !hasText ||
                                EF.Functions.ILike(a.Code, pattern, @"\") ||
                                EF.Functions.ILike(a.Name, pattern, @"\") ||
                                EF.Functions.ILike(a.Description ?? "", pattern, @"\") ||
                                (objAggregateIds.Count > 0 && objAggregateIds.Contains(a.Id)))
                    .OrderBy(a => a.Code)
                    .Take(ExtendedLimit)
                    .Select(a => new
                    {
                        a.Id, a.Code, a.Name, a.Description,
                        IsAnchor = objAggregateIds.Contains(a.Id),
                    })
                    .ToListAsync(ct);
                foreach (var a in rows)
                {
                    candidates.Add(new CandidateRow(
                        "Aggregate", a.Id, $"{a.Code} — {a.Name}", a.Description,
                        a.Code, null, "Aggregate", null, null, null,
                        a.IsAnchor ? "Совпадение: нормативная ХК агрегата" : "Совпадение: реквизиты агрегата",
                        a.IsAnchor ? 1 : 0));
                }
            }

            if (Wanted("Node"))
            {
                var rows = await _db.Nodes.AsNoTracking()
                    .Where(n => !n.IsDeleted && !n.IsDraft)
                    .Where(n => !hasText ||
                                EF.Functions.ILike(n.Code, pattern, @"\") ||
                                EF.Functions.ILike(n.Name, pattern, @"\") ||
                                EF.Functions.ILike(n.Description ?? "", pattern, @"\") ||
                                (objNodeIds.Count > 0 && objNodeIds.Contains(n.Id)))
                    .OrderBy(n => n.Code)
                    .Take(ExtendedLimit)
                    .Select(n => new
                    {
                        n.Id, n.Code, n.Name, n.Description,
                        IsAnchor = objNodeIds.Contains(n.Id),
                    })
                    .ToListAsync(ct);
                foreach (var n in rows)
                {
                    candidates.Add(new CandidateRow(
                        "Node", n.Id, $"{n.Code} — {n.Name}", n.Description,
                        n.Code, null, "Node", null, null, null,
                        n.IsAnchor ? "Совпадение: нормативная ХК узла" : "Совпадение: реквизиты узла",
                        n.IsAnchor ? 1 : 0));
                }
            }

            if (Wanted("AssemblyUnit"))
            {
                var rows = await _db.AssemblyUnits.AsNoTracking()
                    .Where(a => !a.IsDeleted && !a.IsDraft)
                    .Where(a => !hasText ||
                                EF.Functions.ILike(a.Code, pattern, @"\") ||
                                EF.Functions.ILike(a.Name, pattern, @"\") ||
                                EF.Functions.ILike(a.Description ?? "", pattern, @"\") ||
                                (unitIds.Count > 0 && unitIds.Contains(a.Id)))
                    .OrderBy(a => a.Code)
                    .Take(ExtendedLimit)
                    .Select(a => new
                    {
                        a.Id, a.Code, a.Name, a.Description,
                        IsAnchor = unitIds.Contains(a.Id),
                    })
                    .ToListAsync(ct);
                foreach (var a in rows)
                {
                    candidates.Add(new CandidateRow(
                        "AssemblyUnit", a.Id, $"{a.Code} — {a.Name}", a.Description,
                        a.Code, null, "AssemblyUnit", null, null, null,
                        a.IsAnchor ? "Совпадение: используется в строках ХК" : "Совпадение: реквизиты сборочной единицы",
                        a.IsAnchor ? 1 : 0));
                }
            }

            if (Wanted("EquipmentInstance"))
            {
                var rows = await _db.EquipmentInstances.AsNoTracking()
                    .Where(i => !i.IsDeleted)
                    .Where(i => !hasText ||
                                EF.Functions.ILike(i.SerialNumber, pattern, @"\") ||
                                EF.Functions.ILike(i.Index, pattern, @"\") ||
                                EF.Functions.ILike(i.Name, pattern, @"\") ||
                                EF.Functions.ILike(i.Description ?? "", pattern, @"\"))
                    .OrderBy(i => i.SerialNumber)
                    .Take(ExtendedLimit)
                    .Select(i => new
                    {
                        i.Id, i.SerialNumber, i.Name, i.Description,
                        ModelIndex = i.EquipmentModel.Index,
                        IsAnchor = objModelIds.Contains(i.EquipmentModelId),
                    })
                    .ToListAsync(ct);
                foreach (var i in rows)
                {
                    candidates.Add(new CandidateRow(
                        "EquipmentInstance", i.Id, $"{i.SerialNumber} — {i.Name}", i.Description,
                        i.SerialNumber, null, "EquipmentInstance", null, null, null,
                        i.IsAnchor ? "Совпадение: модель из найденной ХК" : "Совпадение: реквизиты экземпляра",
                        i.IsAnchor ? 1 : 0));
                }
            }

            if (Wanted("GsmMaterial"))
            {
                var rows = await _db.GsmMaterials.AsNoTracking()
                    .Where(m => !m.IsDraft && !m.IsDeleted)
                    .Where(m => !hasText ||
                                EF.Functions.ILike(m.Name, pattern, @"\") ||
                                EF.Functions.ILike(m.Type, pattern, @"\") ||
                                EF.Functions.ILike(m.Gost ?? "", pattern, @"\") ||
                                EF.Functions.ILike(m.Description ?? "", pattern, @"\"))
                    .OrderBy(m => m.Name)
                    .Take(ExtendedLimit)
                    .Select(m => new
                    {
                        m.Id, m.Name, m.Type, m.Gost, m.Description,
                    })
                    .ToListAsync(ct);
                foreach (var m in rows)
                {
                    candidates.Add(new CandidateRow(
                        "GsmMaterial", m.Id, m.Name,
                        string.IsNullOrEmpty(m.Gost) ? m.Type : $"{m.Type} ({m.Gost})",
                        m.Name, null, "GsmMaterial", null, null, null,
                        !string.IsNullOrEmpty(m.Gost) && hasText && m.Gost.Contains(text, StringComparison.OrdinalIgnoreCase)
                            ? $"Совпадение: ГОСТ/ТУ {m.Gost}"
                            : "Совпадение: реквизиты марки ГСМ",
                        0));
                }
            }

            if (Wanted("Coefficient"))
            {
                var rows = await _db.Coefficients.AsNoTracking()
                    .Where(k => !k.IsDeleted && k.IsActive)
                    .Where(k => !hasText ||
                                EF.Functions.ILike(k.Name, pattern, @"\") ||
                                EF.Functions.ILike(k.ConditionDescription ?? "", pattern, @"\") ||
                                EF.Functions.ILike(k.NormativeBasis ?? "", pattern, @"\") ||
                                EF.Functions.ILike(k.CoefficientType.Name, pattern, @"\"))
                    .OrderBy(k => k.Name)
                    .Take(ExtendedLimit)
                    .Select(k => new
                    {
                        k.Id, k.Name, TypeName = k.CoefficientType.Name, k.CreatedAt,
                    })
                    .ToListAsync(ct);
                foreach (var k in rows)
                {
                    candidates.Add(new CandidateRow(
                        "Coefficient", k.Id, k.Name, k.TypeName,
                        k.Name, null, "Coefficient", null, k.CreatedAt, null,
                        "Совпадение: реквизиты коэффициента", 0));
                }
            }
        }

        // ── Индивидуальные карты: прямой + зависимый поиск ────────────────
        if (canIC)
        {
            var ic = IndividualCardsQuery(query, scope, branchFilter, from, to);

            var icDirect = await ic
                .Where(c => EF.Functions.ILike(c.Code, pattern, @"\") ||
                            EF.Functions.ILike(c.Version, pattern, @"\") ||
                            EF.Functions.ILike(c.Notes ?? "", pattern, @"\") ||
                            EF.Functions.ILike(c.TargetObjectCodeSnapshot, pattern, @"\") ||
                            EF.Functions.ILike(c.TargetObjectNameSnapshot, pattern, @"\") ||
                            EF.Functions.ILike(c.TargetContextSnapshot ?? "", pattern, @"\"))
                .OrderByDescending(c => c.CreatedAt)
                .Take(ExtendedLimit)
                .Select(c => new
                {
                    c.Id, c.Code, c.Version, c.Status, c.BranchId, c.CreatedAt,
                    c.TargetObjectCodeSnapshot, c.TargetObjectNameSnapshot,
                })
                .ToListAsync(ct);
            foreach (var c in icDirect)
            {
                candidates.Add(new CandidateRow(
                    "IndividualCard", c.Id,
                    $"{c.TargetObjectCodeSnapshot} — {c.TargetObjectNameSnapshot}",
                    $"{c.Code} (v{c.Version})",
                    c.Code, c.Version, c.Status.ToString(), c.BranchId, c.CreatedAt, null,
                    "Совпадение: реквизиты ИК", 0));
            }

            var directIds = icDirect.Select(d => d.Id).ToHashSet();
            if (hasText)
            {
                var icDependent = await ic
                    .Where(c => !directIds.Contains(c.Id))
                    .Where(c => c.Items.Any(i => i.MaterialSnapshots.Any(ms =>
                                    EF.Functions.ILike(ms.MaterialName, pattern, @"\") ||
                                    EF.Functions.ILike(ms.Gost ?? "", pattern, @"\"))) ||
                                c.Items.Any(i => EF.Functions.ILike(i.AssemblyUnitCode, pattern, @"\") ||
                                                 EF.Functions.ILike(i.AssemblyUnitName ?? "", pattern, @"\")) ||
                                c.Items.Any(i => EF.Functions.ILike(i.SourceHKCardCode, pattern, @"\") ||
                                                 EF.Functions.ILike(i.SourceHKCardVersion ?? "", pattern, @"\")) ||
                                c.HKSourceSnapshots.Any(h => EF.Functions.ILike(h.HKCardCode, pattern, @"\")) ||
                                c.HKSourceSnapshots.Any(h => EF.Functions.ILike(h.SourceObjectCode, pattern, @"\") ||
                                                             EF.Functions.ILike(h.SourceObjectName, pattern, @"\")) ||
                                c.CoefficientSnapshots.Any(cs =>
                                    EF.Functions.ILike(cs.CoefficientName, pattern, @"\") ||
                                    EF.Functions.ILike(cs.CoefficientTypeName, pattern, @"\")))
                    .OrderByDescending(c => c.CreatedAt)
                    .Take(ExtendedLimit)
                    .Select(c => new
                    {
                        c.Id, c.Code, c.Version, c.Status, c.BranchId, c.CreatedAt,
                        c.TargetObjectCodeSnapshot, c.TargetObjectNameSnapshot,
                    })
                    .ToListAsync(ct);
                foreach (var c in icDependent)
                {
                    candidates.Add(new CandidateRow(
                        "IndividualCard", c.Id,
                        $"{c.TargetObjectCodeSnapshot} — {c.TargetObjectNameSnapshot}",
                        $"{c.Code} (v{c.Version})",
                        c.Code, c.Version, c.Status.ToString(), c.BranchId, c.CreatedAt, null,
                        "Совпадение: данные состава или источников ИК", 1));
                }
            }
        }

        // ── Задачи ────────────────────────────────────────────────────────
        if (canTask)
        {
            var tasks = _db.WorkTasks.AsNoTracking()
                .Where(t => !t.IsDeleted);
            if (!scope.IsSystemAdmin)
                tasks = tasks.Where(t => t.BranchId == branchFilter || t.BranchId == null);
            else if (branchFilter is { } adminBranch)
                tasks = tasks.Where(t => t.BranchId == adminBranch);
            if (from is { } fromValue)
                tasks = tasks.Where(t => t.CreatedAtUtc >= fromValue);
            if (to is { } toValue)
                tasks = tasks.Where(t => t.CreatedAtUtc <= toValue);
            if (query.TaskStatus is { } taskStatus)
                tasks = tasks.Where(t => t.Status == taskStatus);
            if (query.TaskPriority is { } taskPriority)
                tasks = tasks.Where(t => t.Priority == taskPriority);
            if (query.BranchId is { } taskBranch && !scope.IsSystemAdmin)
                tasks = tasks.Where(t => false); // branch scope для неадмина применён выше

            var taskRows = await tasks
                .Where(t => !hasText ||
                            EF.Functions.ILike(t.Title, pattern, @"\") ||
                            EF.Functions.ILike(t.Description ?? "", pattern, @"\") ||
                            EF.Functions.ILike(t.EntityCodeSnapshot ?? "", pattern, @"\") ||
                            EF.Functions.ILike(t.EntityTitleSnapshot ?? "", pattern, @"\"))
                .OrderByDescending(t => t.CreatedAtUtc)
                .Take(ExtendedLimit)
                .Select(t => new
                {
                    t.Id, t.Title, t.Status, t.EntityCodeSnapshot, t.EntityTitleSnapshot,
                    t.BranchId, t.CreatedAtUtc,
                })
                .ToListAsync(ct);
            foreach (var task in taskRows)
            {
                candidates.Add(new CandidateRow(
                    "WorkTask", task.Id, task.Title,
                    task.EntityTitleSnapshot ?? task.EntityCodeSnapshot,
                    task.EntityCodeSnapshot, null, task.Status.ToString(),
                    task.BranchId, task.CreatedAtUtc, null,
                    "Совпадение: реквизиты задачи", 0));
            }
        }

        return await FinalizePageAsync(candidates, query, hasText, ct);
    }

    // ── Построители базовых запросов расширенного поиска ─────────────────

    private IQueryable<HKCard> HKCardsQuery(
        SearchQuery query,
        ActorScope scope,
        Guid? branchFilter,
        DateTime? from,
        DateTime? to,
        DateTime now)
    {
        var q = _db.HKCards.AsNoTracking()
            .Include(c => c.Node)
            .Include(c => c.Aggregate)
            .Include(c => c.EquipmentModel)
            .Include(c => c.Complex)
            .AsQueryable();

        if (!scope.IsSystemAdmin && scope.UserBranchId is { } userBranch)
            q = q.Where(c => c.BranchId == userBranch);
        else if (branchFilter is { } selected)
            q = q.Where(c => c.BranchId == selected);

        if (from is { } fromValue)
            q = q.Where(c => c.CreatedAt >= fromValue);
        if (to is { } toValue)
            q = q.Where(c => c.CreatedAt <= toValue);

        if (query.HKCardStatus is { } status)
            q = q.Where(c => c.Status == status);
        if (query.HKObjectLevel is { } level)
            q = q.Where(c => c.ObjectLevel == level);

        if (query.HasAttachment is { } hasAttachment)
            q = q.Where(c => hasAttachment ? c.Attachment != null : c.Attachment == null);
        if (query.HKValidity is { } validity)
        {
            var soonest = now.AddDays(90);
            switch (validity)
            {
                case HKValidityFilter.Active:
                    q = q.Where(c => c.Status == HKCardStatus.Approved &&
                                     (c.ExpirationDate == null || c.ExpirationDate >= now) &&
                                     (c.EffectiveDate == null || c.EffectiveDate <= now));
                    break;
                case HKValidityFilter.Expiring:
                    q = q.Where(c => c.ExpirationDate != null &&
                                     c.ExpirationDate >= now && c.ExpirationDate <= soonest);
                    break;
                case HKValidityFilter.Expired:
                    q = q.Where(c => c.ExpirationDate != null && c.ExpirationDate < now);
                    break;
            }
        }

        return q;
    }

    private IQueryable<IndividualCard> IndividualCardsQuery(
        SearchQuery query,
        ActorScope scope,
        Guid? branchFilter,
        DateTime? from,
        DateTime? to)
    {
        var q = _db.IndividualCards.AsNoTracking().AsQueryable();

        if (!scope.IsSystemAdmin && scope.UserBranchId is { } userBranch)
            q = q.Where(c => c.BranchId == userBranch);
        else if (branchFilter is { } selected)
            q = q.Where(c => c.BranchId == selected);

        if (from is { } fromValue)
            q = q.Where(c => c.CreatedAt >= fromValue);
        if (to is { } toValue)
            q = q.Where(c => c.CreatedAt <= toValue);

        if (query.IndividualCardStatus is { } cardStatus)
            q = q.Where(c => c.Status == cardStatus);
        if (query.IndividualCardObjectLevel is { } objectLevel)
            q = q.Where(c => c.ObjectLevel == objectLevel);
        if (query.IsFormed is { } isFormed)
            q = q.Where(c => isFormed ? c.FormedAt != null : c.FormedAt == null);
        if (query.HasCoefficients is { } hasCoefficients)
            q = q.Where(c => hasCoefficients
                ? c.CoefficientSnapshots.Any()
                : !c.CoefficientSnapshots.Any());

        return q;
    }

    private async Task<ActorScope> LoadActorScopeAsync(CancellationToken ct)
    {
        var userId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new UnauthorizedAccessException("Пользователь не найден.");
        var isSystemAdmin = await _userManager.IsInRoleAsync(actor, UserRole.SystemAdmin.ToString());
        Guid? branch = actor.BranchId is { } branchId && branchId != Guid.Empty ? branchId : null;
        if (!isSystemAdmin && branch is null)
            throw new UnauthorizedAccessException("У пользователя не указан филиал.");
        return new ActorScope(isSystemAdmin, branch, userId.ToString());
    }

    private static string DisplayName(string entityType) =>
        SearchDisplayCatalog.EntityTypeDisplay(entityType);

    private static string StatusDisplay(string entityType, string statusKey) => entityType switch
    {
        "HKCard" => SearchDisplayCatalog.HKStatus(statusKey),
        "IndividualCard" => SearchDisplayCatalog.IndividualCardStatus(statusKey),
        "WorkTask" => SearchDisplayCatalog.WorkTaskStatus(statusKey),
        _ => statusKey,
    };

    private static string MapNavigation(string entityType, Guid id) => entityType switch
    {
        "HKCard" => $"/хк/{id}",
        "IndividualCard" => $"/инд-карты/{id}",
        "Complex" => $"/состав-комплекса?complexId={id}",
        "EquipmentModel" => $"/состав-изделия?equipmentModelId={id}",
        "Aggregate" => $"/состав-агрегата?aggregateId={id}",
        "Node" => "/справочник-узлов",
        "AssemblyUnit" => "/справочник-сборочных-единиц",
        "EquipmentInstance" => "/справочник-экземпляров",
        "GsmMaterial" => "/справочник-гсм",
        "Coefficient" => "/справочник-коэффициентов",
        "WorkTask" => $"/задачи/{id}",
        _ => "/",
    };

    private async Task<SearchPageDto> FinalizePageAsync(
        List<CandidateRow> candidates,
        SearchQuery query,
        bool hasText,
        CancellationToken ct)
    {
        // Дедупликация EntityType + EntityId: остаётся более точное (прямое) совпадение.
        var dedup = candidates
            .GroupBy(c => (c.EntityType, c.EntityId))
            .Select(g => g
                .OrderBy(c => c.Rank)
                .ThenBy(c => c.EntityType, StringComparer.Ordinal)
                .First())
            .ToList();

        // ── Область связанных данных: справочник / ХК / ИК ────────────────
        if (query.RelatedScope is { } relatedScope && relatedScope != RelatedResultsScope.All)
        {
            dedup = dedup
                .Where(r => relatedScope switch
                {
                    RelatedResultsScope.ReferenceOnly => r.EntityType is not ("HKCard" or "IndividualCard"),
                    RelatedResultsScope.HKOnly => r.EntityType == "HKCard",
                    RelatedResultsScope.ICOnly => r.EntityType == "IndividualCard",
                    _ => true,
                })
                .ToList();
        }

        var sortBy = query.SortBy switch
        {
            "CreatedAt" => "CreatedAt",
            "ApprovedDate" => "ApprovedDate",
            "Title" => "Title",
            "EntityType" => "EntityType",
            _ => hasText ? "Relevance" : "CreatedAt",
        };

        // Детерминированный ранг релевантности (0..6) по текущему тексту.
        var ranked = dedup
            .Select(c => new
            {
                Row = c,
                Rank = hasText ? RefinedRank(c, query.Text) : c.Rank,
            })
            .ToList();

        var branchIds = ranked
            .Where(c => c.Row.BranchId != null)
            .Select(c => c.Row.BranchId!.Value)
            .Distinct()
            .ToList();
        var branchNames = branchIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Branches.AsNoTracking()
                .Where(b => branchIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        var items = ranked
            .Select(x => (
                x.Rank,
                Item: new SearchResultDto(
                    x.Row.EntityId,
                    x.Row.EntityType,
                    DisplayName(x.Row.EntityType),
                    x.Row.Title,
                    x.Row.Subtitle,
                    x.Row.Code,
                    x.Row.Version,
                    x.Row.StatusKey,
                    StatusDisplay(x.Row.EntityType, x.Row.StatusKey),
                    x.Row.BranchId,
                    x.Row.BranchId is { } branchId && branchNames.TryGetValue(branchId, out var branchName)
                        ? branchName : null,
                    x.Row.CreatedAt,
                    x.Row.ApprovedDate,
                    hasText ? x.Row.MatchContext : null,
                    MapNavigation(x.Row.EntityType, x.Row.EntityId),
                    true)))
            .ToList();

        items = sortBy switch
        {
            "EntityType" => query.SortDescending
                ? items.OrderByDescending(i => i.Item.EntityTypeDisplay, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(i => i.Item.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal).ToList()
                : items.OrderBy(i => i.Item.EntityTypeDisplay, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(i => i.Item.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal).ToList(),
            "Title" => query.SortDescending
                ? items.OrderByDescending(i => i.Item.Title, StringComparer.Ordinal)
                    .ThenBy(i => i.Item.EntityTypeDisplay, StringComparer.OrdinalIgnoreCase).ToList()
                : items.OrderBy(i => i.Item.Title, StringComparer.Ordinal)
                    .ThenBy(i => i.Item.EntityTypeDisplay, StringComparer.OrdinalIgnoreCase).ToList(),
            "ApprovedDate" => query.SortDescending
                ? items.OrderByDescending(i => i.Item.ApprovedDate ?? DateTime.MinValue)
                    .ThenByDescending(i => i.Item.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal).ToList()
                : items.OrderBy(i => i.Item.ApprovedDate ?? DateTime.MaxValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal).ToList(),
            "CreatedAt" => query.SortDescending
                ? items.OrderByDescending(i => i.Item.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal).ToList()
                : items.OrderBy(i => i.Item.CreatedAt ?? DateTime.MaxValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal).ToList(),
            _ => query.SortDescending
                ? items.OrderBy(i => i.Rank)
                    .ThenByDescending(i => i.Item.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal)
                    .ThenBy(i => i.Item.EntityTypeDisplay, StringComparer.OrdinalIgnoreCase).ToList()
                : items.OrderBy(i => i.Rank)
                    .ThenBy(i => i.Item.CreatedAt ?? DateTime.MaxValue)
                    .ThenBy(i => i.Item.Title, StringComparer.Ordinal)
                    .ThenBy(i => i.Item.EntityTypeDisplay, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        var totalCount = items.Count;
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 10, 100);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var currentPage = Math.Max(1, Math.Min(Math.Max(1, query.Page), totalPages == 0 ? 1 : totalPages));

        return new SearchPageDto(
            items.Select(i => i.Item).Skip((currentPage - 1) * pageSize).Take(pageSize).ToList(),
            totalCount,
            currentPage,
            pageSize,
            totalPages);
    }

    /// <summary>
    /// Детерминированный ранг совпадения (меньше — лучше):
    /// 0 — точное попадание в код/версию/инвентарный номер;
    /// 1 — точное название;
    /// 2 — начинается с запроса;
    /// 3 — слово в основном названии;
    /// 4 — совпадение в реквизитах/описании/примечаниях;
    /// 5 — прямое упоминание в ХК или ИК;
    /// 6 — связь через нормативную цепочку.
    /// </summary>
    private static int RefinedRank(CandidateRow c, string text)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        var code = c.Code ?? string.Empty;
        var version = c.Version ?? string.Empty;
        var title = c.Title ?? string.Empty;

        if ((!string.IsNullOrEmpty(code) && code.Equals(text, comparison)) ||
            (!string.IsNullOrEmpty(version) && version.Equals(text, comparison)))
            return 0;
        if (!string.IsNullOrEmpty(title) && title.Equals(text, comparison))
            return 1;
        if (code.StartsWith(text, comparison) || title.StartsWith(text, comparison))
            return 2;
        if (title.Contains(text, comparison))
            return 3;
        return c.Rank switch
        {
            0 => 4,
            1 => 5,
            _ => 6,
        };
    }
}
