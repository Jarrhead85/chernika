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
    int SortOrder,
    Guid? SectionId);

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
    Guid? SectionId,
    IReadOnlyList<HKAcceptedMaterialDto> Materials);

/// <summary>
/// Итоги принятой редакции.
/// <para>
/// Считаются ТОЛЬКО из снимковых Quantity, Volume, UnitOfMeasure и
/// принадлежности разделам. Актуальные рабочие количества и справочные
/// значения для принятой структуры не используются.
/// </para>
/// </summary>
public sealed record HKAcceptedTotalsDto(
    IReadOnlyList<HKAcceptedSectionTotalsDto> Sections,
    IReadOnlyList<Domain.Structure.HKMeasuredTotal> WithoutSection,
    IReadOnlyList<Domain.Structure.HKMeasuredTotal> Overall);

/// <summary>Итог раздела принятой редакции.</summary>
public sealed record HKAcceptedSectionTotalsDto(
    Guid SectionId,
    string Name,
    IReadOnlyList<Domain.Structure.HKMeasuredTotal> Totals);

/// <summary>Раздел принятой редакции, прочитанный из снимка.</summary>
public sealed record HKAcceptedSectionDto(
    Guid SectionId,
    HKCardSectionType SectionType,
    string Name,
    int SortOrder);

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
    IReadOnlyList<HKAcceptedItemDto> Items,
    /// <summary>
    /// Разделы принятой редакции.
    /// <para>
    /// Читаются из СНИМКА, а не из рабочих HKCardSection: принятая карта
    /// не должна меняться вместе с рабочей.
    /// </para>
    /// </summary>
    IReadOnlyList<HKAcceptedSectionDto> Sections,

    DateTime? AcceptedAtUtc,
    string? AcceptedByUserId);

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
public sealed class HKCardRevisionService : IHKAcceptedCompositionReader
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
    /// <summary>
    /// Филиал, карты которого доступны пользователю.
    /// <para>
    /// Порядок тот же, что и в реестре ХК: право HK.View проверяется всегда,
    /// а карты чужой организации недоступны даже при наличии прав на
    /// управление системой.
    /// </para>
    /// </summary>
    private async Task<Guid?> GetAccessibleBranchIdAsync(
        Guid? requestedBranchId, CancellationToken ct)
    {
        var actorId = _currentUser.GetRequiredUserId();

        await _permissions.DemandPermissionAsync(PermissionCodes.HKView, ct);

        if (await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.SystemConfig))
            return requestedBranchId;

        var actor = await _userManager.FindByIdAsync(actorId.ToString())
            ?? throw new UnauthorizedAccessException("Пользователь не найден.");

        if (actor.BranchId is null || actor.BranchId == Guid.Empty)
            throw new UnauthorizedAccessException("У пользователя не указана организация.");

        if (requestedBranchId.HasValue && requestedBranchId != actor.BranchId)
            throw new UnauthorizedAccessException("Нет доступа к данным другой организации.");

        return actor.BranchId;
    }

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
    /// <summary>
    /// Фиксация рабочей структуры как новой редакции.
    /// <para>
    /// Внутренний механизм будущей команды окончательного согласования.
    /// Право <c>HK.View</c> НЕ является правом создавать редакции, поэтому
    /// единственной проверки чтения недостаточно: публичная команда выдала бы
    /// фиксацию любому читателю. Фиксация не вызывается из HTTP и не имеет
    /// endpoint.
    /// </para>
    /// <para>
    /// Вызывающий — только <c>HKApprovalService</c>, уже проверивший право,
    /// филиал, назначение согласующим, состояние этапа и ожидаемую версию.
    /// Контекст операции не принимается из запроса и не может быть подделан DTO.
    /// </para>
    /// </summary>
    /// <summary>
    /// Переводит рабочий раздел в его снимок.
    /// <para>
    /// Раздел, не попавший в снимок, означал бы потерянную принадлежность
    /// ветки: молча превращать её в «без раздела» нельзя.
    /// </para>
    /// </summary>
    private static Guid? ResolveSnapshotSectionId(
        Guid? sectionId, Dictionary<Guid, Guid> map)
    {
        if (sectionId is null)
            return null;

        if (!map.TryGetValue(sectionId.Value, out var snapshotId))
            throw new InvalidOperationException(
                "Раздел ветки не найден в исходном наборе. Фиксация отклонена: " +
                "потеря принадлежности раздела недопустима.");

        return snapshotId;
    }

    internal async Task<Guid> CaptureAsync(
        Guid cardId,
        uint expectedCardRowVersion,
        DateTime? acceptedAtUtc = null,
        string? acceptedByUserId = null,
        CancellationToken ct = default)
    {
        // Карта должна существовать и быть доступна: это защита от фиксации
        // несуществующей карты и от вызова в обход workflow.
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

            // Данные решения задаются ПРИ СОЗДАНИИ. Редакция неизменяема с момента
            // фиксации, поэтому дописывать их после неё нельзя: перехватчик
            // неизменяемости отклонит изменение. Фиксация без решения (внутренние
            // сценарии и проверки) оставляет их пустыми.
            AcceptedAtUtc = acceptedAtUtc,
            AcceptedByUserId = acceptedByUserId,
        };

        // Разделы фиксируются в снимке вместе со структурой: принятая карта
        // показывает ту же группировку, что была согласована. Читать раздел
        // через живую ссылку рабочей карты нельзя.
        var sections = await _db.HKCardSections
            .AsNoTracking()
            .Where(s => s.HKCardId == cardId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct);

        var sectionMap = new Dictionary<Guid, Guid>();
        foreach (var section in sections)
        {
            var snapshot = new HKCardRevisionSection
            {
                Id = Guid.NewGuid(),
                RevisionId = revision.Id,
                SectionType = section.SectionType,
                SortOrder = section.SortOrder
            };

            sectionMap[section.Id] = snapshot.Id;
            revision.Sections.Add(snapshot);
        }


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

                // Раздел верхней позиции фиксируется в снимке. Вложенная
                // позиция наследует раздел корня и значения не получает.
                SectionId = node.ParentId is null
                    ? ResolveSnapshotSectionId(node.SectionId, sectionMap)
                    : null,
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
                SectionId = ResolveSnapshotSectionId(item.SectionId, sectionMap),
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

            // Откат транзакции не очищает ChangeTracker: снимковые записи,
            // оставшиеся в состоянии Added, дописались бы при любом
            // последующем SaveChanges того же scope. Снимок целиком снимается
            // с отслеживания, как будто он не собирался.
            DetachRevisionSnapshot();
            throw;
        }

        if (plan.Owned)
            await tx!.CommitAsync(ct);

        return revision.Id;
    }

    /// <summary>
    /// Снимает добавленный снимок с отслеживания: редакция с вложенными
    /// разделами, позициями, строками и материалами не должна остаться в
    /// ChangeTracker после отката регистрации.
    /// <para>
    /// Записи берутся списком и отсоединяются через уже полученные
    /// <c>EntityEntry</c>: вызов <c>DbContext.Entry(entity)</c> на каждом шаге
    /// запускает DetectChanges, и на середине отсоединения отслеженная
    /// редакция со ссылкой на уже отсоединённые записи считалась бы связью с
    /// разорванной обязательной связью — и отказ маскировался бы чужой ошибкой.
    /// </para>
    /// </summary>
    private void DetachRevisionSnapshot()
    {
        // Порядок отсоединения — от листьев к корню. Пока родитель отслежен,
        // снимать с отслеживания его детей допустимо; наоборот (родитель
        // раньше детей) отслеженные дети остались бы с обязательной ссылкой на
        // уже отсоединённого родителя, и EF отклонил бы операцию ошибкой о
        // разорванной связи.
        var snapshotEntries = _db.ChangeTracker.Entries()
            .Where(e => e.Entity is HKCardRevision or HKCardRevisionSection
                or HKCardRevisionNode or HKCardRevisionItem
                or HKCardRevisionItemMaterial)
            .OrderBy(e => e.Entity switch
            {
                HKCardRevisionItemMaterial => 0,
                HKCardRevisionItem => 1,
                HKCardRevisionNode => 2,
                HKCardRevisionSection => 3,
                _ => 4,
            })
            .ToList();

        foreach (var entry in snapshotEntries)
        {
            entry.State = EntityState.Detached;
        }
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
    /// Итоги принятой редакции, посчитанные из снимка.
    /// <para>
    /// Расчётный механизм тот же, что и для рабочего дерева: формулы не
    /// дублируются, различаются только входные данные.
    /// </para>
    /// </summary>
    public async Task<HKAcceptedTotalsDto?> GetAcceptedTotalsAsync(
        Guid cardId, CancellationToken ct = default)
    {
        await EnsureReadAccessAsync(cardId, ct);

        var pointer = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => c.CurrentAcceptedRevisionId)
            .FirstOrDefaultAsync(ct);

        if (pointer is null)
            return null;

        var sections = await _db.HKCardRevisionSections
            .AsNoTracking()
            .Where(s => s.RevisionId == pointer.Value)
            .OrderBy(s => s.SortOrder)
            .Select(s => new
            {
                s.Id,
                s.SectionType,
                Name = Domain.Structure.HKSectionNames.NameOf(s.SectionType),
                s.SortOrder
            })
            .ToListAsync(ct);

        var nodes = (await _db.HKCardRevisionNodes
            .AsNoTracking()
            .Where(n => n.RevisionId == pointer.Value)
            .OrderBy(n => n.SortOrder)
            .Select(n => new
            {
                n.Id,
                n.ParentId,
                n.Level,
                n.Name,
                n.Quantity,
                n.SortOrder,
                // EF не переводит шаблон is null в SQL: верхность проверяется
                // прямым сравнением, а вложенные позиции наследуют раздел корня.
                TopSectionId = n.ParentId == null ? n.SectionId : (Guid?)null
            })
            .ToListAsync(ct))
            .Select(n => new Domain.Structure.HKStructureNodeInput(
                n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder, n.TopSectionId))
            .ToList();

        var items = (await _db.HKCardRevisionItems
            .AsNoTracking()
            .Where(i => i.RevisionId == pointer.Value)
            .OrderBy(i => i.SortOrder)
            .Select(i => new
            {
                i.Id,
                i.NodeId,
                i.Quantity,
                i.Volume,
                i.UnitOfMeasure,
                i.SortOrder,
                // Принадлежность хранится только у строки под корнем.
                TopSectionId = i.NodeId == null ? i.SectionId : (Guid?)null
            })
            .ToListAsync(ct))
            .Select(i => new Domain.Structure.HKStructureItemInput(
                i.Id, i.NodeId, i.Quantity, i.Volume, i.UnitOfMeasure, i.SortOrder,
                i.TopSectionId))
            .ToList();

        var input = new Domain.Structure.HKStructureInput(
            sections.Select(s => new Domain.Structure.HKStructureSectionInput(
                s.Id, s.SectionType, s.SortOrder)).ToList(),
            nodes, items);

        // Один и тот же механизм, что и для рабочего дерева: формулы не
        // дублируются.        // Один и тот же механизм, что и для рабочего дерева: формулы не
        // дублируются.
        var totals = Domain.Structure.HKStructureTotals.Calculate(input);

        return new HKAcceptedTotalsDto(
            totals.Sections.Select(s => new HKAcceptedSectionTotalsDto(
                s.SectionId,
                sections.Single(x => x.Id == s.SectionId).Name,
                s.Totals)).ToList(),
            totals.WithoutSection!.Totals,
            totals.Overall);
    }

    /// <summary>
    /// Принятая редакция карты целиком: дерево, итоги и реквизиты снимка.
    /// <para>
    /// Указатель принятой редакции читается ОДИН раз, и дерево, и итоги
    /// строятся из одного и того же снимка: два независимых чтения указателя
    /// дали бы смесь разных редакций.
    /// </para>
    /// <para>
    /// Отсутствие принятой редакции возвращается ЯВНО (null): подстановка
    /// рабочего дерева была бы незаметной подменой черновика принятой картой.
    /// </para>
    /// </summary>
    public async Task<HKAcceptedCompositionDto?> GetAcceptedCompositionAsync(
        Guid cardId, CancellationToken ct = default)
    {
        await EnsureReadAccessAsync(cardId, ct);

        var pointer = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => c.CurrentAcceptedRevisionId)
            .FirstOrDefaultAsync(ct);

        if (pointer is null)
            return null;

        var revision = await ReadAsync(cardId, pointer.Value, ct);
        if (revision is null)
            return null;

        return HKAcceptedCompositionProjection.Build(cardId, revision);
    }

    /// <summary>
    /// Реестр карт с принятыми редакциями.
    /// <para>
    /// Номер, название, версия и дата принятия берутся из СНИМКА принятой
    /// редакции, на который указывает указатель карты. Рабочая карта нужна
    /// для доступа, филиала, идентичности карты и самого указателя, но не
    /// как источник нормативных метаданных: показанные и найденные значения
    /// должны совпадать.
    /// </para>
    /// <para>
    /// Итоги страницы считаются ОДНИМ запросом по снимкам строк и позиций
    /// всех карт страницы: запрос на каждую строку реестра дал бы N+1.
    /// </para>
    /// </summary>
    public async Task<PagedResult<HKAcceptedCompositionListItemDto>> GetAcceptedCompositionsAsync(
        string? search = null,
        Guid? branchId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var safeBranchId = await GetAccessibleBranchIdAsync(branchId, ct);

        // Снимок принятой редакции присоединяется к карте ЛЕВЫМ соединением:
        // отсутствующий снимок должен быть виден как нарушение целостности, а
        // не исчезать из реестра молча. Проекция только скалярная: материализация
        // сущностей в соединении открывает вторую операцию над контекстом, и
        // чтение реестра падает из-за гонки с обновлением панели.
        var cardsQuery = _db.HKCards
            .AsNoTracking()
            .Where(c => c.CurrentAcceptedRevisionId != null);

        if (safeBranchId.HasValue)
            cardsQuery = cardsQuery.Where(c => c.BranchId == safeBranchId.Value);

        var query =
            from card in cardsQuery
            join revision in _db.HKCardRevisions.AsNoTracking()
                on card.CurrentAcceptedRevisionId equals (Guid?)revision.Id into revisions
            from revision in revisions.DefaultIfEmpty()
            select new
            {
                card.Id,
                card.CurrentAcceptedRevisionId,
                card.ObjectLevel,
                card.IsAutonomous,
                card.NodeRootCardLevel,
                RevisionId = revision != null ? revision.Id : (Guid?)null,
                RevisionNumber = revision != null ? revision.RevisionNumber : (int?)null,
                RevisionCode = revision != null ? revision.Code : null,
                RevisionName = revision != null ? revision.Name : null,
                RevisionVersion = revision != null ? revision.Version : null,
                AcceptedAtUtc = revision != null ? revision.AcceptedAtUtc : (DateTime?)null
            };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();

            // Поиск идёт по тем же снимковым значениям, которые показывает
            // реестр: расхождение с показанным списком вводит в заблуждение.
            query = query.Where(c =>
                EF.Functions.ILike(c.RevisionCode ?? string.Empty, "%" + term + "%")
                || EF.Functions.ILike(c.RevisionName ?? string.Empty, "%" + term + "%"));
        }

        var total = await query.CountAsync(ct);

        var rows = await query
            // Дата принятия — снимковая. Карты без зафиксированной даты идут
            // в конце: даты принятия у них нет, и ставить их первыми нельзя.
            .OrderBy(c => c.AcceptedAtUtc != null ? 0 : 1)
            .ThenByDescending(c => c.AcceptedAtUtc)
            // Вторичный ключ — идентификатор карты: порядок устойчив.
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var cards = rows
            .Select(r => new
            {
                r.Id,
                r.CurrentAcceptedRevisionId,
                r.ObjectLevel,
                r.IsAutonomous,
                r.NodeRootCardLevel
            })
            .ToList();

        var revisionIds = cards
            .Select(c => c.CurrentAcceptedRevisionId!.Value)
            .ToList();

        var nodes = revisionIds.Count == 0
            ? new List<HKCardRevisionNodeRow>()
            : await _db.HKCardRevisionNodes
                .AsNoTracking()
                .Where(n => revisionIds.Contains(n.RevisionId))
                .Select(n => new HKCardRevisionNodeRow(
                    n.RevisionId, n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder, n.SectionId))
                .ToListAsync(ct);

        var items = revisionIds.Count == 0
            ? new List<HKCardRevisionItemRow>()
            : await _db.HKCardRevisionItems
                .AsNoTracking()
                .Where(i => revisionIds.Contains(i.RevisionId))
                .Select(i => new HKCardRevisionItemRow(
                    i.RevisionId, i.Id, i.NodeId, i.Quantity, i.Volume, i.UnitOfMeasure, i.SortOrder, i.SectionId))
                .ToListAsync(ct);

        var nodesByRevision = nodes.GroupBy(n => n.RevisionId).ToDictionary(g => g.Key, g => g.ToList());
        var itemsByRevision = items.GroupBy(i => i.RevisionId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<HKAcceptedCompositionListItemDto>();

        for (var i = 0; i < rows.Count; i++)
        {
            var card = cards[i];
            var row = rows[i];

            if (row.RevisionId is null)
            {
                // Указатель принятой редакции ссылается на отсутствующий
                // снимок: это нарушение целостности, а не «редакция № 0» с
                // рабочими данными и достоверными нулевыми итогами.
                throw new InvalidOperationException(
                    "Указатель принятой редакции карты ссылается на отсутствующий снимок.");
            }

            var revisionId = card.CurrentAcceptedRevisionId!.Value;

            // Снимок может оказаться пустым: отсутствующие узлы и строки — это
            // пустые списки, а не отсутствие данных.
            var cardNodes = nodesByRevision.TryGetValue(revisionId, out var foundNodes)
                ? foundNodes
                : new List<HKCardRevisionNodeRow>();
            var cardItems = itemsByRevision.TryGetValue(revisionId, out var foundItems)
                ? foundItems
                : new List<HKCardRevisionItemRow>();

            // Расчёт тот же, что и в подробном просмотре: формулы не дублируются.
            var input = new Domain.Structure.HKStructureInput(
                Array.Empty<Domain.Structure.HKStructureSectionInput>(),
                cardNodes
                    .Select(n => new Domain.Structure.HKStructureNodeInput(
                        n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder, n.SectionId))
                    .ToList(),
                cardItems
                    .Select(i => new Domain.Structure.HKStructureItemInput(
                        i.Id, i.NodeId, i.Quantity, i.Volume, i.UnitOfMeasure, i.SortOrder, i.SectionId))
                    .ToList());

            var totals = Domain.Structure.HKStructureTotals.Calculate(input);

            // Уровень карты: у карты со справочным объектом — уровень объекта,
            // у автономной — вывод по принятой структуре.
            var cardLevel = card.IsAutonomous
                ? HKAcceptedCompositionProjection.CardLevel(
                    cardNodes.Select(n => new HKAcceptedNodeDto(
                        n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder, n.SectionId)).ToList(),
                    cardItems.Select(i => new HKAcceptedItemDto(
                        i.Id, i.NodeId, Guid.Empty, null, null,
                        i.Quantity, i.Volume, i.UnitOfMeasure, null, null, i.SortOrder, i.SectionId,
                        Array.Empty<HKAcceptedMaterialDto>())).ToList(),
                    card.NodeRootCardLevel)
                : card.ObjectLevel;

            result.Add(new HKAcceptedCompositionListItemDto(
                card.Id,
                row.RevisionCode,
                row.RevisionName,
                row.RevisionVersion,
                row.RevisionNumber ?? 0,
                // Дата принятия из снимка: если она не зафиксирована, реестр
                // показывает «не зафиксирована», а не дату создания карты.
                row.AcceptedAtUtc,
                totals.Overall,
                input.Nodes.Count,
                input.Items.Count,
                cardLevel,
                card.IsAutonomous));
        }

        return new PagedResult<HKAcceptedCompositionListItemDto>
        {
            Items = result,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>Строка снимка позиции для расчёта итогов реестра.</summary>
    private sealed record HKCardRevisionNodeRow(
        Guid RevisionId, Guid Id, Guid? ParentId, Domain.Enums.HKObjectLevel Level,
        string Name, int Quantity, int SortOrder, Guid? SectionId);

    /// <summary>Строка снимка конечной строки для расчёта итогов реестра.</summary>
    private sealed record HKCardRevisionItemRow(
        Guid RevisionId, Guid Id, Guid? NodeId, int Quantity, decimal Volume,
        string? UnitOfMeasure, int SortOrder, Guid? SectionId);

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

        var sections = await _db.HKCardRevisionSections
            .AsNoTracking()
            .Where(s => s.RevisionId == revisionId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct);

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
                .Select(n => new HKAcceptedNodeDto(
                    n.Id, n.ParentId, n.Level, n.Name, n.Quantity, n.SortOrder, n.SectionId))
                .ToList(),
            items.Select(i => new HKAcceptedItemDto(
                i.Id, i.NodeId, i.AssemblyUnitId,
                i.AssemblyUnitCode, i.AssemblyUnitName,
                i.Quantity, i.Volume, i.UnitOfMeasure, i.Periodicity, i.Notes, i.SortOrder,
                i.SectionId,
                materials.Where(m => m.RevisionItemId == i.Id)
                    .Select(m => new HKAcceptedMaterialDto(
                        m.SourceGsmMaterialId, m.Category, m.Name, m.Nd))
                    .ToList()))
            .ToList(),
            sections.Select(s => new HKAcceptedSectionDto(
                    s.Id, s.SectionType,
                    Domain.Structure.HKSectionNames.NameOf(s.SectionType), s.SortOrder))
                .ToList(),
            // Дата решения отделена от CapturedAtUtc: фиксация — технический
            // момент снятия снимка, решение — момент принятия.
            r.AcceptedAtUtc,
            r.AcceptedByUserId);
    }
}