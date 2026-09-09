using Chernika.Api.Contracts;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Reports;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

using DomainIndividualCardDetailDto = Chernika.Domain.Models.IndividualCardDetailDto;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class IndividualCardsController : ControllerBase
{
    private readonly IndividualCardService _cards;
    private readonly ReportService _reports;

    public IndividualCardsController(IndividualCardService cards, ReportService reports)
    {
        _cards = cards;
        _reports = reports;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<IndividualCardListItemDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? instanceId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var result = await _cards.GetPagedAsync(page, pageSize, instanceId);
        return Ok(new PagedResponse<IndividualCardListItemDto>(
            result.Items.Select(IndividualCardMapper.ToListItem).ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> GetPdf(Guid id, CancellationToken ct)
    {
        IndividualCardPdfFile? file;
        try
        {
            file = await _reports.GenerateIndividualCardPdfAsync(id, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }

        if (file is null)
            return NotFound();

        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileNameStar = file.FileName,
        }.ToString();
        return File(file.Content, "application/pdf");
    }

    [HttpGet("{id:guid}/xlsx")]
    public async Task<IActionResult> GetXlsx(Guid id, CancellationToken ct)
    {
        IndividualCardXlsxFile? file;
        try
        {
            file = await _reports.GenerateIndividualCardXlsxAsync(id, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }

        if (file is null)
            return NotFound();

        return File(
            file.Content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileDownloadName: file.FileName);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Chernika.Api.Contracts.IndividualCardDetailDto>> GetById(Guid id)
    {
        var card = await _cards.GetCardAsync(id);
        if (card == null) return NotFound();
        return Ok(IndividualCardMapper.ToDetail(card));
    }

    [HttpGet("instance/{instanceId}")]
    public async Task<ActionResult<List<IndividualCardListItemDto>>> GetByInstance(Guid instanceId)
    {
        var cards = await _cards.GetCardsByInstanceAsync(instanceId);
        return Ok(cards.Select(IndividualCardMapper.ToListItem).ToList());
    }

    // ── D2/D3: preflight and Draft workflow ───────────────────────────────

    [HttpPost("preflight")]
    public async Task<ActionResult<IndividualCardPreflightResult>> Preflight(
        [FromBody] IndividualCardPreflightRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _cards.BuildPreflightAsync(request, ct: ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("drafts")]
    public async Task<ActionResult<IndividualCardDraftDto>> CreateDraft(
        [FromBody] CreateIndividualCardDraftRequest request, CancellationToken ct)
    {
        try
        {
            var created = await _cards.CreateDraftAsync(request, ct);
            return CreatedAtAction(nameof(GetDraftById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpGet("drafts/{id:guid}")]
    public async Task<ActionResult<IndividualCardDraftDto>> GetDraftById(Guid id, CancellationToken ct)
    {
        var draft = await _cards.GetDraftByIdAsync(id, ct);
        if (draft is null) return NotFound();
        return Ok(draft);
    }

    [HttpPost("drafts/{id:guid}/refresh-sources")]
    public async Task<ActionResult<IndividualCardDraftDto>> RefreshDraftSources(
        Guid id, [FromBody] RefreshIndividualCardDraftSourcesRequest request, CancellationToken ct)
    {
        if (id != request.IndividualCardId)
            return BadRequest(new { message = "Идентификатор в маршруте не совпадает с телом запроса." });

        try
        {
            return Ok(await _cards.RefreshDraftSourcesAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("drafts/{id:guid}")]
    public async Task<ActionResult> DeleteDraft(Guid id, CancellationToken ct)
    {
        try
        {
            await _cards.DeleteDraftAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // ── D4: coefficients, calculation, Form ───────────────────────────────

    [HttpGet("drafts/{id:guid}/calculation")]
    public async Task<ActionResult<IndividualCardCalculationDto>> GetDraftCalculation(Guid id, CancellationToken ct)
    {
        var calculation = await _cards.GetDraftCalculationAsync(id, ct);
        if (calculation is null) return NotFound();
        return Ok(calculation);
    }

    [HttpGet("drafts/{id:guid}/coefficients")]
    public async Task<ActionResult<IReadOnlyList<CoefficientListItemDto>>> GetDraftCoefficients(
        Guid id, [FromQuery] string? searchText, CancellationToken ct)
    {
        try
        {
            return Ok(await _cards.GetWorkingCoefficientsForDraftSelectAsync(id, searchText, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("drafts/{id:guid}/recalculate")]
    public async Task<ActionResult<IndividualCardCalculationDto>> RecalculateDraft(
        Guid id, [FromBody] RecalculateIndividualCardDraftRequest request, CancellationToken ct)
    {
        if (id != request.IndividualCardId)
            return BadRequest(new { message = "Идентификатор в маршруте не совпадает с телом запроса." });

        try
        {
            return Ok(await _cards.RecalculateDraftAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("drafts/{id:guid}/form")]
    public async Task<ActionResult<IndividualCardCalculationDto>> FormDraft(
        Guid id, [FromBody] FormIndividualCardRequest request, CancellationToken ct)
    {
        if (id != request.IndividualCardId)
            return BadRequest(new { message = "Идентификатор в маршруте не совпадает с телом запроса." });

        try
        {
            return Ok(await _cards.FormDraftAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // ── D5: new version, comparison, archive ──────────────────────────────

    [HttpGet("{id:guid}/action-header")]
    public async Task<ActionResult<IndividualCardActionHeaderDto>> GetActionHeader(Guid id, CancellationToken ct)
    {
        var header = await _cards.GetIndividualCardActionHeaderAsync(id, ct);
        if (header is null) return NotFound();
        return Ok(header);
    }

    // ── D6: registry, detail, history (read-only) ─────────────────────────

    [HttpGet("registry")]
    public async Task<ActionResult<PagedResult<IndividualCardRegistryItemDto>>> GetRegistry(
        [FromQuery] string? searchText,
        [FromQuery] IndividualCardObjectLevel? objectLevel,
        [FromQuery] IndividualCardStatus? status,
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? createdFrom,
        [FromQuery] DateTime? createdTo,
        [FromQuery] bool onlyMine = false,
        [FromQuery] bool onlyWithNormativeGaps = false,
        [FromQuery] string sortBy = "CreatedAt",
        [FromQuery] bool sortDescending = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _cards.GetRegistryAsync(new IndividualCardRegistryQuery(
            searchText, objectLevel, status, branchId, createdFrom, createdTo,
            onlyMine, onlyWithNormativeGaps, sortBy, sortDescending,
            page, pageSize), ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}/detail")]
    public async Task<ActionResult<DomainIndividualCardDetailDto>> GetDetail(Guid id, CancellationToken ct)
    {
        var detail = await _cards.GetDetailAsync(id, ct);
        if (detail is null) return NotFound();
        return Ok(detail);
    }

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<IndividualCardVersionChainItemDto>>> GetHistory(Guid id, CancellationToken ct)
    {
        return Ok(await _cards.GetHistoryAsync(id, ct));
    }

    [HttpPost("{id:guid}/new-version/preflight")]
    public async Task<ActionResult<IndividualCardVersionComparisonDto>> NewVersionPreflight(
        Guid id, [FromBody] IndividualCardVersionPreflightRequest request, CancellationToken ct)
    {
        if (id != request.SourceIndividualCardId)
            return BadRequest(new { message = "Идентификатор в маршруте не совпадает с телом запроса." });

        try
        {
            return Ok(await _cards.BuildNewVersionComparisonAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/new-version")]
    public async Task<ActionResult<IndividualCardDraftDto>> CreateNewVersion(
        Guid id, [FromBody] CreateIndividualCardVersionRequest request, CancellationToken ct)
    {
        if (id != request.SourceIndividualCardId)
            return BadRequest(new { message = "Идентификатор в маршруте не совпадает с телом запроса." });

        try
        {
            var created = await _cards.CreateNewVersionAsync(request, ct);
            return CreatedAtAction(nameof(GetDraftById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<ActionResult> Archive(Guid id, [FromBody] ArchiveIndividualCardRequest request, CancellationToken ct)
    {
        if (id != request.IndividualCardId)
            return BadRequest(new { message = "Идентификатор в маршруте не совпадает с телом запроса." });

        try
        {
            await _cards.ArchiveIndividualCardAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("generate/{instanceId}")]
    [Authorize(Policy = "CreateIndividualCard")]
    public async Task<ActionResult<List<Chernika.Api.Contracts.IndividualCardDetailDto>>> GenerateForInstance(
        Guid instanceId,
        [FromBody] GenerateIndividualCardsRequest request)
    {
        try
        {
            var ids = request.CoefficientIds ?? [];
            var created = await _cards.GenerateCardsForInstanceAsync(instanceId, ids);
            return Ok(created.Select(IndividualCardMapper.ToDetail).ToList());
        }
        catch (InvalidOperationException ex)
        {
            // Legacy generation is locked in D2: preflight-based workflow replaces it.
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPatch("{id}/notes")]
    [Authorize(Policy = "CreateIndividualCard")]
    public async Task<ActionResult> UpdateNotes(Guid id, [FromBody] UpdateCardNotesRequest request)
    {
        if (!await _cards.UpdateNotesAsync(id, request.Notes))
            return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteIndividualCard")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _cards.DeleteCardAsync(id))
            return NotFound();
        return NoContent();
    }
}
