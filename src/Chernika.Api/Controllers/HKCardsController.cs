using Chernika.Api.Contracts;
using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class HKCardsController : ControllerBase
{
    private readonly HKCardService _hkCards;
    private readonly HKApprovalService _approvals;
    private readonly HKCardRevisionService _revisions;
    private readonly IAuthorizationService _authorization;
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IPermissionService _permissions;
    private readonly ICurrentUserService _currentUser;
    private readonly IOptions<FileStorageOptions> _fileOptions;

    public HKCardsController(
        HKCardService hkCards,
        HKApprovalService approvals,
        HKCardRevisionService revisions,
        IAuthorizationService authorization,
        AppDbContext db,
        IFileStorageService fileStorage,
        IPermissionService permissions,
        ICurrentUserService currentUser,
        IOptions<FileStorageOptions> fileOptions)
    {
        _hkCards = hkCards;
        _approvals = approvals;
        _revisions = revisions;
        _authorization = authorization;
        _db = db;
        _fileStorage = fileStorage;
        _permissions = permissions;
        _currentUser = currentUser;
        _fileOptions = fileOptions;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<HKCardListItemDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] HKCardStatus? status = null,
        [FromQuery] Guid? branchId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var result = await _hkCards.GetPagedAsync(page, pageSize, status, branchId);
        return Ok(new PagedResponse<HKCardListItemDto>(
            result.Items.ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "ViewHK")]
    public async Task<ActionResult<HKCardDetailDto>> GetById(Guid id)
    {
        var card = await _hkCards.GetByIdAsync(id);
        if (card == null) return NotFound();
        var versions = await _hkCards.GetVersionsAsync(id);
        // Проекция уровня — read-only: GET не пересчитывает и не записывает.
        var level = await _hkCards.GetLevelProjectionAsync(id);
        return Ok(HKCardMapper.ToDetail(card, versions, level));
    }

    [HttpPost]
    [Authorize(Policy = "CreateHK")]
    public async Task<ActionResult<HKCardDetailDto>> Create([FromBody] CreateHKCardRequest request)
    {
        var card = HKCardMapper.FromCreate(request);
        try
        {
            var created = await _hkCards.CreateAsync(card);
            var loaded = await _hkCards.GetByIdAsync(created.Id);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, HKCardMapper.ToDetail(loaded!));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (HKCardValidationException ex)
        {
            return BadRequest(ex.ToUserMessage());
        }
        catch (InvalidOperationException ex)
        {
            // Конфликт номера при параллельном создании и прочие отказы создания —
            // это сообщение пользователю, а не внутренняя ошибка сервера.
            return Conflict(ex.Message);
        }
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditHK")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateHKCardRequest request)
    {
        var card = await _hkCards.GetByIdAsync(id);
        if (card == null) return NotFound();
        if (card.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            return BadRequest("Редактирование доступно только для черновика или карты на доработке.");

        HKCardMapper.ApplyUpdate(card, request);
        try
        {
            await _hkCards.UpdateAsync(card);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        catch (HKCardValidationException ex)
        {
            return BadRequest(ex.ToUserMessage());
        }
        return NoContent();
    }

    /// <summary>
    /// Специальный выбор уровня для карты, старшая верхняя позиция которой —
    /// узел.
    /// <para>
    /// Принимается только выбранный допустимый вариант (агрегат или изделие) и
    /// ожидаемая версия. Выбор и вычисленный уровень сохраняются атомарно.
    /// Фактический верхний уровень проверяется по сохранённой структуре: если он
    /// не узел, выбор неприменим.
    /// </para>
    /// </summary>
    [HttpPost("{id}/node-root-level")]
    [Authorize(Policy = "EditHK")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetNodeRootLevel(
        Guid id, [FromBody] SetNodeRootCardLevelRequest request,
        CancellationToken ct)
    {
        try
        {
            await _hkCards.SetNodeRootCardLevelAsync(
                id, request.Choice, request.RowVersion, ct);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
        catch (HkCardVersionConflictException ex)
        {
            return Conflict(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id}/status")]
    public async Task<ActionResult> ChangeStatus(Guid id, [FromBody] StatusChangeRequest request)
    {
        var policy = request.NewStatus switch
        {
            HKCardStatus.OnReview => "SendToApprove",
            HKCardStatus.Approved => "ApproveHK",
            HKCardStatus.RevisionRequired => "ReturnHK",
            HKCardStatus.Deleted => "DeleteHK",
            HKCardStatus.Archived => "ArchiveHK",
            _ => null
        };

        if (policy != null)
        {
            var authResult = await _authorization.AuthorizeAsync(User, policy);
            if (!authResult.Succeeded)
                return Forbid();
        }

        var (success, error) = await _hkCards.ChangeStatusAsync(
            id, request.NewStatus, request.Comment);

        if (!success)
            return BadRequest(error);

        return NoContent();
    }

    // ── Этапы согласования ──
    // Отдельные команды вместо общего переключателя статуса: этап согласуется
    // адресно, а общий статус вычисляется из состояний этапов и клиентом не
    // устанавливается.

    // Чтение этапов НЕ создаёт их: GET-запрос не меняет состояние системы.
    /// <summary>
    /// Последняя принятая структура карты — read-only.
    /// <para>
    /// Источник — указатель <c>CurrentAcceptedRevisionId</c>, а не последняя
    /// созданная редакция. Отсутствие принятой структуры возвращается явным
    /// 204: подстановка рабочего дерева означала бы, что черновик выдан за
    /// принятую карту.
    /// </para>
    /// <para>
    /// Проверки выполняет сервис: HK.View, активность пользователя, филиал и
    /// принадлежность редакции карте. GET ничего не создаёт и не переключает.
    /// </para>
    /// </summary>
    [HttpGet("{id}/accepted-structure")]
    [ProducesResponseType(typeof(HKAcceptedStructureDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HKAcceptedStructureDto>> GetAcceptedStructure(
        Guid id, CancellationToken ct)
    {
        try
        {
            var accepted = await _revisions.GetAcceptedAsync(id, ct);

            // Принятой структуры ещё нет — это не ошибка и не пустой объект.
            if (accepted is null)
                return NoContent();

            return Ok(accepted);
        }
        catch (HkCardAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (ArgumentException ex) when (IsCardMissing(ex))
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Конкретная сохранённая редакция карты — read-only.
    /// <para>
    /// Редакция чужой карты не раскрывается: пользователь без доступа к карте
    /// получает 404, как при отсутствии редакции.
    /// </para>
    /// </summary>
    [HttpGet("{id}/accepted-structure/{revisionId}")]
    [ProducesResponseType(typeof(HKAcceptedStructureDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HKAcceptedStructureDto>> GetAcceptedRevision(
        Guid id, Guid revisionId, CancellationToken ct)
    {
        try
        {
            var revision = await _revisions.GetRevisionAsync(id, revisionId, ct);
            return revision is null ? NoContent() : Ok(revision);
        }
        catch (HkCardAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (ArgumentException ex) when (IsCardMissing(ex))
        {
            // Включает «редакция не найдена» — принадлежность карте не раскрывается.
            return NotFound();
        }
    }

    [HttpGet("{id}/approvals")]
    public async Task<ActionResult<List<HKApprovalStageDto>>> GetApprovals(Guid id)
    {
        try
        {
            var stages = await _approvals.GetStagesAsync(id);
            return Ok(stages.Select(ToStageDto).ToList());
        }
        catch (HkCardAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Отказ доступа приходит разными типами: назначенный отказ доступа к
            // карте и отказ по правам. Наружу оба должны выглядеть одинаково —
            // иначе отказ превращается в 500.
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (ArgumentException ex) when (IsCardMissing(ex))
        {
            // Отсутствие карты — 404. Без этой ветки необработанное исключение
            // превратилось бы в 500 с внутренним текстом.
            return NotFound();
        }
    }

    [HttpGet("{id}/editing-capabilities")]
    public async Task<ActionResult<HKCardEditingCapabilitiesDto>> GetEditingCapabilities(Guid id)
    {
        try
        {
            var caps = await _approvals.GetEditingCapabilitiesAsync(id);
            return Ok(new HKCardEditingCapabilitiesDto(
                caps.CanEditDetails, caps.CanEditComposition, caps.CanEditPdf));
        }
        catch (HkCardAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Отказ доступа приходит разными типами: назначенный отказ доступа к
            // карте и отказ по правам. Наружу оба должны выглядеть одинаково —
            // иначе отказ превращается в 500.
            return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (ArgumentException ex) when (IsCardMissing(ex))
        {
            return NotFound();
        }
    }

    [HttpPost("{id}/approvals/submit")]
    public async Task<ActionResult> SubmitApprovalStage(
        Guid id, [FromBody] SubmitApprovalStageRequest request)
    {
        if (!TryParseStage(request.Stage, out var stage)) return BadRequest("Неизвестный этап.");

        try
        {
            if (stage == HKApprovalStage.Details)
                await _approvals.SubmitDetailsAsync(
                    id, request.ApproverUserId ?? "", request.RowVersion);
            else
                await _approvals.SubmitCompositionAsync(id, request.RowVersion);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or UnauthorizedAccessException
                                      or HkCardVersionConflictException)
        {
            return ToStageError(ex);
        }
        catch (HKCardValidationException ex)
        {
            return BadRequest(ex.ToUserMessage());
        }

        return NoContent();
    }

    [HttpPost("{id}/approvals/decision")]
    public async Task<ActionResult> DecideApprovalStage(
        Guid id, [FromBody] ApprovalStageDecisionRequest request)
    {
        if (!TryParseStage(request.Stage, out var stage)) return BadRequest("Неизвестный этап.");
        if (!TryParseDecision(request.Decision, out var decision))
            return BadRequest("Неизвестное решение: ожидается approve или return.");

        try
        {
            if (stage == HKApprovalStage.Details)
            {
                if (decision == ApprovalDecision.Approve)
                    await _approvals.ApproveDetailsAsync(id, request.Comment, request.RowVersion);
                else
                    await _approvals.ReturnDetailsAsync(id, request.Comment, request.RowVersion);
            }
            else
            {
                if (decision == ApprovalDecision.Approve)
                    // Окончательное принятие состава закрыто до появления
                    // хранилища принятой структуры.
                    await _approvals.ApproveCompositionAsync(id, request.Comment, request.RowVersion);
                else
                    await _approvals.ReturnCompositionAsync(id, request.Comment, request.RowVersion);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or UnauthorizedAccessException
                                      or HkCardVersionConflictException)
        {
            return ToStageError(ex);
        }

        return NoContent();
    }

    /// <summary>
    /// Отличает «карта не найдена» от других ошибок входных данных: по тексту
    /// сообщения, а не по типу, потому что тип ArgumentException используется
    /// для обеих ситуаций.
    /// </summary>
    private static bool IsCardMissing(Exception ex) =>
        ex.Message.Contains("не найдена", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Единое соглашение проекта по кодам ответа для команд этапов:
    /// 403 — отказ доступа, 404 — карта не найдена, 409 — конфликт версии или
    /// конкурентного решения, 400 — недопустимая команда или ошибка входа.
    /// <para>
    /// Тексты внутренних ошибок PostgreSQL наружу не выдаются: они содержат
    /// имена таблиц, ограничений и внутренних типов и не относятся к предметной
    /// области ответа.
    /// </para>
    /// </summary>
    private ActionResult ToStageError(Exception ex) => ex switch
    {
        HkCardAccessDeniedException denied
            => StatusCode(StatusCodes.Status403Forbidden, denied.Message),
        UnauthorizedAccessException denied
            => StatusCode(StatusCodes.Status403Forbidden, denied.Message),
        HkCardVersionConflictException conflict
            => Conflict(conflict.Message),
        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException
            => Conflict("Карточка была изменена другим пользователем. Обновите страницу и повторите попытку."),
        ArgumentException arg when arg.Message.Contains("не найдена", StringComparison.OrdinalIgnoreCase)
            => NotFound(),
        _ => BadRequest(ex.Message),
    };

    private static bool TryParseStage(string? value, out HKApprovalStage stage)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "details":
            case "реквизиты":
                stage = HKApprovalStage.Details;
                return true;
            case "composition":
            case "состав":
                stage = HKApprovalStage.Composition;
                return true;
            default:
                stage = HKApprovalStage.Details;
                return false;
        }
    }

    private static bool TryParseDecision(string? value, out ApprovalDecision decision)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "approve":
                decision = ApprovalDecision.Approve;
                return true;
            case "return":
                decision = ApprovalDecision.Return;
                return true;
            default:
                decision = ApprovalDecision.Approve;
                return false;
        }
    }

    private static HKApprovalStageDto ToStageDto(PersistedStageState approval) => new(
        StageKey(approval.Stage),
        StageTitle(approval.Stage),
        approval.State.ToString(),
        StateTitle(approval.State),
        approval.AssignedApproverUserId,
        approval.ApprovedAtUtc);

    internal static string StageKey(HKApprovalStage stage) =>
        stage == HKApprovalStage.Details ? "details" : "composition";

    internal static string StageTitle(HKApprovalStage stage) =>
        stage == HKApprovalStage.Details
            ? HKApprovalService.DetailsStageTitle
            : HKApprovalService.CompositionStageTitle;

    internal static string StateTitle(HKApprovalStageState state) => state switch
    {
        HKApprovalStageState.Draft => "Черновик",
        HKApprovalStageState.OnReview => "На рассмотрении",
        HKApprovalStageState.RevisionRequired => "Возвращено на доработку",
        HKApprovalStageState.Approved => "Принято",
        _ => state.ToString()
    };

    [HttpGet("{id}/components")]
    public async Task<ActionResult<List<HKCardComponentDto>>> GetComponents(Guid id)
    {
        var components = await _hkCards.GetComponentsAsync(id);
        return Ok(components);
    }

    [HttpGet("{id}/parent-components")]
    public async Task<ActionResult<List<HKCardComponentDto>>> GetParentComponents(Guid id)
    {
        var components = await _hkCards.GetParentComponentsAsync(id);
        return Ok(components);
    }

    [HttpGet("{id}/aggregated-rows")]
    public async Task<ActionResult<List<AggregatedRowDto>>> GetAggregatedRows(Guid id)
    {
        try
        {
            var rows = await _hkCards.GetAggregatedRowsAsync(id);
            return Ok(rows);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id}/components")]
    public async Task<ActionResult> AddComponent(Guid id, [FromBody] AddComponentRequest request)
    {
        try
        {
            await _hkCards.AddComponentAsync(id, request.ChildCardId);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpDelete("components/{componentId}")]
    public async Task<ActionResult> RemoveComponent(Guid componentId)
    {
        try
        {
            await _hkCards.RemoveComponentAsync(componentId);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteHK")]
    public async Task<ActionResult> Delete(Guid id, [FromBody] DeleteHKCardRequest request)
    {
        var (success, error) = await _hkCards.DeleteAsync(id, request.Reason);
        if (!success) return BadRequest(error ?? "Невозможно удалить карточку в текущем статусе.");
        return NoContent();
    }

    [HttpGet("{id}/attachment")]
    [Authorize(Policy = "HKAttachmentView")]
    public async Task<ActionResult<object>> GetAttachment(Guid id)
    {
        // Доступ проверяет сервис: права на вложение и организация карточки.
        // Раньше контроллер читал вложение прямо из контекста, и организация не
        // проверялась вовсе — метаданные вложения чужой организации отдавались
        // любому пользователю с правом просмотра ХК.
        if (await _hkCards.GetAttachmentInfoAsync(id) is null) return NotFound();

        var attachment = await _db.HKCardAttachments.FirstOrDefaultAsync(a => a.HKCardId == id);
        if (attachment == null) return NotFound();
        return Ok(new
        {
            attachment.Id,
            attachment.OriginalFileName,
            attachment.ContentType,
            SizeBytes = attachment.SizeBytes,
            attachment.UploadedByUserName,
            attachment.UploadedAt
        });
    }

    [HttpPost("{id}/attachment")]
    [Authorize(Policy = "HKAttachmentEdit")]
    public async Task<ActionResult> UploadAttachment(Guid id, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("Файл не выбран.");

        var card = await _hkCards.GetByIdAsync(id);
        if (card == null) return NotFound();

        if (card.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            return BadRequest("Загрузка вложения доступна только для черновика или карты на доработке.");

        var actorId = _currentUser.GetRequiredUserId();
        var actor = await _db.Users.FirstOrDefaultAsync(u => u.Id == actorId.ToString());
        if (actor == null) return Unauthorized();

        var existingAttachment = await _db.HKCardAttachments.FirstOrDefaultAsync(a => a.HKCardId == id);
        if (existingAttachment != null)
        {
            await _fileStorage.DeleteAsync(existingAttachment.StorageKey);
            _db.HKCardAttachments.Remove(existingAttachment);
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                EntityType = "HKCardAttachment",
                EntityId = existingAttachment.Id.ToString(),
                Action = "Deleted",
                UserId = actorId,
                CreatedAt = DateTime.UtcNow,
                EntityDisplayName = $"{card.Code} v{card.Version} — {existingAttachment.OriginalFileName}"
            });
        }

        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Допускается только PDF-формат.");

        var maxBytes = _fileOptions.Value.MaxPdfSizeBytes > 0
            ? _fileOptions.Value.MaxPdfSizeBytes
            : 20L * 1024 * 1024;
        if (file.Length > maxBytes)
            return BadRequest($"Размер файла не должен превышать {FormatFileSize(maxBytes)}.");

        if (!string.IsNullOrEmpty(file.ContentType)
            && !IsPdfContentType(file.ContentType))
            return BadRequest("Недопустимый тип содержимого файла (ожидается PDF).");

        await using var stream = file.OpenReadStream();

        var firstBytes = new byte[5];
        var read = await stream.ReadAsync(firstBytes.AsMemory(0, 5));
        stream.Position = 0;
        if (read < 5 || firstBytes[0] != 0x25 || firstBytes[1] != 0x50 || firstBytes[2] != 0x44 || firstBytes[3] != 0x46 || firstBytes[4] != 0x2D)
            return BadRequest("Файл не является корректным PDF (неверная сигнатура).");

        var storageKey = $"hk/{id}/{Guid.NewGuid():N}.pdf";
        var result = await _fileStorage.SaveAsync(stream, storageKey);

        var attachment = new HKCardAttachment
        {
            Id = Guid.NewGuid(),
            HKCardId = id,
            OriginalFileName = Path.GetFileName(file.FileName),
            StorageKey = result.StorageKey,
            ContentType = "application/pdf",
            SizeBytes = result.SizeBytes,
            Sha256 = result.Sha256,
            UploadedByUserId = actorId.ToString(),
            UploadedByUserName = actor.FullName ?? actor.UserName,
            UploadedAt = DateTime.UtcNow
        };

        _db.HKCardAttachments.Add(attachment);
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCardAttachment",
            EntityId = attachment.Id.ToString(),
            Action = "Created",
            UserId = actorId,
            CreatedAt = DateTime.UtcNow,
            EntityDisplayName = $"{card.Code} v{card.Version} — {attachment.OriginalFileName}"
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            await _fileStorage.DeleteAsync(storageKey);
            throw;
        }

        return Ok(new { attachment.Id, attachment.OriginalFileName, attachment.SizeBytes, attachment.Sha256 });
    }

    [HttpGet("{id}/attachment/content")]
    [Authorize(Policy = "HKAttachmentView")]
    public async Task<IActionResult> GetAttachmentContent(Guid id, [FromQuery] bool inline = false)
    {
        // Проверку прав и организации поручаем сервису. Раньше здесь стояла
        // собственная проверка одного лишь HK.View, из-за чего организация не
        // проверялась: пользователь с правом просмотра ХК выдавал себе PDF чужой
        // организации. Политика на эндпоинте — дополнительный уровень, а не
        // замена проверки сервиса.
        var accessible = await _hkCards.OpenAttachmentAsync(id);
        if (accessible is null)
            return Forbid();

        var attachment = await _db.HKCardAttachments.FirstOrDefaultAsync(a => a.HKCardId == id);
        if (attachment == null) return NotFound();

        var stream = await _fileStorage.OpenReadAsync(attachment.StorageKey);
        if (inline)
        {
            Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
            {
                FileName = attachment.OriginalFileName,
                FileNameStar = attachment.OriginalFileName
            }.ToString();
            return File(stream, attachment.ContentType, enableRangeProcessing: true);
        }
        return File(stream, attachment.ContentType, attachment.OriginalFileName, enableRangeProcessing: true);
    }

    [HttpDelete("{id}/attachment")]
    [Authorize(Policy = "HKAttachmentEdit")]
    public async Task<ActionResult> DeleteAttachment(Guid id)
    {
        var card = await _hkCards.GetByIdAsync(id);
        if (card == null) return NotFound();

        if (card.Status is not (HKCardStatus.Draft or HKCardStatus.RevisionRequired))
            return BadRequest("Удаление вложения доступно только для черновика или карты на доработке.");

        var attachment = await _db.HKCardAttachments.FirstOrDefaultAsync(a => a.HKCardId == id);
        if (attachment == null) return NotFound();

        var storageKey = attachment.StorageKey;
        _db.HKCardAttachments.Remove(attachment);

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityType = "HKCardAttachment",
            EntityId = attachment.Id.ToString(),
            Action = "Deleted",
            UserId = _currentUser.GetRequiredUserId(),
            CreatedAt = DateTime.UtcNow,
            EntityDisplayName = $"{card.Code} v{card.Version} — {attachment.OriginalFileName}"
        });

        await _db.SaveChangesAsync();
        await _fileStorage.DeleteAsync(storageKey);

        return NoContent();
    }

    [HttpPost("{id}/versions")]
    [Authorize(Policy = "CreateHK")]
    public async Task<ActionResult<CreateNewHKCardVersionResponse>> CreateNewVersion(Guid id)
    {
        try
        {
            var (success, newCardId, error) = await _hkCards.CreateNewVersionAsync(id);
            if (!success || !newCardId.HasValue)
                return BadRequest(error ?? "Не удалось создать новую версию.");

            var loaded = await _hkCards.GetByIdAsync(newCardId.Value);
            return CreatedAtAction(nameof(GetById), new { id = newCardId.Value },
                new CreateNewHKCardVersionResponse(newCardId.Value));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpGet("{id}/versions")]
    [Authorize(Policy = "ViewHK")]
    public async Task<ActionResult<IReadOnlyList<HKCardVersionDto>>> GetVersions(Guid id)
    {
        var versions = await _hkCards.GetVersionsAsync(id);
        return Ok(versions);
    }

    [HttpPost("{id}/archive")]
    [Authorize(Policy = "ArchiveHK")]
    public async Task<ActionResult> Archive(Guid id, [FromBody] ArchiveHKCardRequest request)
    {
        var (success, error) = await _hkCards.ArchiveAsync(id, request.ReplacementCardId, request.Reason);
        if (!success)
            return BadRequest(error ?? "Не удалось заархивировать ХК.");
        return NoContent();
    }

    private static bool IsPdfContentType(string contentType)
    {
        return contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("application/x-pdf", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{(double)bytes / (1024 * 1024):0.##} МБ";
        if (bytes >= 1024)
            return $"{(double)bytes / 1024:0.#} КБ";
        return $"{bytes} Б";
    }
}

