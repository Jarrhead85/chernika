using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.AspNetCore.Identity;
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
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPermissionService _permissions;
    private readonly TimeProvider _time;

    public HKCardRevisionService(
        AppDbContext db,
        ICurrentUserService currentUser,
        UserManager<ApplicationUser> userManager,
        IPermissionService permissions,
        TimeProvider time)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
        _permissions = permissions;
        _time = time;
    }

    // ── Доступ к карте ──

    /// <summary>
    /// Единая проверка доступа к карте для чтения редакций.
    /// <para>
    /// Проверяется АУТЕНТИФИЦИРОВАННЫЙ и АКТИВНЫЙ пользователь, действующее
    /// право <c>HK.View</c> и принадлежность филиалу карты.
    /// </para>
    /// <para>
    /// Проверка принадлежности редакции карте НЕ заменяет проверку полномочий:
    /// она отвечает на вопрос «та ли это карта», а не «можно ли её читать».
    /// Поэтому порядок важен: сначала полномочия, потом принадлежность.
    /// </para>
    /// <para>
    /// Отсутствие доступной карты даёт понятную ошибку. Наличие редакции чужой
    /// карты НЕ раскрывается: пользователю без доступа возвращается тот же
    /// отказ, что и при отсутствии редакции.
    /// </para>
    /// </summary>
    private async Task<PersistedCardState> EnsureReadAccessAsync(
        Guid cardId, CancellationToken ct)
    {
        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _userManager.FindByIdAsync(actorId.ToString())
            ?? throw new UnauthorizedAccessException("Пользователь не найден.");

        if (!actor.IsActive)
            throw new UnauthorizedAccessException("Пользователь отключён.");

        // Проверка выполняется по фактическому идентификатору пользователя, а не
        // по его имени: полномочия и принадлежность филиалу принадлежат записи.

        // Индивидуальный запрет действует и здесь: он может быть наложен поверх
        // роли, и наличие роли с правом просмотра запрет не отменяет.
        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKView, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для просмотра ХК.");

        var card = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => new PersistedCardState(
                c.Id, c.Status, c.BranchId, c.AuthorId, c.IsAutonomous,
                c.Code, c.Version, c.RowVersion))
            .FirstOrDefaultAsync(ct)
            ?? throw new ArgumentException("ХК не найдена.");

        if (actor.BranchId != card.BranchId && !await _permissions.IsSystemAdminAsync(actor.Id))
            throw new UnauthorizedAccessException("Нет доступа к карточке другой организации.");

        return card;
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
        // Фиксация — внутренний механизм будущей команды согласования. Она не
        // принимает контекст доступа из запроса и не открывает endpoint: вызывающий
        // сценарий обязан проверить доступ сам.
        await EnsureReadAccessAsync(cardId, ct);

        await using var plan = await ResolveTransactionAsync(ct);
        var tx = plan.Transaction;

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

        // Снимок фиксирует данные УЖЕ СОХРАНЁННЫХ ссылок. Поэтому мягко удалённая
        // справочная запись читается осознанно, через IgnoreQueryFilters в
        // ограниченной точке: выбрать удалённую запись в НОВУЮ строку по-прежнему
        // нельзя — фильтр там остаётся. Отсутствие записи, напротив, — отказ.
        var assemblyIds = items.Select(i => i.AssemblyUnitId).Distinct().ToList();
        var assemblies = (await _db.AssemblyUnits
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(a => assemblyIds.Contains(a.Id))
                .ToListAsync(ct))
            .ToDictionary(a => a.Id);

        // Обязательный источник не придумывается: если сборочной единицы в
        // базе нет, фиксация отклоняется, а не делается с пустым наименованием.
        var missingAssembly = assemblyIds.FirstOrDefault(id => !assemblies.ContainsKey(id));
        if (missingAssembly != Guid.Empty)
            throw new InvalidOperationException(
                "Сборочная единица конечной строки отсутствует в справочнике. " +
                "Фиксация отклонена: снимок не должен хранить данные без источника.");

        var materials = await _db.HKCardItemMaterials
            .AsNoTracking()
            .Where(m => items.Select(i => i.Id).Contains(m.HKCardItemId))
            .ToListAsync(ct);

        var materialIds = materials.Select(m => m.GsmMaterialId).Distinct().ToList();
        var gsm = (await _db.GsmMaterials
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(g => materialIds.Contains(g.Id))
                .ToListAsync(ct))
            .ToDictionary(g => g.Id);

        var missingGsm = materialIds.FirstOrDefault(id => !gsm.ContainsKey(id));
        if (missingGsm != Guid.Empty)
            throw new InvalidOperationException(
                "Марка ГСМ конечной строки отсутствует в справочнике. " +
                "Фиксация отклонена: название и НД не выдумываются.");

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

            if (!nodeIdMap.TryGetValue(node.ParentId.Value, out var parentSnapshotId))
                // Позиция объявлена потомком, но её родитель не попал в
                // исходный набор. Молча делать её корневой нельзя: это
                // изменило бы сохранённую структуру.
                throw new InvalidOperationException(
                    "Родительская позиция дерева не найдена в исходном наборе. " +
                    "Фиксация отклонена: превращать позицию в корневую нельзя.");

            snapshot.ParentId = parentSnapshotId;
        }

        foreach (var item in items)
        {
            var snapshot = new HKCardRevisionItem
            {
                Id = Guid.NewGuid(),
                RevisionId = revision.Id,
                SourceItemId = item.Id,
                NodeId = ResolveSnapshotNodeId(item, nodeIdMap),
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

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Собственную транзакцию сервис откатывает сам: иначе вызывающий
            // случайно зафиксировал бы частичную редакцию.
            if (plan.Owned)
                await tx!.RollbackAsync(ct);
            throw;
        }

        if (plan.Owned)
            await tx!.CommitAsync(ct);

        return revision.Id;
    }

    /// <summary>
    /// Снимковая позиция строки редакции.
    /// <para>
    /// Если строке задана рабочая позиция, но её нет в исходном наборе, строка не
    /// становится строкой под корнем: фиксация отклоняется. Подмена положения
    /// строки молча изменила бы сохранённую структуру.
    /// </para>
    /// </summary>
    private static Guid? ResolveSnapshotNodeId(
        HKCardItem item, IReadOnlyDictionary<Guid, Guid> nodeIdMap)
    {
        if (item.HKCardNodeId is null)
            return null;

        if (!nodeIdMap.TryGetValue(item.HKCardNodeId.Value, out var mapped))
            throw new InvalidOperationException(
                "Позиция дерева, указанная у конечной строки, отсутствует в исходном наборе. " +
                "Фиксация отклонена: превращать строку в строку под корнем нельзя.");

        return mapped;
    }

    /// <summary>
    /// Решает, кто владеет транзакцией фиксации, и какой уровень изоляции
    /// допускается.
    /// <para>
    /// Согласованность набора данных редакции требует, что чтения карты, позиций,
    /// строк и материалов видели ОДНО состояние базы. При READ COMMITTED каждый
    /// отдельный запрос видит свой момент времени, и конкурентная запись рабочей
    /// структуры попала бы внутрь редакции частично.
    /// </para>
    /// <para>
    /// Допустимые уровни для внешней транзакции — REPEATABLE READ и SERIALIZABLE:
    /// оба дают согласованный снимок. Всё остальное отклоняется ДО записи.
    /// </para>
    /// <para>
    /// Сервис не фиксирует и не откатывает чужую транзакцию: владение
    /// однозначно принадлежит вызывающему. При ошибке сервис откатывает
    /// собственную транзакцию и никогда не оставляет частичную редакцию.
    /// </para>
    /// </summary>
    private sealed class TransactionPlan : IAsyncDisposable
    {
        public TransactionPlan(IDbContextTransaction? transaction, bool owned)
        {
            Transaction = transaction;
            Owned = owned;
        }

        public IDbContextTransaction? Transaction { get; }
        public bool Owned { get; }
        public ValueTask DisposeAsync() => Transaction?.DisposeAsync() ?? default;
    }

    /// <summary>Уровни, дающие согласованный снимок набора данных фиксации.</summary>
    private static readonly HashSet<System.Data.IsolationLevel> AllowedIsolationLevels =
        [System.Data.IsolationLevel.RepeatableRead, System.Data.IsolationLevel.Serializable];

    private async Task<TransactionPlan> ResolveTransactionAsync(CancellationToken ct)
    {
        var existing = _db.Database.CurrentTransaction;

        if (existing is not null)
        {
            if (!AllowedIsolationLevels.Contains(existing.GetDbTransaction().IsolationLevel))
                throw new InvalidOperationException(
                    "Фиксация требует уровня изоляции RepeatableRead или Serializable. " +
                    "Переданная транзакция не обеспечивает согласованного снимка.");

            // Чужую транзакцию сервис не фиксирует и не откатывает.
            return new TransactionPlan(null, false);
        }

        var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct);
        return new TransactionPlan(tx, true);
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
        // Проверка доступа выполняется ДО чтения указателя: по одному известному
        // идентификатору карты нельзя выдать чужую редакцию.
        await EnsureReadAccessAsync(cardId, ct);

        var pointer = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => c.CurrentAcceptedRevisionId)
            .FirstOrDefaultAsync(ct);

        // Доступная карта без принятой редакции — ЯВНОЕ отсутствие принятой
        // структуры. Подстановка рабочего дерева была бы незаметной подменой
        // черновика принятой картой.
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
        await EnsureReadAccessAsync(cardId, ct);

        // Принадлежность редакции проверяется вместе с существованием и НЕ
        // раскрывается отдельно: пользователь без доступа к карте не должен
        // узнавать, что такая редакция существует.
        var exists = await _db.HKCardRevisions
            .AsNoTracking()
            .AnyAsync(r => r.Id == revisionId && r.HKCardId == cardId, ct);

        if (!exists)
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