using Chernika.Domain.Enums;
using Chernika.Domain.Entities;
using Chernika.Domain.Leveling;
using Chernika.Domain.Structure;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

/// <summary>Объект, зарегистрированный в библиотеке.</summary>
public sealed record HKLibraryRegistrationItem(
    HKObjectLevel Level,
    string Name,
    string Code,
    Guid ObjectId,
    bool Created);

/// <summary>Итог регистрации объектов принятой структуры в библиотеке.</summary>
public sealed record HKLibraryRegistrationResult(
    IReadOnlyList<HKLibraryRegistrationItem> Items)
{
    /// <summary>Сколько объектов создано (остальные уже были в библиотеке).</summary>
    public int CreatedCount => Items.Count(i => i.Created);

    /// <summary>Созданные объекты.</summary>
    public IReadOnlyList<HKLibraryRegistrationItem> Created => Items.Where(i => i.Created).ToList();

    /// <summary>Человекочитаемый итог для журнала и уведомления.</summary>
    public string Summary => Items.Count == 0
        ? string.Empty
        : CreatedCount == 0
            ? "Объекты уже были в библиотеке."
            : $"В библиотеку добавлено: {string.Join(", ", Created.Select(c => $"{HKLevelTitles.NameOf(c.Level)} «{c.Name}»"))}.";
}

/// <summary>
/// Регистрация объектов принятой структуры в библиотеке.
/// <para>
/// При окончательном принятии состава объекты из строк ХК и сама карта
/// попадают в классификаторы библиотеки (изделия, комплексы, агрегаты, узлы):
/// принятая карта описывает реальные объекты, и они должны быть доступны для
/// ссылок из других карт.
/// </para>
/// <para>
/// Регистрация идемпотентна: объект, уже существующий в библиотеке с тем же
/// уровнем и наименованием, не дублируется.
/// </para>
/// </summary>
public sealed class HKLibraryRegistrationService
{
    private readonly AppDbContext _db;

    public HKLibraryRegistrationService(AppDbContext db) => _db = db;

    /// <summary>Позиция принятой структуры в снимке редакции.</summary>
    private sealed record RevisionNode(
        Guid Id, Guid? ParentId, HKObjectLevel Level, string Name);

    /// <summary>
    /// Регистрирует объекты принятой структуры и саму карту в библиотеке.
    /// </summary>
    public async Task<HKLibraryRegistrationResult> RegisterAcceptedAsync(
        Guid cardId, Guid revisionId, CancellationToken ct = default)
    {
        var card = await _db.HKCards
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cardId, ct)
            ?? throw new ArgumentException("ХК не найдена.", nameof(cardId));

        var nodes = await _db.HKCardRevisionNodes
            .AsNoTracking()
            .Where(n => n.RevisionId == revisionId)
            .OrderBy(n => n.SortOrder)
            .Select(n => new RevisionNode(n.Id, n.ParentId, n.Level, n.Name))
            .ToListAsync(ct);

        var items = new List<HKLibraryRegistrationItem>();

        // 1. Объекты строк принятой структуры: у каждой позиции свой уровень.
        foreach (var node in nodes)
        {
            if (!IsLibraryLevel(node.Level))
                continue;

            var name = node.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            items.Add(await EnsureObjectAsync(node.Level, name, preferredCode: null, ct));
        }

        // 2. Сама карта — объект своего уровня. Уровень карты выводится из
        // принятой структуры: верхний агрегат даёт ХК изделия.
        var cardName = card.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(cardName))
        {
            var cardLevel = ResolveCardLevel(card, nodes);
            if (cardLevel is { } level && IsLibraryLevel(level))
                items.Add(await EnsureObjectAsync(level, cardName, card.Code, ct));
        }

        return new HKLibraryRegistrationResult(items);
    }

    /// <summary>
    /// Уровень карты по принятой структуре: то же доменное правило, что и при
    /// сохранении структуры.
    /// </summary>
    private static HKObjectLevel? ResolveCardLevel(
        HKCard card,
        IReadOnlyList<RevisionNode> nodes)
    {
        var positions = nodes
            .Select(n => new HKTreePosition(n.Id, n.ParentId, n.Level))
            .ToList();

        var shape = new HKTreeShape(positions, 0);
        var resolved = HKCardLevelRule.Resolve(shape, card.NodeRootCardLevel);

        return resolved.Status == HKCardLevelStatus.Determined ? resolved.CardLevel : null;
    }

    /// <summary>Уровень, объекты которого заведены в библиотеке.</summary>
    private static bool IsLibraryLevel(HKObjectLevel level) =>
        level is HKObjectLevel.EquipmentModel
            or HKObjectLevel.Complex
            or HKObjectLevel.Aggregate
            or HKObjectLevel.Node;

    /// <summary>
    /// Находит объект в библиотеке по уровню и наименованию либо создаёт его.
    /// </summary>
    private async Task<HKLibraryRegistrationItem> EnsureObjectAsync(
        HKObjectLevel level, string name, string? preferredCode, CancellationToken ct)
    {
        var existing = await FindAsync(level, name, ct);
        if (existing is not null)
            return new HKLibraryRegistrationItem(level, name, existing.Value.Code, existing.Value.Id, false);

        var code = await BuildCodeAsync(level, name, preferredCode, ct);

        switch (level)
        {
            case HKObjectLevel.EquipmentModel:
            {
                var model = new EquipmentModel
                {
                    Id = Guid.NewGuid(),
                    Index = code,
                    Name = name
                };
                _db.EquipmentModels.Add(model);
                return new HKLibraryRegistrationItem(level, name, code, model.Id, true);
            }
            case HKObjectLevel.Complex:
            {
                var complex = new Complex
                {
                    Id = Guid.NewGuid(),
                    Code = code,
                    Name = name
                };
                _db.Complexes.Add(complex);
                return new HKLibraryRegistrationItem(level, name, code, complex.Id, true);
            }
            case HKObjectLevel.Aggregate:
            {
                var aggregate = new Aggregate
                {
                    Id = Guid.NewGuid(),
                    Code = code,
                    Name = name
                };
                _db.Aggregates.Add(aggregate);
                return new HKLibraryRegistrationItem(level, name, code, aggregate.Id, true);
            }
            default:
            {
                var node = new Node
                {
                    Id = Guid.NewGuid(),
                    Code = code,
                    Name = name
                };
                _db.Nodes.Add(node);
                return new HKLibraryRegistrationItem(level, name, code, node.Id, true);
            }
        }
    }

    /// <summary>Ищет объект библиотеки по наименованию на заданном уровне.</summary>
    private async Task<(Guid Id, string Code)?> FindAsync(
        HKObjectLevel level, string name, CancellationToken ct)
    {
        switch (level)
        {
            case HKObjectLevel.EquipmentModel:
            {
                var model = await _db.EquipmentModels
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Name == name, ct);
                return model is null ? null : (model.Id, model.Index);
            }
            case HKObjectLevel.Complex:
            {
                var complex = await _db.Complexes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Name == name, ct);
                return complex is null ? null : (complex.Id, complex.Code);
            }
            case HKObjectLevel.Aggregate:
            {
                var aggregate = await _db.Aggregates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Name == name, ct);
                return aggregate is null ? null : (aggregate.Id, aggregate.Code);
            }
            default:
            {
                var node = await _db.Nodes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(n => n.Name == name, ct);
                return node is null ? null : (node.Id, node.Code);
            }
        }
    }

    /// <summary>
    /// Код объекта: у карты — её собственный, у позиции строки — производный
    /// от наименования, потому что обозначения у строки нет. Код уникален в
    /// пределах вида объектов.
    /// </summary>
    private async Task<string> BuildCodeAsync(
        HKObjectLevel level, string name, string? preferredCode, CancellationToken ct)
    {
        var baseCode = Truncate(
            string.IsNullOrWhiteSpace(preferredCode) ? name : preferredCode,
            level == HKObjectLevel.EquipmentModel ? 100 : 50);

        if (!await CodeTakenAsync(level, baseCode, ct))
            return baseCode;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Truncate($"{baseCode} ({i})", level == HKObjectLevel.EquipmentModel ? 100 : 50);
            if (!await CodeTakenAsync(level, candidate, ct))
                return candidate;
        }

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var room = Math.Max(1, 50 - suffix.Length - 1);
        return $"{Truncate(baseCode, room)}-{suffix}";
    }

    private async Task<bool> CodeTakenAsync(
        HKObjectLevel level, string code, CancellationToken ct)
    {
        switch (level)
        {
            case HKObjectLevel.EquipmentModel:
                return await _db.EquipmentModels.AnyAsync(m => m.Index == code, ct);
            case HKObjectLevel.Complex:
                return await _db.Complexes.AnyAsync(c => c.Code == code, ct);
            case HKObjectLevel.Aggregate:
                return await _db.Aggregates.AnyAsync(a => a.Code == code, ct);
            default:
                return await _db.Nodes.AnyAsync(n => n.Code == code, ct);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
