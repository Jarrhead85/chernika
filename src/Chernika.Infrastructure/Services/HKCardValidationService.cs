using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public sealed class HKCardValidationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    public HKCardValidationService(AppDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<HKValidationResult> ValidateDraftAsync(HKCard card, CancellationToken ct = default)
    {
        var errors = new List<HKValidationError>();

        if (!Enum.IsDefined(typeof(HKObjectLevel), card.ObjectLevel))
        {
            errors.Add(new HKValidationError(
                nameof(card.ObjectLevel),
                "Неизвестный уровень объекта нормирования.",
                "invalid-object-level"));
            return HKValidationResult.Fail(errors);
        }

        var objectInvariant = ValidateExactlyOneObject(card);
        if (objectInvariant != null)
        {
            errors.Add(objectInvariant);
            return HKValidationResult.Fail(errors);
        }

        if (!await ObjectExistsAsync(card, ct))
            errors.Add(new HKValidationError(
                "ObjectId",
                $"{ObjectFieldLabel(card.ObjectLevel)} не найден или недоступен.",
                "object-not-found"));

        if (card.EffectiveDate.HasValue && card.ExpirationDate.HasValue
            && card.ExpirationDate.Value < card.EffectiveDate.Value)
            errors.Add(new HKValidationError(
                "ExpirationDate",
                "Дата окончания действия не может быть раньше даты начала действия.",
                "invalid-date-range"));

        errors.AddRange(ValidateTextFields(card));
        errors.AddRange(ValidateRequestRules(card));

        var now = _time.GetUtcNow().UtcDateTime;
        if (card.RequestReceivedDate.HasValue && card.RequestReceivedDate.Value > now.AddDays(1))
            errors.Add(new HKValidationError(
                "RequestReceivedDate",
                "Дата поступления не может быть позже текущей даты более чем на один день.",
                "request-date-in-future"));

        errors.AddRange(await ValidateItemReferencesAsync(card.Items, ct));
        errors.AddRange(ValidateMaterialsIntegrity(card.Items));

        return new HKValidationResult(errors.Count == 0, errors);
    }

    public async Task<HKValidationResult> ValidateForReviewAsync(HKCard card, CancellationToken ct = default)
    {
        var draft = await ValidateDraftAsync(card, ct);
        if (!draft.IsValid)
            return draft;

        var errors = new List<HKValidationError>();

        if (card.ObjectLevel == HKObjectLevel.Node)
        {
            errors.AddRange(await ValidateNodeRowsForReviewAsync(card, ct));
        }
        else
        {
            var now = _time.GetUtcNow().UtcDateTime;
            errors.AddRange(await ValidateAggregateLevelForReviewAsync(card, now, ct));
        }

        if (!string.IsNullOrEmpty(card.IncomingLetterNumber))
        {
            var hasAttachment = await _db.HKCardAttachments.AnyAsync(a => a.HKCardId == card.Id, ct);
            if (!hasAttachment)
                errors.Add(new HKValidationError(
                    "Attachment",
                    "PDF-скан исходной ХК: загрузите скан поступившего обращения.",
                    "attachment-required"));
        }

        return new HKValidationResult(errors.Count == 0, errors);
    }

    public async Task<HKValidationResult> ValidateForApprovalAsync(HKCard card, CancellationToken ct = default)
    {
        return await ValidateForReviewAsync(card, ct);
    }

    private async Task<List<HKValidationError>> ValidateNodeRowsForReviewAsync(HKCard card, CancellationToken ct)
    {
        var errors = new List<HKValidationError>();
        var rows = card.Items.OrderBy(i => i.SortOrder).ToList();

        if (rows.Count == 0)
            errors.Add(new HKValidationError(
                "Items",
                "Строки ХК: добавьте хотя бы одну строку.",
                "rows-required"));

        var auIds = rows.Where(i => i.AssemblyUnitId != Guid.Empty)
            .Select(i => i.AssemblyUnitId).Distinct().ToList();
        var activeAus = auIds.Count > 0
            ? (await _db.AssemblyUnits.AsNoTracking()
                .Where(a => auIds.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(ct)).ToHashSet()
            : new HashSet<Guid>();

        for (var idx = 0; idx < rows.Count; idx++)
        {
            var item = rows[idx];
            var prefix = $"Строка {idx + 1}";

            if (item.AssemblyUnitId == Guid.Empty)
                errors.Add(new HKValidationError(
                    $"Items[{item.Id}]",
                    $"{prefix}: выберите сборочную единицу.",
                    "assembly-unit-required"));
            else if (!activeAus.Contains(item.AssemblyUnitId))
                errors.Add(new HKValidationError(
                    $"Items[{item.Id}]",
                    $"{prefix}: сборочная единица не найдена или недоступна.",
                    "assembly-unit-not-found"));

            if (item.Quantity <= 0)
                errors.Add(new HKValidationError(
                    $"Items[{item.Id}]",
                    $"{prefix}: количество изделий должно быть больше нуля.",
                    "quantity-invalid"));

            if (item.Volume < 0)
                errors.Add(new HKValidationError(
                    $"Items[{item.Id}]",
                    $"{prefix}: масса/объём не может быть отрицательным.",
                    "volume-negative"));
        }

        return errors;
    }

    private async Task<List<HKValidationError>> ValidateAggregateLevelForReviewAsync(
        HKCard card, DateTime now, CancellationToken ct)
    {
        var errors = new List<HKValidationError>();
        var levelName = LevelName(card.ObjectLevel);

        if (card.Items.Count > 0)
            errors.Add(new HKValidationError(
                "Items",
                $"ХК {levelName}: строки сборочных единиц не заполняются вручную — состав формируется из утверждённых дочерних ХК.",
                "manual-items-not-allowed"));

        var components = await _db.HKCardComponents.AsNoTracking()
            .Where(c => c.ParentHKCardId == card.Id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

        if (components.Count == 0)
            errors.Add(new HKValidationError(
                "Components",
                $"ХК {levelName}: добавьте хотя бы одну утверждённую дочернюю ХК.",
                "components-required"));

        var duplicateChild = components
            .GroupBy(c => c.ChildHKCardId)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateChild != null)
            errors.Add(new HKValidationError(
                "Components",
                $"ХК {levelName}: дочерняя ХК включена в состав несколько раз.",
                "duplicate-component"));

        var childIds = components.Select(c => c.ChildHKCardId).Distinct().ToList();
        var children = childIds.Count > 0
            ? await _db.HKCards.AsNoTracking()
                .Where(h => childIds.Contains(h.Id))
                .Select(h => new ChildSnapshot(
                    h.Id, h.Code, h.Status, h.EffectiveDate, h.ExpirationDate,
                    h.ObjectLevel, h.ComplexId, h.EquipmentModelId, h.AggregateId, h.NodeId))
                .ToListAsync(ct)
            : new List<ChildSnapshot>();

        var childById = children.ToDictionary(c => c.Id);

        foreach (var comp in components)
        {
            if (!childById.TryGetValue(comp.ChildHKCardId, out var child))
            {
                errors.Add(new HKValidationError(
                    "Components",
                    $"Дочерняя ХК «{comp.ChildCode}» не найдена.",
                    "child-not-found"));
                continue;
            }

            if (child.Status != HKCardStatus.Approved)
                errors.Add(new HKValidationError(
                    "Components",
                    $"Дочерняя ХК «{child.Code}» не утверждена.",
                    "child-not-approved"));

            if (child.EffectiveDate.HasValue && child.EffectiveDate.Value > now)
                errors.Add(new HKValidationError(
                    "Components",
                    $"Дочерняя ХК «{child.Code}» ещё не вступила в силу.",
                    "child-not-effective"));

            if (child.ExpirationDate.HasValue && child.ExpirationDate.Value < now)
                errors.Add(new HKValidationError(
                    "Components",
                    $"Срок действия дочерней ХК «{child.Code}» истёк.",
                    "child-expired"));

            var levelOk = (card.ObjectLevel, child.ObjectLevel) switch
            {
                (HKObjectLevel.Complex, HKObjectLevel.EquipmentModel) => true,
                (HKObjectLevel.EquipmentModel, HKObjectLevel.Aggregate) => true,
                (HKObjectLevel.Aggregate, HKObjectLevel.Node) => true,
                _ => false
            };
            if (!levelOk)
                errors.Add(new HKValidationError(
                    "Components",
                    $"Дочерняя ХК «{child.Code}» имеет недопустимый уровень для состава ХК {levelName}.",
                    "child-level-mismatch"));

            if (!await CompositionLinkValidAsync(card, child, ct))
                errors.Add(new HKValidationError(
                    "Components",
                    $"Дочерняя ХК «{child.Code}» не входит в утверждённый действующий конструктивный состав.",
                    "child-not-in-composition"));
        }

        if (await HasCycleAsync(card.Id, childIds, ct))
            errors.Add(new HKValidationError(
                "Components",
                "Обнаружен циклический состав дочерних ХК.",
                "component-cycle"));

        return errors;
    }

    private async Task<List<HKValidationError>> ValidateItemReferencesAsync(
        ICollection<HKCardItem> items, CancellationToken ct)
    {
        var errors = new List<HKValidationError>();
        if (items == null || items.Count == 0)
            return errors;

        var auIds = items.Where(i => i.AssemblyUnitId != Guid.Empty)
            .Select(i => i.AssemblyUnitId).Distinct().ToList();
        if (auIds.Count > 0)
        {
            var existing = await _db.AssemblyUnits.AsNoTracking()
                .Where(a => auIds.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(ct);
            var existingSet = existing.ToHashSet();
            foreach (var id in auIds.Where(id => !existingSet.Contains(id)))
                errors.Add(new HKValidationError(
                    "AssemblyUnitId",
                    "Сборочная единица не найдена или недоступна.",
                    "assembly-unit-not-found"));
        }

        var allMaterials = items.SelectMany(i => i.Materials).ToList();
        foreach (var mat in allMaterials)
        {
            if (!Enum.IsDefined(typeof(GsmCategory), mat.Category))
            {
                errors.Add(new HKValidationError(
                    "GsmCategory",
                    "Некорректная категория марки ГСМ.",
                    "invalid-material-category"));
            }
        }

        var gsmIds = allMaterials.Where(m => m.GsmMaterialId != Guid.Empty)
            .Select(m => m.GsmMaterialId).Distinct().ToList();
        if (gsmIds.Count > 0)
        {
            var existing = await _db.GsmMaterials.AsNoTracking()
                .Where(m => gsmIds.Contains(m.Id))
                .Select(m => m.Id)
                .ToListAsync(ct);
            var existingSet = existing.ToHashSet();
            foreach (var id in gsmIds.Where(id => !existingSet.Contains(id)))
                errors.Add(new HKValidationError(
                    "GsmMaterialId",
                    "Марка ГСМ не найдена или недоступна.",
                    "material-not-found"));
        }

        return errors;
    }

    private static List<HKValidationError> ValidateMaterialsIntegrity(ICollection<HKCardItem> items)
    {
        var errors = new List<HKValidationError>();
        if (items == null || items.Count == 0)
            return errors;

        var rows = items.OrderBy(i => i.SortOrder).ToList();
        for (var idx = 0; idx < rows.Count; idx++)
        {
            var item = rows[idx];
            foreach (var grp in item.Materials.GroupBy(m => m.Category))
            {
                var duplicate = grp.GroupBy(m => m.GsmMaterialId).FirstOrDefault(g => g.Count() > 1);
                if (duplicate != null)
                {
                    errors.Add(new HKValidationError(
                        $"Items[{item.Id}]",
                        $"Строка {idx + 1}: марка ГСМ дублируется в категории «{CategoryName(grp.Key)}».",
                        "duplicate-material"));
                }
            }
        }

        return errors;
    }

    private static HKValidationError? ValidateExactlyOneObject(HKCard card)
    {
        var candidates = new (Guid? Id, HKObjectLevel Level)[]
        {
            (card.ComplexId, HKObjectLevel.Complex),
            (card.EquipmentModelId, HKObjectLevel.EquipmentModel),
            (card.AggregateId, HKObjectLevel.Aggregate),
            (card.NodeId, HKObjectLevel.Node)
        };
        var set = candidates
            .Where(c => c.Id.HasValue && c.Id.Value != Guid.Empty)
            .ToList();

        if (set.Count == 0)
            return new HKValidationError(
                "ObjectId",
                $"Необходимо выбрать объект нормирования ({ObjectFieldLabel(card.ObjectLevel)}).",
                "object-required");

        if (set.Count > 1)
            return new HKValidationError(
                "ObjectId",
                $"Указано несколько объектов нормирования. Для {ObjectFieldLabel(card.ObjectLevel)} должен быть указан ровно один объект.",
                "object-invariant");

        if (set[0].Level != card.ObjectLevel)
            return new HKValidationError(
                "ObjectId",
                $"{ObjectFieldLabel(card.ObjectLevel)}: выбран объект другого уровня ({LevelName(set[0].Level)}). Укажите объект уровня «{ObjectFieldLabel(card.ObjectLevel)}».",
                "object-level-mismatch");

        return null;
    }

    private async Task<bool> ObjectExistsAsync(HKCard card, CancellationToken ct)
    {
        return card.ObjectLevel switch
        {
            HKObjectLevel.Complex => await _db.Complexes.AnyAsync(c => c.Id == card.ComplexId, ct),
            HKObjectLevel.EquipmentModel => await _db.EquipmentModels.AnyAsync(m => m.Id == card.EquipmentModelId, ct),
            HKObjectLevel.Aggregate => await _db.Aggregates.AnyAsync(a => a.Id == card.AggregateId, ct),
            HKObjectLevel.Node => await _db.Nodes.AnyAsync(n => n.Id == card.NodeId, ct),
            _ => false
        };
    }

    private static List<HKValidationError> ValidateTextFields(HKCard card)
    {
        var errors = new List<HKValidationError>();
        var fields = new (string Name, string? Value, int Max)[]
        {
            ("Номер ХК", card.Code, 50),
            ("Версия", card.Version, 10),
            ("Назначение ХК", card.Purpose, 2000),
            ("Нормативная база", card.NormativeBasis, 2000),
            ("Примечание", card.Notes, 4000),
            ("Организация", card.RequestOrganization, 500),
            ("ФИО отправителя", card.RequestSenderFullName, 500),
            ("Реквизиты / основание", card.RequestDetails, 2000),
            ("Входящий номер письма", card.IncomingLetterNumber, 100),
            ("Исходящий номер письма", card.OutgoingLetterNumber, 100)
        };

        foreach (var (name, value, max) in fields)
        {
            if (value == null) continue;
            if (value.Length > max)
                errors.Add(new HKValidationError(
                    name,
                    $"Поле «{name}» не может быть длиннее {max} символов.",
                    "text-too-long"));
            if (value.Any(ch => ch < ' ' && ch != '\t' && ch != '\n' && ch != '\r'))
                errors.Add(new HKValidationError(
                    name,
                    $"Поле «{name}» содержит недопустимые символы.",
                    "text-control-symbol"));
        }

        card.Code = card.Code?.Trim() ?? "";
        card.Version = card.Version?.Trim() ?? "";
        card.Purpose = card.Purpose?.Trim();
        card.NormativeBasis = card.NormativeBasis?.Trim();
        card.Notes = card.Notes?.Trim();
        card.RequestOrganization = card.RequestOrganization?.Trim();
        card.RequestSenderFullName = card.RequestSenderFullName?.Trim();
        card.RequestDetails = card.RequestDetails?.Trim();
        card.IncomingLetterNumber = card.IncomingLetterNumber?.Trim();
        card.OutgoingLetterNumber = card.OutgoingLetterNumber?.Trim();

        return errors;
    }

    private static List<HKValidationError> ValidateRequestRules(HKCard card)
    {
        var errors = new List<HKValidationError>();

        if (!string.IsNullOrEmpty(card.IncomingLetterNumber))
        {
            if (string.IsNullOrEmpty(card.RequestOrganization))
                errors.Add(new HKValidationError(
                    "RequestOrganization",
                    "При указании входящего номера письма обязательна «Организация».",
                    "incoming-letter-requires-organization"));

            if (!card.RequestReceivedDate.HasValue)
                errors.Add(new HKValidationError(
                    "RequestReceivedDate",
                    "При указании входящего номера письма обязательна «Дата поступления».",
                    "incoming-letter-requires-date"));
        }

        var hasAnyRequestField = !string.IsNullOrEmpty(card.RequestOrganization)
            || !string.IsNullOrEmpty(card.RequestSenderFullName)
            || card.RequestReceivedDate.HasValue
            || !string.IsNullOrEmpty(card.IncomingLetterNumber)
            || !string.IsNullOrEmpty(card.OutgoingLetterNumber);

        if (hasAnyRequestField && string.IsNullOrEmpty(card.RequestDetails))
            errors.Add(new HKValidationError(
                "RequestDetails",
                "При заполнении реквизитов обращения обязательно поле «Реквизиты / основание».",
                "request-details-required"));

        return errors;
    }

    private async Task<bool> CompositionLinkValidAsync(HKCard parent, ChildSnapshot child, CancellationToken ct)
    {
        return (parent.ObjectLevel, child.ObjectLevel) switch
        {
            (HKObjectLevel.Aggregate, HKObjectLevel.Node) =>
                parent.AggregateId.HasValue && child.NodeId.HasValue &&
                await _db.AggregateCompositions
                    .Where(ac => ac.AggregateId == parent.AggregateId.Value
                        && ac.Status == ProductCompositionStatus.Approved && ac.IsActive)
                    .SelectMany(ac => ac.Nodes)
                    .AnyAsync(n => n.NodeId == child.NodeId.Value, ct),

            (HKObjectLevel.EquipmentModel, HKObjectLevel.Aggregate) =>
                parent.EquipmentModelId.HasValue && child.AggregateId.HasValue &&
                await _db.ProductCompositions
                    .Where(pc => pc.EquipmentModelId == parent.EquipmentModelId.Value
                        && pc.Status == ProductCompositionStatus.Approved && pc.IsActive)
                    .SelectMany(pc => pc.Parts)
                    .SelectMany(p => p.Aggregates)
                    .AnyAsync(a => a.AggregateId == child.AggregateId.Value, ct),

            (HKObjectLevel.Complex, HKObjectLevel.EquipmentModel) =>
                parent.ComplexId.HasValue && child.EquipmentModelId.HasValue &&
                await _db.ComplexCompositions
                    .Where(cc => cc.ComplexId == parent.ComplexId.Value
                        && cc.Status == ProductCompositionStatus.Approved && cc.IsActive)
                    .SelectMany(cc => cc.Items)
                    .AnyAsync(i => i.EquipmentModelId == child.EquipmentModelId.Value, ct),

            _ => false
        };
    }

    private async Task<bool> HasCycleAsync(Guid parentCardId, IReadOnlyList<Guid> childIds, CancellationToken ct)
    {
        if (childIds.Count == 0)
            return false;

        var edges = await _db.HKCardComponents.AsNoTracking()
            .Select(c => new { c.ParentHKCardId, c.ChildHKCardId })
            .ToListAsync(ct);

        var parentsOf = edges
            .GroupBy(e => e.ChildHKCardId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ParentHKCardId).ToList());

        foreach (var leaf in childIds)
        {
            var visited = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(leaf);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == parentCardId)
                    return true;
                if (!visited.Add(current))
                    continue;
                if (parentsOf.TryGetValue(current, out var parents))
                {
                    foreach (var p in parents)
                        queue.Enqueue(p);
                }
            }
        }

        return false;
    }

    private static string ObjectFieldLabel(HKObjectLevel level) => level switch
    {
        HKObjectLevel.Complex => "Комплекс",
        HKObjectLevel.EquipmentModel => "Изделие",
        HKObjectLevel.Aggregate => "Агрегат",
        HKObjectLevel.Node => "Узел",
        _ => "Объект"
    };

    private static string LevelName(HKObjectLevel level) => level switch
    {
        HKObjectLevel.Complex => "комплекса",
        HKObjectLevel.EquipmentModel => "изделия",
        HKObjectLevel.Aggregate => "агрегата",
        HKObjectLevel.Node => "узла",
        _ => "объекта"
    };

    private static string CategoryName(GsmCategory category) => category switch
    {
        GsmCategory.Primary => "Основные",
        GsmCategory.Duplicate => "Дублирующие",
        GsmCategory.Reserve => "Резервные",
        GsmCategory.Foreign => "Зарубежные",
        _ => category.ToString()
    };

    private sealed record ChildSnapshot(
        Guid Id,
        string Code,
        HKCardStatus Status,
        DateTime? EffectiveDate,
        DateTime? ExpirationDate,
        HKObjectLevel ObjectLevel,
        Guid? ComplexId,
        Guid? EquipmentModelId,
        Guid? AggregateId,
        Guid? NodeId);
}
