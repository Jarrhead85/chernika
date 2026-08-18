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
    private readonly IAuthorizationService _authorization;
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IPermissionService _permissions;
    private readonly ICurrentUserService _currentUser;
    private readonly IOptions<FileStorageOptions> _fileOptions;

    public HKCardsController(
        HKCardService hkCards,
        IAuthorizationService authorization,
        AppDbContext db,
        IFileStorageService fileStorage,
        IPermissionService permissions,
        ICurrentUserService currentUser,
        IOptions<FileStorageOptions> fileOptions)
    {
        _hkCards = hkCards;
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
    public async Task<ActionResult<HKCardDetailDto>> GetById(Guid id)
    {
        var card = await _hkCards.GetByIdAsync(id);
        if (card == null) return NotFound();
        return Ok(HKCardMapper.ToDetail(card));
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
    public async Task<ActionResult<object>> GetAttachment(Guid id)
    {
        var card = await _hkCards.GetByIdAsync(id);
        if (card == null) return NotFound();
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
    public async Task<IActionResult> GetAttachmentContent(Guid id, [FromQuery] bool inline = false)
    {
        if (!await _permissions.HasPermissionAsync(_currentUser.GetRequiredUserId().ToString(), PermissionCodes.HKView))
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

