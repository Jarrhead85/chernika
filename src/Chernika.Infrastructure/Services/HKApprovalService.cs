using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
        _time = time;
        _logger = logger;
    }

    public const string DetailsStageTitle = "Согласование реквизитов и основания ХК";
    public const string CompositionStageTitle = "Согласование состава и норм ХК";

    // ── Чтение состояния ──

    /// <summary>
    /// Возвращает состояния этапов карты, создавая отсутствующие записи.
    /// <para>
    /// Отсутствующие этапы — обычное состояние: карта может быть создана до
    /// появления модели согласования. Они создаются в состоянии
    /// <see cref="HKApprovalStageState.Draft"/> без фиктивных дат принятия:
    /// дата принятия выдумывать нельзя, её нет только после реального решения.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<HKCardApproval>> EnsureStagesAsync(
        Guid cardId, CancellationToken ct = default)
    {
        var existing = await _db.HKCardApprovals
            .Where(a => a.HKCardId == cardId)
            .ToListAsync(ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var added = false;

        foreach (var stage in new[] { HKApprovalStage.Details, HKApprovalStage.Composition })
        {
            if (existing.Any(a => a.Stage == stage)) continue;

            existing.Add(new HKCardApproval
            {
                Id = Guid.NewGuid(),
                HKCardId = cardId,
                Stage = stage,
                State = HKApprovalStageState.Draft,
                UpdatedAtUtc = now
            });
            added = true;
        }

        if (added) await _db.SaveChangesAsync(ct);
        return existing;
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
        IReadOnlyList<HKCardApproval> stages, HKCardStatus status, bool hasWorkPermission)
    {
        // Архивная и удалённая карта не редактируется ни в одной части.
        if (status is HKCardStatus.Archived or HKCardStatus.Deleted)
            return new HKCardEditingCapabilities(false, false, false);

        var details = FindStage(stages, HKApprovalStage.Details);
        var composition = FindStage(stages, HKApprovalStage.Composition);

        var detailsEditable = hasWorkPermission
                              && details is not null
                              && details.State is HKApprovalStageState.Draft
                                  or HKApprovalStageState.RevisionRequired;

        var compositionEditable = hasWorkPermission
                                  && composition is not null
                                  && composition.State is HKApprovalStageState.Draft
                                      or HKApprovalStageState.RevisionRequired;

        return new HKCardEditingCapabilities(
            detailsEditable,
            compositionEditable,
            detailsEditable);
    }

    private static HKCardApproval? FindStage(IReadOnlyList<HKCardApproval> stages, HKApprovalStage stage) =>
        stages.FirstOrDefault(s => s.Stage == stage);

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
    public async Task SubmitDetailsAsync(
        Guid cardId, string approverUserId, uint expectedRowVersion, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKSubmit, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для отправки ХК на согласование.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var card = await LoadCardForDecisionAsync(cardId, ct);
        var stages = (await EnsureStagesAsync(cardId, ct)).ToList();
        var details = FindStage(stages, HKApprovalStage.Details)!;

        EnsureCardRowVersion(card, expectedRowVersion);
        EnsureCardNotTerminal(card);

        if (details.State == HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Реквизиты уже отправлены на согласование.");
        if (details.State == HKApprovalStageState.Approved)
            throw new InvalidOperationException("Реквизиты уже приняты.");

        await EnsureApproverUsableAsync(approverUserId, card, PermissionCodes.HKApprove, ct);

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
            NotifyAssignee: true), ct: ct);

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
    public async Task ApproveDetailsAsync(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKApprove, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для принятия реквизитов.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var card = await LoadCardForDecisionAsync(cardId, ct);
        var stages = (await EnsureStagesAsync(cardId, ct)).ToList();
        var details = FindStage(stages, HKApprovalStage.Details)!;

        EnsureCardRowVersion(card, expectedRowVersion);
        EnsureCardNotTerminal(card);
        EnsureAssignedApprover(details, actorId.ToString());

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

        await CloseStageTasksAsync(card.Id, WorkTaskType.HKDetailsReview, actorId, comment, ct);

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
    public async Task ReturnDetailsAsync(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKReview, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для возврата реквизитов.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var card = await LoadCardForDecisionAsync(cardId, ct);
        var stages = (await EnsureStagesAsync(cardId, ct)).ToList();
        var details = FindStage(stages, HKApprovalStage.Details)!;

        EnsureCardRowVersion(card, expectedRowVersion);
        EnsureCardNotTerminal(card);
        EnsureAssignedApprover(details, actorId.ToString());

        if (details.State != HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Вернуть реквизиты можно только когда они на рассмотрении.");

        var now = _time.GetUtcNow().UtcDateTime;
        details.State = HKApprovalStageState.RevisionRequired;
        details.UpdatedAtUtc = now;
        card.Status = DeriveOverallStatus(stages);
        card.UpdatedAt = now;

        await CloseStageTasksAsync(card.Id, WorkTaskType.HKDetailsReview, actorId, comment, ct);
        await CreateAuthorRevisionTaskAsync(card, "Реквизиты возвращены на доработку.", comment, ct);

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
    public async Task SubmitCompositionAsync(
        Guid cardId, uint expectedRowVersion, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKSubmit, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для отправки состава на согласование.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var card = await LoadCardForDecisionAsync(cardId, ct);
        var stages = (await EnsureStagesAsync(cardId, ct)).ToList();
        var details = FindStage(stages, HKApprovalStage.Details)!;
        var composition = FindStage(stages, HKApprovalStage.Composition)!;

        EnsureCardRowVersion(card, expectedRowVersion);
        EnsureCardNotTerminal(card);

        if (details.State != HKApprovalStageState.Approved)
            throw new InvalidOperationException(
                "Отправить состав на согласование можно только после принятия реквизитов.");

        if (composition.State == HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Состав уже отправлен на согласование.");

        if (string.IsNullOrWhiteSpace(details.AssignedApproverUserId))
            throw new InvalidOperationException(
                "Не найден назначенный согласующий: отправьте реквизиты на согласование.");

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
            NotifyAssignee: true), ct: ct);

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCard", card.Id.ToString(), "CompositionSubmitted", actorId,
            EntityDisplayName: $"{card.Code} v{card.Version}",
            Details: "Состав и нормы отправлены на согласование."), ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Возвращает состав на доработку. Данные не удаляются.</summary>
    public async Task ReturnCompositionAsync(
        Guid cardId, string? comment, uint expectedRowVersion, CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKReview, ct))
            throw new UnauthorizedAccessException("Недостаточно прав для возврата состава.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var card = await LoadCardForDecisionAsync(cardId, ct);
        var stages = (await EnsureStagesAsync(cardId, ct)).ToList();
        var composition = FindStage(stages, HKApprovalStage.Composition)!;

        EnsureCardRowVersion(card, expectedRowVersion);
        EnsureCardNotTerminal(card);
        EnsureAssignedApprover(composition, actorId.ToString());

        if (composition.State != HKApprovalStageState.OnReview)
            throw new InvalidOperationException("Вернуть состав можно только когда он на рассмотрении.");

        var now = _time.GetUtcNow().UtcDateTime;
        composition.State = HKApprovalStageState.RevisionRequired;
        composition.UpdatedAtUtc = now;
        card.Status = DeriveOverallStatus(stages);
        card.UpdatedAt = now;

        await CloseStageTasksAsync(card.Id, WorkTaskType.HKCompositionReview, actorId, comment, ct);
        await CreateAuthorRevisionTaskAsync(card, "Состав возвращён на доработку.", comment, ct);

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
        throw new InvalidOperationException(
            "Окончательное принятие состава пока недоступно: для него требуется хранилище " +
            "последней принятой структуры. Реализуется на следующем этапе.");

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
    /// </summary>
    private static void EnsureCardRowVersion(HKCard card, uint expectedRowVersion)
    {
        if (expectedRowVersion == 0)
            throw new InvalidOperationException(
                "Версия карточки не указана. Обновите страницу и повторите попытку.");

        if (card.RowVersion != expectedRowVersion)
            throw new InvalidOperationException(
                "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
    }

    private static void EnsureCardNotTerminal(HKCard card)
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
        string approverUserId, HKCard card, string requiredPermission, CancellationToken ct)
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
    private async Task CloseStageTasksAsync(
        Guid cardId, WorkTaskType type, Guid actorUserId, string? comment, CancellationToken ct)
    {
        var open = await _db.WorkTasks
            .Where(t => !t.IsDeleted
                && t.Type == type
                && t.EntityType == "HKCard"
                && t.EntityId == cardId
                && (t.Status == WorkTaskStatus.Open
                    || t.Status == WorkTaskStatus.InProgress
                    || t.Status == WorkTaskStatus.Overdue))
            .ToListAsync(ct);

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var task in open)
        {
            task.Status = WorkTaskStatus.Completed;
            task.CompletedAtUtc = now;
            task.CompletedByUserId = actorUserId.ToString();
            task.CompletionComment = string.IsNullOrWhiteSpace(comment) ? null : comment;
            task.UpdatedAtUtc = now;
        }
    }

    /// <summary>
    /// Задача доработки первоначальному автору. Отдельная от задачи
    /// рассмотрения и от любых задач пересчёта ИК.
    /// </summary>
    private async Task CreateAuthorRevisionTaskAsync(
        HKCard card, string title, string? comment, CancellationToken ct)
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
            NotifyAssignee: true), ct: ct);
    }
}