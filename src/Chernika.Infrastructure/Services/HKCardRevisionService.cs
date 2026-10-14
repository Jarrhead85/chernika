using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Chernika.Infrastructure.Services;

/// <summary>Снимок позиции принятой структуры для чтения.</summary>
public sealed record HKAcceptedNodeDto(
    Guid Id,
    Guid? ParentId,
    HKObjectLevel Level,
    string Name,
    int Quantity,
    int SortOrder);

/// <summary>Снимок материала принятой строки для чтения.</summary>
public sealed record HKAcceptedMaterialDto(
    Guid SourceGsmMaterialId,
    GsmCategory Category,
    string? Name,
    string? Nd);

/// <summary>Снимок конечной строки принятой структуры для чтения.</summary>
public sealed record HKAcceptedItemDto(
    Guid Id,
    Guid? NodeId,
    Guid AssemblyUnitId,
    string? AssemblyUnitCode,
    string? AssemblyUnitName,
    int Quantity,
    decimal Volume,
    string? UnitOfMeasure,
    string? Periodicity,
    string? Notes,
    int SortOrder,
    IReadOnlyList<HKAcceptedMaterialDto> Materials);

/// <summary>
/// Редакция карты, прочитанная для показа.
/// <para>
/// Наружу не отдаются EF-сущности с изменяемыми коллекциями и не отдаётся
/// ключ хранения файла: путь файловой системы — внутренняя деталь.
/// </para>
/// </summary>
public sealed record HKAcceptedStructureDto(
    Guid RevisionId,
    int RevisionNumber,
    DateTime CapturedAtUtc,
    string? Name,
    string? Code,
    string? Version,
    HKObjectLevel ObjectLevel,
    string? Purpose,
    string? NormativeBasis,
    string? Notes,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    bool HasAttachment,
    IReadOnlyList<HKAcceptedNodeDto> Nodes,
    IReadOnlyList<HKAcceptedItemDto> Items);

/// <summary>
/// Хранилище редакций собственной структуры карты.
/// <para>
/// Служба намеренно узкая. Она НЕ выставляет <c>Composition.Approved</c>, не
/// выставляет статус карты, не закрывает задачу рассмотрения, не шлёт
/// уведомление «ХК принята» и не переключает указатель принятой редакции.
/// Связать решение, снимок, указатель, задачи и аудит атомарно — задача
/// следующего PR; здесь подготовлен ровно тот внутренний механизм, который
/// для этого понадобится.
/// </para>
/// </summary>
public class HKCardRevisionService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _time;

    public HKCardRevisionService(
        AppDbContext db, ICurrentUserService currentUser, TimeProvider time)
    {
        _db = db;
        _currentUser = currentUser;
        _time = time;
    }

    // ── Фиксация ──

    /// <summary>
    /// Фиксирует рабочую структуру карты как новую редакцию.
    /// <para>
    /// Источник данных — СОХРАНЁННОЕ состояние, а не граф, присланный вызывающим:
    /// форма могла быть не сохранена или изменена после чтения.
    /// </para>
    /// <para>
    /// Согласованность набора данных обеспечивается транзакцией с уровнем
    /// изоляции REPEATABLE READ. Обычная последовательность SELECT при
    /// READ COMMITTED не образует согласованный снимок: конкурентная запись
    /// рабочей структуры попала бы внутрь редакции.
    /// </para>
    /// <para>
    /// Если вызывающий уже открыл транзакцию, новая не открывается: владение
    /// транзакцией должно быть однозначным.
    /// </para>
    /// <para>
    /// Возвращает идентификатор созданной редакции. Указатель принятой
    /// редакции НЕ переключается и статус этапа НЕ меняется.
    /// </para>
    /// </summary>
    public async Task<Guid> CaptureAsync(Guid cardId, uint expectedCardRowVersion,
        CancellationToken ct = default)
    {
        await using var owned = await BeginOwnedTransactionAsync(ct);
        var tx = owned?.Transaction;

        var card = await _db.HKCards
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cardId, ct)
            ?? throw new ArgumentException("ХК не найдена.");

        if (card.RowVersion != expectedCardRowVersion)
            throw new InvalidOperationException(
                "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");

        var attachment = await _db.HKCardAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.HKCardId == cardId, ct);

        var nodes = await _db.HKCardNodes
            .AsNoTracking()
            .Where(n => n.HKCardId == cardId)
            .OrderBy(n => n.SortOrder)
            .ToListAsync(ct);

        var items = await _db.HKCardItems
            .AsNoTracking()
            .Where(i => i.HKCardId == cardId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync(ct);

        var assemblies = await _db.AssemblyUnits
            .AsNoTracking()
            .Where(a => items.Select(i => i.AssemblyUnitId).Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var materials = await _db.HKCardItemMaterials
            .AsNoTracking()
            .Where(m => items.Select(i => i.Id).Contains(m.HKCardItemId))
            .ToListAsync(ct);

        var materialIds = materials.Select(m => m.GsmMaterialId).Distinct().ToList();
        var gsm = await _db.GsmMaterials
            .AsNoTracking()
            .Where(g => materialIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var actorId = _currentUser.GetRequiredUserId().ToString();

        var lastNumber = await _db.HKCardRevisions
            .AsNoTracking()
            .Where(r => r.HKCardId == cardId)
            .MaxAsync(r => (int?)r.RevisionNumber, ct) ?? 0;

        var revision = new HKCardRevision
        {
            Id = Guid.NewGuid(),
            HKCardId = cardId,
            RevisionNumber = lastNumber + 1,
            CapturedAtUtc = _time.GetUtcNow().UtcDateTime,
            CapturedByUserId = actorId,
            SourceCardRowVersion = card.RowVersion,

            Name = card.Name,
            Code = card.Code,
            Version = card.Version,
            ObjectLevel = card.ObjectLevel,
            Purpose = card.Purpose,
            NormativeBasis = card.NormativeBasis,
            Notes = card.Notes,
            RequestOrganization = card.RequestOrganization,
            RequestSenderFullName = card.RequestSenderFullName,
            RequestReceivedDate = card.RequestReceivedDate,
            RequestDetails = card.RequestDetails,
            IncomingLetterNumber = card.IncomingLetterNumber,
            OutgoingLetterNumber = card.OutgoingLetterNumber,
            EffectiveDate = card.EffectiveDate,
            ExpirationDate = card.ExpirationDate,

            AttachmentId = attachment?.Id,
            AttachmentOriginalFileName = attachment?.OriginalFileName,
            AttachmentStorageKey = attachment?.StorageKey,
            AttachmentContentType = attachment?.ContentType,
            AttachmentSizeBytes = attachment?.SizeBytes,
            AttachmentSha256 = attachment?.Sha256,
            AttachmentUploadedAtUtc = attachment?.UploadedAt,
        };

        // Только Добавление: редакция неизменяема с момента фиксации.
        var nodeIdMap = new Dictionary<Guid, Guid>();
        foreach (var node in nodes)
        {
            var snapshot = new HKCardRevisionNode
            {
                Id = Guid.NewGuid(),
                RevisionId = revision.Id,
                SourceNodeId = node.Id,
                Level = node.Level,
                Name = node.Name,
                Quantity = node.Quantity,
                SortOrder = node.SortOrder,
            };
            nodeIdMap[node.Id] = snapshot.Id;
            revision.Nodes.Add(snapshot);
        }

        // Родитель проставляется вторым проходом: порядок обхода не гарантирует,
        // что родитель встретится раньше потомка.
        foreach (var node in nodes)
        {
            if (node.ParentId is null)
                continue;
            var snapshot = revision.Nodes.Single(n => n.Id == nodeIdMap[node.Id]);
            if (nodeIdMap.TryGetValue(node.ParentId.Value, out var parentSnapshotId))
                snapshot.ParentId = parentSnapshotId;
        }

        foreach (var item in items)
        {
            var snapshot = new HKCardRevisionItem
            {
                Id = Guid.NewGuid(),
                RevisionId = revision.Id,
                SourceItemId = item.Id,
                NodeId = item.HKCardNodeId is { } src && nodeIdMap.TryGetValue(src, out var mapped)
                    ? mapped
                    : null,
                AssemblyUnitId = item.AssemblyUnitId,
                AssemblyUnitCode = assemblies.TryGetValue(item.AssemblyUnitId, out var au) ? au.Code : null,
                AssemblyUnitName = assemblies.TryGetValue(item.AssemblyUnitId, out var au2) ? au2.Name : null,
                Quantity = item.Quantity,
                Volume = item.Volume,
                UnitOfMeasure = item.UnitOfMeasure,
                Periodicity = item.Periodicity,
                Notes = item.Notes,
                SortOrder = item.SortOrder,
            };

            foreach (var material in materials.Where(m => m.HKCardItemId == item.Id))
            {
                gsm.TryGetValue(material.GsmMaterialId, out var source);
                snapshot.Materials.Add(new HKCardRevisionItemMaterial
                {
                    Id = Guid.NewGuid(),
                    RevisionItemId = snapshot.Id,
                    SourceGsmMaterialId = material.GsmMaterialId,
                    Category = material.Category,
                    Name = source?.Name,
                    Nd = source?.Nd,
                });
            }

            revision.Items.Add(snapshot);
        }

        _db.HKCardRevisions.Add(revision);
        await _db.SaveChangesAsync(ct);

        if (tx is not null)
            await tx.CommitAsync(ct);

        return revision.Id;
    }

    /// <summary>
    /// Открывает транзакцию только если её ещё нет: владение транзакцией
    /// должно быть однозначным, а вложенная несовместимая транзакция
    /// сломала бы фиксацию.
    /// </summary>
    private async Task<OwnedTransaction?> BeginOwnedTransactionAsync(CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null)
            return null;

        var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct);
        return new OwnedTransaction(tx);
    }

    private sealed class OwnedTransaction : IAsyncDisposable
    {
        public OwnedTransaction(IDbContextTransaction transaction) => Transaction = transaction;
        public IDbContextTransaction Transaction { get; }
        public ValueTask DisposeAsync() => Transaction.DisposeAsync();
    }

    // ── Чтение ──

    /// <summary>
    /// Последняя ПРИНЯТАЯ структура карты.
    /// <para>
    /// Источник истины — указатель <see cref="HKCard.CurrentAcceptedRevisionId"/>.
    /// Выбор по максимальной дате, максимальному номеру или последней созданной
    /// записи дал бы иную карту при любом расхождении истории.
    /// </para>
    /// <para>
    /// Отсутствие принятой структуры возвращается ЯВНО. Подстановка рабочего
    /// дерева была бы незаметной подменой: пользователь счёл бы рабочий
    /// черновик принятым.
    /// </para>
    /// </summary>
    public async Task<HKAcceptedStructureDto?> GetAcceptedAsync(Guid cardId, CancellationToken ct = default)
    {
        var pointer = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => c.CurrentAcceptedRevisionId)
            .FirstOrDefaultAsync(ct);

        if (pointer is null)
            return null;

        return await ReadAsync(cardId, pointer.Value, ct);
    }

    /// <summary>
    /// Конкретная сохранённая редакция в пределах доступа к карте.
    /// <para>
    /// Проверяется принадлежность редакции той же карте: по одному известному
    /// идентификатору чужая редакция не выдаётся.
    /// </para>
    /// </summary>
    public async Task<HKAcceptedStructureDto?> GetRevisionAsync(
        Guid cardId, Guid revisionId, CancellationToken ct = default)
    {
        var belongs = await _db.HKCardRevisions
            .AsNoTracking()
            .AnyAsync(r => r.Id == revisionId && r.HKCardId == cardId, ct);

        if (!belongs)
            throw new ArgumentException("Редакция не найдена.");

        return await ReadAsync(cardId, revisionId, ct);
    }

    private async Task<HKAcceptedStructureDto?> ReadAsync(
        Guid cardId, Guid revisionId, CancellationToken ct)
    {
        var r = await _db.HKCardRevisions
            .AsNoTracking()
            .Include(x => x.Nodes)
            .FirstOrDefaultAsync(x => x.Id == revisionId && x.HKCardId == cardId, ct);

        if (r is null)
            return null;

        var items = await _db.HKCardRevisionItems
            .AsNoTracking()
            .Where(i => i.RevisionId == revisionId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync(ct);

        var materials = await _db.HKCardRevisionItemMaterials
            .AsNoTracking()
            .Where(m => items.Select(i => i.Id).Contains(m.RevisionItemId))
            .ToListAsync(ct);

        return new HKAcceptedStructureDto(
            r.Id, r.RevisionNumber, r.CapturedAtUtc,
            r.Name, r.Code, r.Version, r.ObjectLevel,
            r.Purpose, r.NormativeBasis, r.Notes,
            r.EffectiveDate, r.ExpirationDate,
            r.AttachmentStorageKey is not null,
            r.Nodes
                .OrderBy(n => n.SortOrder)
                .Select(n => new HKAcceptedNodeDto(n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder))
                .ToList(),
            items.Select(i => new HKAcceptedItemDto(
                i.Id, i.NodeId, i.AssemblyUnitId,
                i.AssemblyUnitCode, i.AssemblyUnitName,
                i.Quantity, i.Volume, i.UnitOfMeasure, i.Periodicity, i.Notes, i.SortOrder,
                materials.Where(m => m.RevisionItemId == i.Id)
                    .Select(m => new HKAcceptedMaterialDto(
                        m.SourceGsmMaterialId, m.Category, m.Name, m.Nd))
                    .ToList()))
            .ToList());
    }
}