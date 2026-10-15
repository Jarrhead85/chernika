using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Рабочее состояние части карты: что именно сейчас разрешено изменять.
/// <para>
/// Три способности проверяются раздельно, потому что блокируются по разным
/// причинам и в разные моменты. Общий статус карты для этого не годится: во
/// время рассмотрения реквизитов общий статус карты один и тот же, а автору
/// структуру заполнять можно, реквизиты — нельзя.
/// </para>
/// <para>
/// Вычисление НЕ является разрешением на действие: каждая команда дополнительно
/// проверяет право и принадлежность филиалу.
/// </para>
/// </summary>
public sealed record HKCardEditingCapabilities(
    bool CanEditDetails,
    bool CanEditComposition,
    bool CanEditPdf);

/// <summary>
/// Решения по этапам согласования химмотологической карты.
/// <para>
/// Отдельный сервис, а не методы в <see cref="HKCardService"/>: у согласования
/// своя модель данных, свои права и своя транзакция. Смешивание с правкой
/// карты в одном методе раньше и позволяло обходить блокировки — например,
/// передать изменённые строки вместе с разрешёнными реквизитами.
/// </para>
/// </summary>
public class HKApprovalService
{
    private readonly AppDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly ICurrentUserService _currentUser;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly HKCardValidationService _validation;
    private readonly AuditService _audit;
    private readonly HKCardRevisionService _revisions;
    private readonly NotificationService _notifications;
    private readonly IFileStorageService _fileStorage;
    private readonly TaskService _tasks;
    private readonly TimeProvider _time;
    private readonly ILogger<HKApprovalService> _logger;

    public HKApprovalService(
        AppDbContext db,
        IPermissionService permissions,
        ICurrentUserService currentUser,
        UserManager<ApplicationUser> userManager,
        HKCardValidationService validation,
        AuditService audit,
        TaskService tasks,
        HKCardRevisionService revisions,
        NotificationService notifications,
        IFileStorageService fileStorage,
        TimeProvider time,
        ILogger<HKApprovalService> logger)
    {
        _db = db;
        _permissions = permissions;
        _currentUser = currentUser;
        _userManager = userManager;
        _validation = validation;
        _audit = audit;
        _tasks = tasks;
        _revisions = revisions;
        _notifications = notifications;
        _fileStorage = fileStorage;
        _time = time;
        _logger = logger;
    }

    public const string DetailsStageTitle = "Согласование реквизитов и основания ХК";
    public const string CompositionStageTitle = "Согласование состава и норм ХК";

    // ── Чтение состояния ──

    /// <summary>
    /// Состояния этапов карты для чтения. НИЧЕГО не создаёт и не сохраняет.
    /// <para>
    /// Чтение — побочный эффект не имеет: иначе обычный просмотр карты создавал
    /// бы записи в БД, и GET-запрос менял бы состояние системы. Отсутствие
    /// этапов — допустимое состояние, оно возвращается как есть.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PersistedStageState>> GetStagesAsync(
        Guid cardId, CancellationToken ct = default)
    {
        var access = await BuildReadAccessAsync(cardId, ct);
        return await ReadStagesAsync(access.Card.Id, ct);
    }

    /// <summary>
    /// Возможности правки карты для текущего пользователя.
    /// <para>
    /// Считается по сохранённым состояниям этапов. Если этапов ещё нет, карта
    /// нового процесса ещё не инициализирована: она считается полностью
    /// редактируемой, иначе первая же правка была бы невозможна.
    /// </para>
    /// </summary>
    public async Task<HKCardEditingCapabilities> GetEditingCapabilitiesAsync(
        Guid cardId, CancellationToken ct = default)
    {
        var access = await BuildReadAccessAsync(cardId, ct);
        var stages = await ReadStagesAsync(access.Card.Id, ct);

        var hasWork = await _permissions.HasPermissionAsync(
            access.ActorUserId.ToString(), PermissionCodes.HKWork, ct);

        // Отсутствие этапов разбирается наравне с их наличием: запреты
        // статуса (архивная, удалённая) действуют независимо от того,
        // созданы этапы или нет.
        return DeriveEditingCapabilities(stages, access.Card.Status, hasWork);
    }

    /// <summary>
    /// Отбор сохранённых состояний этапов. Неизменяемая проекция: вызывающий
    /// код получает снимок, а не отслеживаемые объекты, которые можно менять.
    /// </summary>
    private async Task<List<PersistedStageState>> ReadStagesAsync(Guid cardId, CancellationToken ct) =>
        await _db.HKCardApprovals
            .AsNoTracking()
            .Where(a => a.HKCardId == cardId)
            .Select(a => new PersistedStageState(
                a.Id, a.Stage, a.State, a.AssignedApproverUserId, a.ApprovedAtUtc, a.RowVersion))
            .ToListAsync(ct);

    /// <summary>
    /// Создаёт недостающие этапы ВНУТРИ команды.
    /// <para>
    /// Записи добавляются через <see cref="DbSet{HKCardApproval}.Add"/>:
    /// добавление в локальный список отслеживаемых объектов контекст не
    /// регистрирует, и этапы молча не сохранялись.
    /// </para>
    /// <para>
    /// Защита от параллельного создания — уникальный индекс
    /// <c>UX_HKCardApprovals_Card_Stage</c>. Победившая транзакция вставляет
    /// пару, проигравшая ловит нарушение и перечитывает уже созданные этапы.
    /// Инициализация идёт ПОСЛЕ проверок доступа и В той же транзакции, что и
    /// команда, поэтому фиктивных записей вне команды не появляется.
    /// </para>
    /// <para>
    /// Даты принятия не выдумываются: у нового этапа их нет.
    /// </para>
    /// </summary>
    /// <summary>
    /// Создаёт недостающие этапы карты внутри транзакции вызывающей команды.
    /// <para>
    /// Вставка идёт одним атомарным <c>INSERT ... ON CONFLICT DO NOTHING</c> по
    /// уникальному индексу пары «карта + этап» (UX_HKCardApprovals_Card_Stage).
    /// Поэтому:
    /// </para>
    /// <list type="bullet">
    /// <item>параллельная инициализация не создаёт дубликаты;</item>
    /// <item>ловить «любое нарушение уникальности» не требуется — такой разбор
    /// относился бы и к посторонним ограничениям;</item>
    /// <item>откат не подменяется <c>ChangeTracker.Clear()</c>: это сброс
    /// отслеживания в памяти, а не откат транзакции;</item>
    /// <item>состояние после вставки перечитывается, и команда работает только с
    /// реально сохранёнными строками, а не с отсоединёнными объектами.</item>
    /// </list>
    /// <para>Вызывается только из команды и только после проверок доступа.</para>
    /// </summary>
    private async Task<List<HKCardApproval>> EnsureStagesForCommandAsync(
        Guid cardId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var stage in new[] { HKApprovalStage.Details, HKApprovalStage.Composition })
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "HKCardApprovals"
                    ("Id", "HKCardId", "Stage", "State", "UpdatedAtUtc")
                VALUES ({Guid.NewGuid()}, {cardId}, {(int)stage}, {(int)HKApprovalStageState.Draft}, {now})
                ON CONFLICT ("HKCardId", "Stage") DO NOTHING
                """, ct);
        }

        return await _db.HKCardApprovals.Where(a => a.HKCardId == cardId).ToListAsync(ct);
    }

    /// <summary>
    /// Вычисляет общий статус карты по состояниям этапов.
    /// <para>
    /// Общий статус — отображение рабочего жизненного цикла, а не источник
    /// блокировок. При рассмотрении любого этапа карта «на проверке», при
    /// возврате любого этапа — на доработке, иначе — черновик: автор готовит
    /// следующий этап.
    /// </para>
    /// <para>
    /// Архивное и удалённое состояния не вычисляются и не возвращаются в
    /// активные: они самостоятельны по смыслу, и их потеря была бы способом
    /// незаметно вернуть документ в работу.
    /// </para>
    /// </summary>
    public static HKCardStatus DeriveOverallStatus(IReadOnlyList<HKCardApproval> stages)
    {
        if (stages.Any(s => s.State == HKApprovalStageState.OnReview))
            return HKCardStatus.OnReview;

        if (stages.Any(s => s.State == HKApprovalStageState.RevisionRequired))
            return HKCardStatus.RevisionRequired;

        return HKCardStatus.Draft;
    }

    /// <summary>
    /// Возможности правки для указанного пользователя.
    /// <para>
    /// Реквизиты и PDF следуют за этапом реквизитов: PDF — часть основания, и
    /// он точно так же замораживается на рассмотрении. Состав следует за этапом
    /// состава; во время рассмотрения реквизитов он остаётся доступен, потому
    /// что автор заполняет структуру параллельно.
    /// </para>
    /// </summary>
    public static HKCardEditingCapabilities DeriveEditingCapabilities(
        IReadOnlyList<PersistedStageState> stages, HKCardStatus status, bool hasWorkPermission)
    {
        // Архивная и удалённая карта не редактируется ни в одной части.
        if (status is HKCardStatus.Archived or HKCardStatus.Deleted)
            return new HKCardEditingCapabilities(false, false, false);

        var details = FindPersistedStage(stages, HKApprovalStage.Details);
        var composition = FindPersistedStage(stages, HKApprovalStage.Composition);

        // У карты, ещё не проходившей ни одной команды, этапов нет — это
        // допустимое состояние, этапы создаст первая команда. Отсутствие этапа
        // само по себе редактирование не запрещает: запреты статуса отсечены
        // выше, а этап, которого нет, заблокировать не может.
        var detailsEditable = hasWorkPermission
                              && (details is null
                                  || details.State is HKApprovalStageState.Draft
                                      or HKApprovalStageState.RevisionRequired);

        var compositionEditable = hasWorkPermission
                                  && (composition is null
                                      || composition.State is HKApprovalStageState.Draft
                                          or HKApprovalStageState.RevisionRequired);

        return new HKCardEditingCapabilities(
            detailsEditable,
            compositionEditable,
            detailsEditable);
    }

    /// <summary>
    /// Приводит конкурентное изменение строки к типу конфликта версии: оно
    /// означает изменение данных, а не ошибку программы, и принадлежит
    /// предметной области ответа.
    /// </summary>
    private static async Task GuardedAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Настоящая отмена запроса: это не конкурентный исход и подменять его
            // конфликтом нельзя. Признак отмены доходит до вызывающего как есть.
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw VersionConflict();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            // Различаются конкретные конкурентные ситуации.
            switch (pg.SqlState)
            {
                // Ошибка сериализации и взаимоблокировка: конкурентные решения.
                case PostgresErrorCodes.SerializationFailure:
                case PostgresErrorCodes.DeadlockDetected:
                    throw VersionConflict();

                // Нарушение уникальности: конкурентная фиксация редакции.
                case PostgresErrorCodes.UniqueViolation when
                    pg.ConstraintName is "UX_HKCardRevisions_Card_Number"
                        or "UX_HKCardApprovals_Card_Stage":
                    throw VersionConflict();

                // Прочая ошибка базы: внутренние подробности пользователю не
                // выдаются, но и конфликтом она не называется.
                default:
                    throw new InvalidOperationException(
                        "Не удалось выполнить операцию согласования. Повторите попытку.");
            }
        }
        catch (PostgresException pg) when (
            pg.SqlState is PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.DeadlockDetected)
        {
            throw VersionConflict();
        }
    }

    private static HkCardVersionConflictException VersionConflict() =>
        new("Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");

    private static HKCardApproval? FindStage(IReadOnlyList<HKCardApproval> stages, HKApprovalStage stage) =>
        stages.FirstOrDefault(s => s.Stage == stage);

    private static PersistedStageState? FindPersistedStage(
        IReadOnlyList<PersistedStageState> stages, HKApprovalStage stage) =>
        stages.FirstOrDefault(s => s.Stage == stage);

    // ── Доступ к конкретной карте ──

    /// <summary>
    /// Доступ на чтение: право просмотра и принадлежность филиалу карты.
    /// <para>
    /// Проверяется действующее право, а не только факт входа: индивидуальный
    /// запрет обязан действовать и на чтении.
    /// </para>
    /// </summary>
    private async Task<HkCardAccessContext> BuildReadAccessAsync(Guid cardId, CancellationToken ct)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKView, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для просмотра ХК.");

        var card = await LoadPersistedCardAsync(cardId, ct);
        var kind = await ResolveBranchAccessAsync(card, actorId, ct);

        return new HkCardAccessContext(card, kind, actorId);
    }

    /// <summary>
    /// Доступ на отправку этапа: право HK.Submit, филиал карты и первоначальный
    /// автор.
    /// <para>
    /// Отправляет автор. Системный администратор — единственное исключение, и
    /// оно явное: попадает в значение контекста и в аудит команды. Скрытого
    /// обхода, при котором любой пользователь с правом отправляет чужую карту,
    /// здесь нет.
    /// </para>
    /// </summary>
    private async Task<HkCardAccessContext> BuildSubmitAccessAsync(
        Guid cardId, CancellationToken ct)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKSubmit, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для отправки ХК на согласование.");

        var card = await LoadPersistedCardAsync(cardId, ct);
        var kind = await ResolveBranchAccessAsync(card, actorId, ct);

        if (card.AuthorId == actorId)
            return new HkCardAccessContext(card, kind, actorId);

        if (kind == HkCardAccessKind.SystemAdmin)
            return new HkCardAccessContext(card, kind, actorId);

        throw new HkCardAccessDeniedException(
            "Отправить на согласование может первоначальный автор карты.");
    }

    /// <summary>
    /// Доступ на решение по этапу: право решения, активность пользователя,
    /// доступ к карте и назначение согласующим.
    /// <para>
    /// Наличие права само по себе не открывает решение: право есть у многих, а
    /// решение по этой карте принимает тот, кому она адресно направлена.
    /// </para>
    /// </summary>
    private async Task<HkCardAccessContext> BuildDecisionAccessAsync(
        Guid cardId, string requiredPermission, CancellationToken ct)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), requiredPermission, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для решения по этапу.");

        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor is null)
            throw new UnauthorizedAccessException("Пользователь не найден.");
        if (!actor.IsActive)
            throw new UnauthorizedAccessException("Пользователь отключён.");

        var card = await LoadPersistedCardAsync(cardId, ct);
        var kind = await ResolveBranchAccessAsync(card, actorId, ct);

        return new HkCardAccessContext(card, kind, actorId);
    }

    /// <summary>
    /// Принадлежность филиалу. Физическая граница данных, а не разрешение на
    /// действие: индивидуальное разрешение её не обходит.
    /// </summary>
    private async Task<HkCardAccessKind> ResolveBranchAccessAsync(
        PersistedCardState card, Guid actorId, CancellationToken ct)
    {
        if (await _permissions.IsSystemAdminAsync(actorId.ToString(), ct))
            return HkCardAccessKind.SystemAdmin;

        var actor = await _userManager.FindByIdAsync(actorId.ToString());
        if (actor?.BranchId != card.BranchId)
            throw new HkCardAccessDeniedException("Нет доступа к ХК другой организации.");

        return HkCardAccessKind.Branch;
    }

    /// <summary>
    /// Сохранённые данные карты. Независимая проекция: решение принимается по
    /// ней, а не по отслеживаемому экземпляру, который вызывающий код мог
    /// предварительно изменить.
    /// </summary>
    private async Task<PersistedCardState> LoadPersistedCardAsync(Guid cardId, CancellationToken ct) =>
        await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => new PersistedCardState(
                c.Id, c.Status, c.BranchId, c.AuthorId, c.IsAutonomous, c.Code, c.Version, c.RowVersion))
            .FirstOrDefaultAsync(ct)
        ?? throw new ArgumentException("ХК не найдена.");

    /// <summary>
    /// Карта для ЗАПИСИ. Загружается отслеживаемой уже после всех проверок:
    /// до них объект трогать нельзя, иначе отказ оставил бы подготовленные
    /// изменения в контексте.
    /// </summary>
    private async Task<HKCard> LoadCardForWriteAsync(Guid cardId, CancellationToken ct) =>
        await _db.HKCards.FirstOrDefaultAsync(c => c.Id == cardId, ct)
        ?? throw new ArgumentException("ХК не найдена.");

    // ── Команды первого этапа ──

    /// <summary>
    /// Отправляет реквизиты и основание на согласование конкретному согласующему.
    /// <para>
    /// Состав на этом шаге не проверяется и не требуется: структура заполняется
    /// параллельно и проверяется на своём этапе. Требование уровня не
    /// предъявляется — у автономной карты он не определён до вывода по
    /// структуре.
    /// </para>
    /// </summary>
    /// <summary>SubmitDetailsAsync: конкурентное изменение строки приводится к типу
    /// конфликта версии. Токен строки проверяется не только в финальной
    /// фиксации: служебные сервисы задач и аудита сохраняют свой контекст и могут
    /// выполнить отложенное обновление строки этапа раньше, чем управление дойдёт
    /// до финального сохранения. Приведение типа выполняется на границе команды,
    /// поэтому подробности PostgreSQL наружу не выходят.</summary>
    public Task SubmitDetailsAsync(Guid cardId, string approverUserId, uint expectedRowVersion, CancellationToken ct = default) =>
        GuardedAsync(SubmitDetailsAsyncCore(cardId, approverUserId, expectedRowVersion, ct));

    private async Task SubmitDetailsAsyncCore(
        Guid cardId, string approverUserId, uint expectedRowVersion, CancellationToken ct = default)
    {
        // Доступ проверяется по независимо сохранённому состоянию ДО открытия
        // транзакции и до любой записи.
        var access = await BuildSubmitAccessAsync(cardId, ct);
        var actorId = access.ActorUserId;

        EnsureNotTerminal(access.Card);
        EnsureRowVersion(access.Card, expectedRowVersion);

        var existingStages = await ReadStagesAsync(cardId, ct);
        var persistedDetails = existingStages.FirstOrDefault(s => s.Stage == HKApprovalStage.Details);

        if (persistedDetails?.State == HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Реквизиты уже отправлены на согласование.");
        if (persistedDetails?.State == HKApprovalStageState.Approved)
            throw new InvalidOperationException("Реквизиты уже приняты.");

        await EnsureApproverUsableAsync(approverUserId, access.Card, PermissionCodes.HKApprove, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Инициализация этапов — внутри команды и внутри её транзакции.
        var stages = await EnsureStagesForCommandAsync(cardId, ct);
        var details = FindStage(stages, HKApprovalStage.Details)!;
        var card = await LoadCardForWriteAsync(cardId, ct);

        // Версия проверяется ПОВТОРНО по строке, прочитанной внутри транзакции.
        //
        // Предварительная проверка по снимку, взятому до открытия транзакции,
        // защищает от устаревшего экрана, но не от гонки: между этими двумя
        // чтениями другая команда может зафиксировать изменения. Тогда проигравшая
        // команда перечитает уже обновлённую строку, токен совпадёт с её
        // собственной фиксацией, и UPDATE пройдёт — обе команды объявят успех.
        // Именно это и происходило: обе параллельные отправки проходили.
        EnsureRowVersion(card.RowVersion, expectedRowVersion);

        // Реквизиты проверяются как черновик: без требования структуры и уровня.
        var validation = await _validation.ValidateDraftAsync(card, ct);
        if (!validation.IsValid)
            throw new HKCardValidationException(validation.Errors);

        var now = _time.GetUtcNow().UtcDateTime;
        details.State = HKApprovalStageState.OnReview;
        details.AssignedApproverUserId = approverUserId;
        details.UpdatedAtUtc = now;
        card.Status = DeriveOverallStatus(stages);
        card.UpdatedAt = now;

        // Повторная отправка закрывает задачу доработки ЭТОГО этапа.
        // Задачи другого этапа остаются открытыми: возврат состава не
        // означает, что реквизиты снова требуют доработки.
        await CloseStageTasksAsync(card.Id, HKApprovalStage.Details, actorId, null, ct);

        await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
            Title: $"{DetailsStageTitle}: {card.Code} v{card.Version}",
            Type: WorkTaskType.HKDetailsReview,
            Priority: WorkTaskPriority.Normal,
            Description: $"Карта {card.Code} версии {card.Version} отправлена на согласование реквизитов и основания.",
            AssignedToUserId: approverUserId,
            BranchId: card.BranchId,
            EntityType: "HKCard",
            EntityId: card.Id,
            EntityCodeSnapshot: card.Code,
            EntityTitleSnapshot: card.Name,
            NotifyAssignee: true,
            HKStage: HKApprovalStage.Details), ct: ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCard", card.Id.ToString(), "DetailsSubmitted", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: $"Реквизиты отправлены на согласование. Согласующий: {approverUserId}."), ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Принимает реквизиты. Фиксирует дату принятия один раз.
    /// <para>
    /// Решение принимает именно назначенный согласующий. Системный администратор
    /// не подменяет назначенного согласующего молча: это отдельная операция,
    /// которой в этом PR нет.
    /// </para>
    /// </summary>
    /// <summary>ApproveDetailsAsync: конкурентное изменение строки приводится к типу
    /// конфликта версии. Токен строки проверяется не только в финальной
    /// фиксации: служебные сервисы задач и аудита сохраняют свой контекст и могут
    /// выполнить отложенное обновление строки этапа раньше, чем управление дойдёт
    /// до финального сохранения. Приведение типа выполняется на границе команды,
    /// поэтому подробности PostgreSQL наружу не выходят.</summary>
    public Task ApproveDetailsAsync(Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default) =>
        GuardedAsync(ApproveDetailsAsyncCore(cardId, comment, expectedRowVersion, ct));

    private async Task ApproveDetailsAsyncCore(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default)
    {
        // Право решения, активность пользователя, доступ к карте и назначение
        // согласующим проверяются по сохранённому состоянию.
        var access = await BuildDecisionAccessAsync(cardId, PermissionCodes.HKApprove, ct);
        var actorId = access.ActorUserId;

        EnsureNotTerminal(access.Card);
        EnsureRowVersion(access.Card, expectedRowVersion);

        var stages = (await EnsureStagesForCommandAsync(cardId, ct));
        var details = FindStage(stages, HKApprovalStage.Details)!;

        EnsureAssignedApprover(details, actorId.ToString());

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var card = await LoadCardForWriteAsync(cardId, ct);

        // Повторная проверка версии по строке, прочитанной ВНУТРИ транзакции.
        // Предварительной проверки по снимку вне транзакции для защиты от
        // гонки недостаточно: между двумя чтениями другая команда может
        // зафиксировать изменения, и проигравшая сверит версию с самой собой.
        EnsureRowVersion(card.RowVersion, expectedRowVersion);

        if (details.State != HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Принять реквизиты можно только когда они на рассмотрении.");

        var now = _time.GetUtcNow().UtcDateTime;
        details.State = HKApprovalStageState.Approved;
        // Дата принятия заполняется один раз и не обнуляется при возврате
        // другого этапа. Это момент решения, а не EffectiveDate документа.
        details.ApprovedAtUtc ??= now;
        details.UpdatedAtUtc = now;
        card.Status = DeriveOverallStatus(stages);
        card.UpdatedAt = now;

        await CloseReviewTasksAsync(card.Id, HKApprovalStage.Details, actorId, comment, ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCard", card.Id.ToString(), "DetailsApproved", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: string.IsNullOrWhiteSpace(comment)
                ? "Реквизиты приняты."
                : $"Реквизиты приняты. Комментарий: {comment}"), ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Возвращает реквизиты на доработку. Данные не удаляются.
    /// <para>
    /// Структура и конечные строки при возврате сохраняются: возврат одного этапа
    /// не отменяет работу над другим.
    /// </para>
    /// </summary>
    /// <summary>ReturnDetailsAsync: конкурентное изменение строки приводится к типу
    /// конфликта версии. Токен строки проверяется не только в финальной
    /// фиксации: служебные сервисы задач и аудита сохраняют свой контекст и могут
    /// выполнить отложенное обновление строки этапа раньше, чем управление дойдёт
    /// до финального сохранения. Приведение типа выполняется на границе команды,
    /// поэтому подробности PostgreSQL наружу не выходят.</summary>
    public Task ReturnDetailsAsync(Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default) =>
        GuardedAsync(ReturnDetailsAsyncCore(cardId, comment, expectedRowVersion, ct));

    private async Task ReturnDetailsAsyncCore(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default)
    {
        var access = await BuildDecisionAccessAsync(cardId, PermissionCodes.HKReview, ct);
        var actorId = access.ActorUserId;

        EnsureNotTerminal(access.Card);
        EnsureRowVersion(access.Card, expectedRowVersion);

        var stages = (await EnsureStagesForCommandAsync(cardId, ct));
        var details = FindStage(stages, HKApprovalStage.Details)!;

        EnsureAssignedApprover(details, actorId.ToString());

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var card = await LoadCardForWriteAsync(cardId, ct);

        // Повторная проверка версии по строке, прочитанной ВНУТРИ транзакции.
        // Предварительной проверки по снимку вне транзакции для защиты от
        // гонки недостаточно: между двумя чтениями другая команда может
        // зафиксировать изменения, и проигравшая сверит версию с самой собой.
        EnsureRowVersion(card.RowVersion, expectedRowVersion);

        if (details.State != HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Вернуть реквизиты можно только когда они на рассмотрении.");

        var now = _time.GetUtcNow().UtcDateTime;
        details.State = HKApprovalStageState.RevisionRequired;
        details.UpdatedAtUtc = now;
        card.Status = DeriveOverallStatus(stages);
        card.UpdatedAt = now;

        await CloseReviewTasksAsync(card.Id, HKApprovalStage.Details, actorId, comment, ct);
        await CreateAuthorRevisionTaskAsync(card, HKApprovalStage.Details, "Реквизиты возвращены на доработку.", comment, ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCard", card.Id.ToString(), "DetailsReturned", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: string.IsNullOrWhiteSpace(comment)
                ? "Реквизиты возвращены на доработку."
                : $"Реквизиты возвращены на доработку. Комментарий: {comment}"), ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ── Команды второго этапа ──

    /// <summary>
    /// Отправляет состав и нормы на согласование. Доступно только после
    /// принятия реквизитов.
    /// <para>
    /// Назначенный согласующий сохраняется: второй этап согласует тот же
    /// человек, который принял реквизиты.
    /// </para>
    /// </summary>
    /// <summary>SubmitCompositionAsync: конкурентное изменение строки приводится к типу
    /// конфликта версии. Токен строки проверяется не только в финальной
    /// фиксации: служебные сервисы задач и аудита сохраняют свой контекст и могут
    /// выполнить отложенное обновление строки этапа раньше, чем управление дойдёт
    /// до финального сохранения. Приведение типа выполняется на границе команды,
    /// поэтому подробности PostgreSQL наружу не выходят.</summary>
    public Task SubmitCompositionAsync(Guid cardId, uint expectedRowVersion, CancellationToken ct = default) =>
        GuardedAsync(SubmitCompositionAsyncCore(cardId, expectedRowVersion, ct));

    private async Task SubmitCompositionAsyncCore(
        Guid cardId, uint expectedRowVersion, CancellationToken ct = default)
    {
        var access = await BuildSubmitAccessAsync(cardId, ct);
        var actorId = access.ActorUserId;

        EnsureNotTerminal(access.Card);
        EnsureRowVersion(access.Card, expectedRowVersion);

        var existing = await ReadStagesAsync(cardId, ct);
        var detailsState = existing.FirstOrDefault(s => s.Stage == HKApprovalStage.Details);
        var compositionState = existing.FirstOrDefault(s => s.Stage == HKApprovalStage.Composition);

        if (detailsState?.State != HKApprovalStageState.Approved)
            throw new InvalidOperationException(
                "Отправить состав на согласование можно только после принятия реквизитов.");

        if (compositionState?.State == HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Состав уже отправлен на согласование.");

        if (string.IsNullOrWhiteSpace(detailsState!.AssignedApproverUserId))
            throw new InvalidOperationException(
                "Не найден назначенный согласующий: отправьте реквизиты на согласование.");

        // Согласующий состава — тот же человек, что принял реквизиты. Перед
        // повторной отправкой он проверяется заново: мог быть отключён или
        // потерять полномочия. Заменять его автоматически нельзя.
        await EnsureApproverUsableAsync(
            detailsState.AssignedApproverUserId, access.Card, PermissionCodes.HKApprove, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var stages = await EnsureStagesForCommandAsync(cardId, ct);
        var details = FindStage(stages, HKApprovalStage.Details)!;
        var composition = FindStage(stages, HKApprovalStage.Composition)!;
        var card = await LoadCardForWriteAsync(cardId, ct);

        // Повторная проверка версии по строке, прочитанной ВНУТРИ транзакции.
        // Предварительной проверки по снимку вне транзакции для защиты от
        // гонки недостаточно: между двумя чтениями другая команда может
        // зафиксировать изменения, и проигравшая сверит версию с самой собой.
        EnsureRowVersion(card.RowVersion, expectedRowVersion);

        // Структура проверяется соответствующими валидаторами. Карта только с
        // конечными строками под корнем допустима.
        var validation = await _validation.ValidateForReviewAsync(card, ct);
        if (!validation.IsValid)
            throw new HKCardValidationException(validation.Errors);

        var now = _time.GetUtcNow().UtcDateTime;
        composition.State = HKApprovalStageState.OnReview;
        composition.AssignedApproverUserId = details.AssignedApproverUserId;
        composition.UpdatedAtUtc = now;
        card.Status = DeriveOverallStatus(stages);
        card.UpdatedAt = now;

        await CloseStageTasksAsync(card.Id, HKApprovalStage.Composition, actorId, null, ct);

        await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
            Title: $"{CompositionStageTitle}: {card.Code} v{card.Version}",
            Type: WorkTaskType.HKCompositionReview,
            Priority: WorkTaskPriority.Normal,
            Description: $"Карта {card.Code} версии {card.Version} отправлена на согласование состава и норм.",
            AssignedToUserId: composition.AssignedApproverUserId,
            BranchId: card.BranchId,
            EntityType: "HKCard",
            EntityId: card.Id,
            EntityCodeSnapshot: card.Code,
            EntityTitleSnapshot: card.Name,
            NotifyAssignee: true,
            HKStage: HKApprovalStage.Composition), ct: ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCard", card.Id.ToString(), "CompositionSubmitted", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: "Состав и нормы отправлены на согласование."), ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Возвращает состав на доработку. Данные не удаляются.</summary>
    /// <summary>ReturnCompositionAsync: конкурентное изменение строки приводится к типу
    /// конфликта версии. Токен строки проверяется не только в финальной
    /// фиксации: служебные сервисы задач и аудита сохраняют свой контекст и могут
    /// выполнить отложенное обновление строки этапа раньше, чем управление дойдёт
    /// до финального сохранения. Приведение типа выполняется на границе команды,
    /// поэтому подробности PostgreSQL наружу не выходят.</summary>
    public Task ReturnCompositionAsync(Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default) =>
        GuardedAsync(ReturnCompositionAsyncCore(cardId, comment, expectedRowVersion, ct));

    private async Task ReturnCompositionAsyncCore(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default)
    {
        var access = await BuildDecisionAccessAsync(cardId, PermissionCodes.HKReview, ct);
        var actorId = access.ActorUserId;

        EnsureNotTerminal(access.Card);
        EnsureRowVersion(access.Card, expectedRowVersion);

        var stages = (await EnsureStagesForCommandAsync(cardId, ct));
        var composition = FindStage(stages, HKApprovalStage.Composition)!;

        EnsureAssignedApprover(composition, actorId.ToString());

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var card = await LoadCardForWriteAsync(cardId, ct);

        // Повторная проверка версии по строке, прочитанной ВНУТРИ транзакции.
        // Предварительной проверки по снимку вне транзакции для защиты от
        // гонки недостаточно: между двумя чтениями другая команда может
        // зафиксировать изменения, и проигравшая сверит версию с самой собой.
        EnsureRowVersion(card.RowVersion, expectedRowVersion);

        if (composition.State != HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Вернуть состав можно только когда он на рассмотрении.");

        var now = _time.GetUtcNow().UtcDateTime;
        composition.State = HKApprovalStageState.RevisionRequired;
        composition.UpdatedAtUtc = now;
        card.Status = DeriveOverallStatus(stages);
        card.UpdatedAt = now;

        await CloseReviewTasksAsync(card.Id, HKApprovalStage.Composition, actorId, comment, ct);
        await CreateAuthorRevisionTaskAsync(card, HKApprovalStage.Composition, "Состав возвращён на доработку.", comment, ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCard", card.Id.ToString(), "CompositionReturned", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: string.IsNullOrWhiteSpace(comment)
                ? "Состав возвращён на доработку."
                : "Состав возвращён на доработку."), ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Окончательное принятие состава.
    /// <para>
    /// Намеренно недоступно. Оно публикует рабочее дерево как принятую
    /// структуру, а хранилища принятой структуры ещё нет: создавать
    /// фиктивную принятую копию означало бы выдать виджет за выполненную работу,
    /// и карта получила бы утверждённый статус без фактического принятия.
    /// </para>
    /// </summary>
    public Task ApproveCompositionAsync(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default) =>
        GuardedAsync(ApproveCompositionAsyncCore(cardId, comment, expectedRowVersion, ct));

    /// <summary>
    /// Окончательное принятие состава: единственная команда, переводящая карту в
    /// утверждённое состояние.
    /// <para>
    /// Владелец общей транзакции — этот метод. Она открывается с уровнем
    /// REPEATABLE READ, который <see cref="HKCardRevisionService"/> принимает как
    /// совместимый: вложенная транзакция не открывается, общая не коммитится
    /// сервисом фиксации. Любая ошибка на любом шаге откатывает ВСЮ операцию, и
    /// частично принятая карта не может возникнуть.
    /// </para>
    /// <para>
    /// Источник принимаемых данных — только сохранённая рабочая карта. Клиент не
    /// присылает снимок и не назначает указатель: иначе он подменял бы принятую
    /// структуру.
    /// </para>
    /// <para>
    /// Идентификаторы карты, названия, номера и отображаемой версии не меняются.
    /// Принятие состава не трогает дату принятия реквизитов, EffectiveDate и
    /// ExpirationDate: правило начала учёта срока после принятия реквизитов
    /// остаётся в силе.
    /// </para>
    /// </summary>
    private async Task ApproveCompositionAsyncCore(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default)
    {
        // ── Проверки ДО транзакции: право, активность, филиал, карта ──
        // Решение о доступе принимается по сохранённому состоянию, а не по
        // объектам, которые вызывающий код мог загрузить и изменить.
        var access = await BuildDecisionAccessAsync(cardId, PermissionCodes.HKApprove, ct);
        var actorId = access.ActorUserId;

        EnsureNotTerminal(access.Card);
        EnsureRowVersion(access.Card, expectedRowVersion);

        // Несохранённые изменения вызывающего отсекаются до чтения и записи:
        // промежуточный SaveChanges при фиксации выполнил бы их попутно.
        EnsureNoPendingCallerChanges(cardId);

        // Этапы ТОЛЬКО читаются. Для окончательного принятия они уже должны
        // существовать: состав обязан был пройти отправку. Создавать этапы
        // попутно с принятием нельзя — принятие не имеет права материализовать
        // состояние, которого не было.
        var preStages = await ReadStagesAsync(cardId, ct);
        var preDetails = preStages.FirstOrDefault(s => s.Stage == HKApprovalStage.Details);
        var preComposition = preStages.FirstOrDefault(s => s.Stage == HKApprovalStage.Composition);

        if (preDetails is null || preComposition is null)
            throw new InvalidOperationException(
                "У карты отсутствуют этапы согласования: окончательное принятие невозможно.");

        EnsureCompositionApprovable(preDetails, preComposition, actorId.ToString());

        try
        {
        // ── Общая транзакция принадлежит этой команде ──
        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct);

        // ── Независимое чтение сохранённого состояния ВНУТРИ транзакции ──
        // Отслеживаемые строки читаются ЗДЕСЬ и только для записи. Решение
        // принимается по проекциям: повторный tracking-запрос отслеживаемой сущности
        // вернул бы тот же экземпляр и не увидел бы изменений из базы.
        var persistedCard = await LoadPersistedCardAsync(cardId, ct);
        EnsureNotTerminal(persistedCard);
        EnsureRowVersion(persistedCard, expectedRowVersion);

        var stages = await ReadStagesAsync(cardId, ct);
        var detailsState = stages.FirstOrDefault(s => s.Stage == HKApprovalStage.Details);
        var compositionState = stages.FirstOrDefault(s => s.Stage == HKApprovalStage.Composition);

        if (detailsState is null || compositionState is null)
            throw new InvalidOperationException("Этапы согласования отсутствуют.");

        // Те же условия, что и до транзакции, но по данным из базы.
        EnsureCompositionApprovable(detailsState, compositionState, actorId.ToString());

        // ── Строки для записи ──
        // Отслеживаемые экземпляры могли быть изменены вызывающим кодом, поэтому
        // охраняемые поля возвращаются к сохранённым значениям ДО проверки и записи.
        var card = await LoadCardForWriteAsync(cardId, ct);
        RestoreGuardedFields(card, persistedCard);

        var details = await _db.HKCardApprovals.FirstAsync(a => a.Id == detailsState.Id, ct);
        var composition = await _db.HKCardApprovals.FirstAsync(a => a.Id == compositionState.Id, ct);

        // Версии этапов проверяются по сохранённым значениям: конкурентное
        // изменение строки должно отклониться, а не перезаписаться молча.
        EnsureStageRowVersion(detailsState, details, "реквизитов");
        EnsureStageRowVersion(compositionState, composition, "состава");

        // Охраняемые поля этапов тоже возвращаются к сохранённым.
        details.State = detailsState.State;
        details.AssignedApproverUserId = detailsState.AssignedApproverUserId;
        details.ApprovedAtUtc = detailsState.ApprovedAtUtc;
        composition.State = compositionState.State;
        composition.AssignedApproverUserId = compositionState.AssignedApproverUserId;
        composition.ApprovedAtUtc = compositionState.ApprovedAtUtc;

        // ── Полная проверка принимаемого состава ──
        // ValidateDraftAsync проверяет только реквизиты. Окончательное принятие
        // публикует структуру, поэтому проверяется всё, что попадёт в снимок.
        var validation = await _validation.ValidateForApprovalAsync(card, ct);
        if (!validation.IsValid)
            throw new HKCardValidationException(validation.Errors);

        // ── Существующее вложение должно быть доступно и цело ──
        await EnsureAttachmentUsableAsync(cardId, ct);

        var previousRevisionId = card.CurrentAcceptedRevisionId;
        var previousStatus = card.Status;
        var now = _time.GetUtcNow().UtcDateTime;

        // ── 1-3. Фиксация сохранённой рабочей структуры ──
        var revisionId = await _revisions.CaptureAsync(
            cardId, card.RowVersion, now, actorId.ToString(), ct);

        var belongs = await _db.HKCardRevisions
            .AsNoTracking()
            .AnyAsync(r => r.Id == revisionId && r.HKCardId == cardId, ct);
        if (!belongs)
            throw new InvalidOperationException("Редакция не принадлежит этой карте.");

        // ── 4. Указатель на принятую структуру ──
        card.CurrentAcceptedRevisionId = revisionId;

        // ── 5-6. Принятие этапа состава ──
        composition.State = HKApprovalStageState.Approved;
        composition.ApprovedAtUtc = now;
        composition.UpdatedAtUtc = now;

        // ── 7-8. Статус карты и сведения о решении ──
        card.Status = HKCardStatus.Approved;
        card.UpdatedAt = now;
        card.ReviewerId = actorId;
        if (!card.ApprovedDate.HasValue)
            card.ApprovedDate = now;

        // ── 9. Закрытие задач этого этапа ──
        await CloseReviewTasksAsync(card.Id, HKApprovalStage.Composition, actorId, comment, ct);
        await CloseStageTasksAsync(card.Id, HKApprovalStage.Composition, actorId, comment, ct);

        // ── 10. Журнал, аудит и уведомление ──
        _db.HKCardStatusLogs.Add(new HKCardStatusLog
        {
            Id = Guid.NewGuid(),
            HKCardId = card.Id,
            FromStatus = previousStatus,
            ToStatus = HKCardStatus.Approved,
            ChangedByUserId = actorId,
            Comment = string.IsNullOrWhiteSpace(comment) ? "Состав и нормы приняты." : comment,
            ChangedAt = now
        });

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCard", card.Id.ToString(), "CompositionApproved", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: previousRevisionId is null
                ? $"Состав и нормы приняты. Создана принятая редакция {revisionId}."
                : $"Состав и нормы приняты. Создана принятая редакция {revisionId}; " +
                  $"прежняя принятая редакция {previousRevisionId}."), ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCardRevision", revisionId.ToString(), "Accepted", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: "Редакция принята как последняя принятая структура карты."), ct);

        if (card.AuthorId.HasValue)
        {
            await _notifications.AddAsync(
                card.AuthorId.Value.ToString(),
                new CreateNotificationCommand(
                    NotificationType.HKApproved,
                    "ХК принята",
                    $"Карта {card.Code} версии {card.Version} принята: состав и нормы утверждены.",
                    EntityType: "HKCard",
                    EntityId: card.Id,
                    // Маршрут страницы карты объявлен как @page "/хк/{Id:guid}".
                    NavigationUrl: $"/хк/{card.Id}",
                    BranchId: card.BranchId,
                    DeduplicationKey: $"hk-accepted-{card.Id}-{revisionId}"),
                ct);
        }

        // ── 11. Фиксация ──
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        }
        catch
        {
            // Откат PostgreSQL отменяет ЗАПИСИ, но не восстанавливает объекты
            // ChangeTracker. Без этого следующий SaveChanges того же scope дописал
            // бы отменённые строки снимка, указатель, аудит и уведомление.
            await RecoverAfterFailureAsync();
            throw;
        }
    }

    /// <summary>
    /// Явное завершение неуспешной единицы работы.
    /// <para>
    /// Записи, появившиеся в трекере после начала команды, отделяются: они
    /// принадлежат отменённой попытке. Записи, существовавшие ранее, и были
    /// перечитаны — но к моменту отказа несохранённых изменений вызывающего уже
    /// быть не может: команда отклонила их до любой записи.
    /// </para>
    /// <para>
    /// Отменённая попытка не удаляется из базы и не компенсируется: единственный
    /// источник истины — транзакция. Восстановление касается только состояния
    /// объектов в памяти, чтобы scope стал пригоден для следующей работы.
    /// </para>
    /// </summary>
    private async Task RecoverAfterFailureAsync()
    {
        // Набор записей, которые пишет команда принятия.
        //
        // Порядок обязателен: дети отделяются раньше родителя. Отделение
        // заголовка снимка при ещё отслеживаемых строках обрывает связь, и EF
        // бросает исключение вместо отсоединения — отмена решения не должна
        // приводить к выходу из catch с потерей исходной ошибки.
        //
        // Перечисление берётся из DbSet.Local, а не из снимка
        // ChangeTracker.Entries(): после прерванного SaveChanges состав
        // отложенных записей определяется EF, и снимок неполон.
        //
        // Проверка несохранённых изменений вызывающего выполнялась ДО любой
        // записи, поэтому отделяемое здесь принадлежит отменённой попытке, а не
        // вводу пользователя.
        DetachAll(_db.HKCardRevisionItemMaterials.Local);
        DetachAll(_db.HKCardRevisionItems.Local);
        DetachAll(_db.HKCardRevisionNodes.Local);
        DetachAll(_db.HKCardRevisions.Local);
        DetachAll(_db.AuditLogs.Local);
        DetachAll(_db.Notifications.Local);
        DetachAll(_db.HKCardStatusLogs.Local);

        // Изменённые и удалённые записи вызывающего возвращаются к сохранённым.
        foreach (var entry in _db.ChangeTracker.Entries()
            .Where(e => e.State is Microsoft.EntityFrameworkCore.EntityState.Modified
                or Microsoft.EntityFrameworkCore.EntityState.Deleted)
            .ToList())
        {
            await entry.ReloadAsync();
        }
    }

    /// <summary>
    /// Отсоединяет добавленные и помеченные к удалению записи набора.
    /// Изменённые не трогаются: их состояние разбирает вызывающий после
    /// восстановления.
    /// </summary>
    private void DetachAll<T>(IEnumerable<T> local) where T : class
    {
        foreach (var entity in local.ToList())
        {
            var entry = _db.Entry(entity);
            if (entry.State is Microsoft.EntityFrameworkCore.EntityState.Added
                or Microsoft.EntityFrameworkCore.EntityState.Deleted)
            {
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Условия, при которых состав можно принять. Проверяется по сохранённым
    /// состояниям этапов, а не по отслеживаемым экземплярам.
    /// </summary>
    private static void EnsureCompositionApprovable(
        PersistedStageState details, PersistedStageState composition, string actorId)
    {
        // Решение принимает назначенный согласующий, а не любой пользователь с
        // правом. Обхода назначения для SystemAdmin здесь нет.
        if (string.IsNullOrWhiteSpace(composition.AssignedApproverUserId))
            throw new InvalidOperationException(
                "Этап не направлен конкретному согласующему: отправьте его на согласование.");

        if (composition.AssignedApproverUserId != actorId)
            throw new UnauthorizedAccessException(
                "Решение по составу принимает назначенный согласующий.");

        if (details.State != HKApprovalStageState.Approved)
            throw new InvalidOperationException(
                "Принять состав можно только после принятия реквизитов и основания.");

        if (composition.State == HKApprovalStageState.Approved)
            throw new InvalidOperationException("Состав уже принят.");

        if (composition.State != HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Принять состав можно только когда он на рассмотрении.");

        // Оба этапа рассматривает один конкретный согласующий.
        if (details.AssignedApproverUserId != composition.AssignedApproverUserId)
            throw new InvalidOperationException(
                "Согласующий состава не совпадает с согласующим реквизитов.");
    }

    /// <summary>
    /// Токен строки этапа сверяется с сохранённым: конкурентное изменение должно
    /// отклониться, а не перезаписаться молча.
    /// </summary>
    private static void EnsureStageRowVersion(
        PersistedStageState persisted, HKCardApproval tracked, string stageName)
    {
        if (tracked.RowVersion != persisted.RowVersion)
            throw new HkCardVersionConflictException(
                $"Этап {stageName} изменён другим пользователем. Обновите страницу и повторите попытку.");
    }

    /// <summary>
    /// Отказывает, если в контексте остались несохранённые изменения вызывающего.
    /// <para>
    /// Команда принимает решение по сохранённым данным. Промежуточный
    /// <c>SaveChanges</c> внутри фиксации выполнил бы отложенные изменения того же
    /// контекста: правку формы, добавленную сущность, изменённые в памяти
    /// наименования. Ввод пользователя при этом либо записался бы попутно без
    /// спроса, либо потерялся бы — оба исхода неверны.
    /// </para>
    /// <para>
    /// <c>ChangeTracker.Clear()</c> здесь не применяется: он уничтожил бы ввод
    /// молча. Вместо этого — понятный отказ ДО любой записи, с предложением
    /// сохранить или обновить данные.
    /// </para>
    /// </summary>
    private void EnsureNoPendingCallerChanges(Guid cardId)
    {
        var pending = _db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified
                                or EntityState.Deleted)
            .ToList();

        // Записи самой команды ещё не существуют: команда начинается с этого
        // момента, и всё подозрительное принадлежит вызывающему коду.
        if (pending.Count == 0)
            return;

        var foreign = pending
            .Where(e => !IsCardEntity(e, cardId))
            .Select(e => e.Metadata.Name)
            .Distinct()
            .ToList();

        var cardChanges = pending
            .Where(e => IsCardEntity(e, cardId))
            .SelectMany(e => e.Properties
                .Where(pr => pr.IsModified || e.State == EntityState.Added)
                .Select(pr => pr.Metadata.Name))
            .Distinct()
            .ToList();

        if (foreign.Count == 0 && cardChanges.Count == 0)
            return;

        var details = new List<string>();
        if (foreign.Count > 0)
            details.Add("несохранённые изменения сущностей: "
                        + string.Join(", ", foreign.Take(3)));
        if (cardChanges.Count > 0)
            details.Add("несохранённые изменения карты: "
                        + string.Join(", ", cardChanges.Take(5)));

        throw new InvalidOperationException(
            "В контексте есть несохранённые изменения ("
            + string.Join("; ", details)
            + "). Сохраните или отмените их перед решением по согласованию.");
    }

    /// <summary>
    /// Относится ли отслеживаемая сущность к карте, за которую принимается решение.
    /// </summary>
    private static bool IsCardEntity(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, Guid cardId)
    {
        if (entry.Entity is HKCard card)
            return card.Id == cardId;

        if (entry.Entity is HKCardItem item)
            return item.HKCardId == cardId;

        if (entry.Entity is HKCardItemMaterial material)
        {
            if (material.HKCardItemId == Guid.Empty)
                return false;

            // Строка может быть ещё не сохранена: её карта уже в контексте.
            return entry.Context.Entry(material)
                       ?.Reference(x => x.HKCardItem)?.TargetEntry?.Entity is HKCardItem trackedItem
                   ? trackedItem.HKCardId == cardId
                   : true;
        }

        if (entry.Entity is HKCardNode node)
            return node.HKCardId == cardId;

        return false;
    }

    /// <summary>
    /// Возвращает охраняемые поля карты к СОХРАНЁННЫМ значениям.
    /// <para>
    /// Отслеживаемый экземпляр — тот же объект, который вызывающий код мог
    /// изменить заранее. Без возврата к сохранённым значениям команда записала бы
    /// подменённые статус, филиал, автора или указатель принятой редакции.
    /// </para>
    /// </summary>
    private static void RestoreGuardedFields(HKCard card, PersistedCardState persisted)
    {
        card.Status = persisted.Status;
        card.BranchId = persisted.BranchId;
        card.AuthorId = persisted.AuthorId;
        card.Code = persisted.Code;
        card.Version = persisted.Version;
    }

    /// <summary>
    /// Существующее вложение должно существовать и совпадать с сохранёнными
    /// метаданными: принятая структура не может ссылаться на недоступный файл.
    /// <para>
    /// PDF не обязателен для всех карт: требование действующего первого этапа
    /// сохраняется, проверяется только то вложение, которое уже есть.
    /// </para>
    /// </summary>
    private async Task EnsureAttachmentUsableAsync(Guid cardId, CancellationToken ct)
    {
        var attachment = await _db.HKCardAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.HKCardId == cardId, ct);

        if (attachment is null)
            return;

        Stream stream;
        try
        {
            stream = await _fileStorage.OpenReadAsync(attachment.StorageKey, ct);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(
                "Файл PDF-скана не найден: принять состав с таким основанием нельзя.");
        }

        await using (stream)
        {
            if (attachment.SizeBytes > 0 && stream.Length != attachment.SizeBytes)
                throw new InvalidOperationException(
                    "Размер PDF-скана не совпадает с сохранённым: основание повреждено.");

            if (!string.IsNullOrWhiteSpace(attachment.Sha256))
            {
                var actual = Convert.ToHexString(
                    await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
                if (!string.Equals(actual, attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Контрольная сумма PDF-скана не совпадает с сохранённой: основание повреждено.");
            }
        }
    }

    // ── Общие проверки ──

    /// <summary>
    /// Карта для принятия решения. Загружается ОТСЛЕЖИВАЕМОЙ: решение меняет
    /// её состояние, и изменение должно попасть в ту же транзакцию, что и
    /// состояния этапов, задачи и аудит.
    /// </summary>
    private async Task<HKCard> LoadCardForDecisionAsync(Guid cardId, CancellationToken ct) =>
        await _db.HKCards.FirstOrDefaultAsync(c => c.Id == cardId, ct)
        ?? throw new ArgumentException("ХК не найдена.");

    /// <summary>
    /// Версия карты на момент загрузки вызывающего. Без неё два одновременных
    /// решения перезаписали бы друг друга: второе увидело бы уже применённое
    /// состояние и сочло бы себя первым.
    /// <para>
    /// Проверяется против снимка из базы, а не против значения, присланного
    /// клиентом: иначе клиент отправил бы актуальную версию и обошёл гонку.
    /// </para>
    /// </summary>
    private static void EnsureRowVersion(PersistedCardState card, uint expectedRowVersion)
    {
        if (expectedRowVersion == 0)
            throw new InvalidOperationException(
                "Версия карточки не указана. Обновите страницу и повторите попытку.");

        EnsureRowVersion(card.RowVersion, expectedRowVersion);
    }

    /// <summary>
    /// Сверяет версию строки с той, которую знает вызывающий.
    /// <para>
    /// Значение обязано быть фактической версией ПРОЧТЁННОЙ СТРОКИ. Проверка по
    /// значению, прочитанному до открытия транзакции, не защищает от гонки:
    /// проигравшая команда успевает перечитать уже обновлённую строку и сверить
    /// её версию с самой собой.
    /// </para>
    /// </summary>
    private static void EnsureRowVersion(uint actualRowVersion, uint expectedRowVersion)
    {
        if (expectedRowVersion == 0)
            throw new InvalidOperationException(
                "Версия карточки не указана. Обновите страницу и повторите попытку.");

        if (actualRowVersion != expectedRowVersion)
            throw new HkCardVersionConflictException(
                "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
    }

    private static void EnsureNotTerminal(PersistedCardState card)
    {
        if (card.Status is HKCardStatus.Archived or HKCardStatus.Deleted)
            throw new InvalidOperationException(
                "Карта находится в архиве или удалена: согласование недоступно.");
    }

    /// <summary>
    /// Решение принимает назначенный согласующий и только он.
    /// <para>
    /// Проверка по идентичности пользователя, а не по наличию полномочия:
    /// право есть у многих, а решение по этой карте принять должен тот, кому она
    /// адресно направлена. Системный администратор не обходит это молча.
    /// </para>
    /// </summary>
    private static void EnsureAssignedApprover(HKCardApproval stage, string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(stage.AssignedApproverUserId))
            throw new InvalidOperationException(
                "Этап не направлен конкретному согласующему: отправьте его на согласование.");

        if (!string.Equals(stage.AssignedApproverUserId, actorUserId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "Решение может принять только назначенный согласующий.");
    }

    /// <summary>
    /// Согласующий должен существовать, быть активным, относиться к филиалу
    /// карты и иметь действующее полномочие.
    /// <para>
    /// Роль не требуется: согласующим может быть начальник подразделения с
    /// нужными полномочиями. Требуется именно эффективное разрешение, поэтому
    /// индивидуальный запрет учитывается.
    /// </para>
    /// </summary>
    private async Task EnsureApproverUsableAsync(
        string approverUserId, PersistedCardState card, string requiredPermission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(approverUserId))
            throw new InvalidOperationException("Выберите согласующего.");

        var approver = await _userManager.FindByIdAsync(approverUserId);
        if (approver == null)
            throw new InvalidOperationException("Выбранный согласующий не найден.");

        if (!approver.IsActive)
            throw new InvalidOperationException("Выбранный согласующий отключён.");

        // Физическая граница: согласующий должен быть в филиале карты.
        if (approver.BranchId != card.BranchId && !await _permissions.IsSystemAdminAsync(approverUserId, ct))
            throw new InvalidOperationException(
                "Выбранный согласующий относится к другой организации.");

        if (!await _permissions.HasPermissionAsync(approverUserId, requiredPermission, ct))
            throw new InvalidOperationException(
                "У выбранного согласующего нет действующих полномочий на согласование.");
    }

    /// <summary>
    /// Закрывает открытые задачи рассмотрения этапа. Повторная отправка не
    /// плодит дубликаты: задачи фильтруются по типу, карте и открытому
    /// состоянию.
    /// </summary>
    /// <summary>
    /// Закрывает открытые задачи рассмотрения ЭТОГО этапа и пишет аудит по
    /// каждой закрытой задаче.
    /// <para>
    /// Этап берётся из данных задачи, а не из её типа: типы задач рассмотрения
    /// и доработки общие для обоих этапов, и фильтр только по типу закрыл бы
    /// чужую. Аудит закрытия теряться не должен — он пишется в той же
    /// транзакции, что и изменение задачи.
    /// </para>
    /// </summary>
    private async Task CloseStageTasksAsync(
        Guid cardId, HKApprovalStage stage, Guid actorUserId, string? comment, CancellationToken ct)
    {
        var open = await _db.WorkTasks
            .Where(t => !t.IsDeleted
                && t.Type == WorkTaskType.HKRevision
                && t.EntityType == "HKCard"
                && t.EntityId == cardId
                && t.HKStage == stage
                && (t.Status == WorkTaskStatus.Open
                    || t.Status == WorkTaskStatus.InProgress
                    || t.Status == WorkTaskStatus.Overdue))
            .ToListAsync(ct);

        await CloseTasksAsync(open, actorUserId, comment, ct);
    }

    /// <summary>
    /// Закрывает открытые задачи рассмотрения этапа: при принятии или возврате
    /// задача согласующему больше не нужна.
    /// </summary>
    private async Task CloseReviewTasksAsync(
        Guid cardId, HKApprovalStage stage, Guid actorUserId, string? comment, CancellationToken ct)
    {
        var reviewType = stage == HKApprovalStage.Details
            ? WorkTaskType.HKDetailsReview
            : WorkTaskType.HKCompositionReview;

        var open = await _db.WorkTasks
            .Where(t => !t.IsDeleted
                && t.Type == reviewType
                && t.EntityType == "HKCard"
                && t.EntityId == cardId
                && t.HKStage == stage
                && (t.Status == WorkTaskStatus.Open
                    || t.Status == WorkTaskStatus.InProgress
                    || t.Status == WorkTaskStatus.Overdue))
            .ToListAsync(ct);

        await CloseTasksAsync(open, actorUserId, comment, ct);
    }

    private async Task CloseTasksAsync(
        List<WorkTask> tasks, Guid actorUserId, string? comment, CancellationToken ct)
    {
        if (tasks.Count == 0) return;

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var task in tasks)
        {
            task.Status = WorkTaskStatus.Completed;
            task.CompletedAtUtc = now;
            task.CompletedByUserId = actorUserId.ToString();
            task.CompletionComment = string.IsNullOrWhiteSpace(comment) ? null : comment;
            task.UpdatedAtUtc = now;

            await _audit.LogAsync(new AuditWriteRequest(
                "WorkTask", task.Id.ToString(), "Task.Completed", actorUserId,
                EntityDisplayName: task.Title,
                Details: $"Задача закрыта решением по этапу согласования. "
                         + (string.IsNullOrWhiteSpace(comment) ? string.Empty : comment.Trim())), ct);
        }
    }

    /// <summary>
    /// Задача доработки первоначальному автору. Отдельная от задачи
    /// рассмотрения и от любых задач пересчёта ИК.
    /// </summary>
    private async Task CreateAuthorRevisionTaskAsync(
        HKCard card, HKApprovalStage stage, string title, string? comment, CancellationToken ct)
    {
        // Автор хранится как Guid?, а задачи адресуются по ключу Identity (text).
        if (!card.AuthorId.HasValue) return;

        var authorId = card.AuthorId.Value.ToString();

        // Повторная отправка на доработку не должна создавать вторую открытую
        // задачу тому же автору по той же карте.
        var alreadyOpen = await _db.WorkTasks.AnyAsync(t => !t.IsDeleted
            && t.Type == WorkTaskType.HKRevision
            && t.EntityType == "HKCard"
            && t.EntityId == card.Id
            && t.AssignedToUserId == authorId
            && (t.Status == WorkTaskStatus.Open
                || t.Status == WorkTaskStatus.InProgress
                || t.Status == WorkTaskStatus.Overdue), ct);

        if (alreadyOpen) return;

        await _tasks.CreateFromWorkflowAsync(new CreateWorkflowTaskCommand(
            Title: $"Доработка ХК: {card.Code} v{card.Version}",
            Type: WorkTaskType.HKRevision,
            Priority: WorkTaskPriority.Normal,
            Description: string.IsNullOrWhiteSpace(comment) ? title : $"{title} {comment}",
            AssignedToUserId: authorId,
            BranchId: card.BranchId,
            EntityType: "HKCard",
            EntityId: card.Id,
            EntityCodeSnapshot: card.Code,
            EntityTitleSnapshot: card.Name,
            NotifyAssignee: true,
            HKStage: stage), ct: ct);
    }
}
