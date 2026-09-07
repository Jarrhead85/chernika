using Chernika.Api.Contracts;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class IndividualCardsController : ControllerBase
{
    private readonly IndividualCardService _cards;

    public IndividualCardsController(IndividualCardService cards) => _cards = cards;

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

    [HttpGet("{id}")]
    public async Task<ActionResult<IndividualCardDetailDto>> GetById(Guid id)
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
            return Ok(await _cards.BuildPreflightAsync(request, ct));
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

    [HttpPost("generate/{instanceId}")]
    [Authorize(Policy = "CreateIndividualCard")]
    public async Task<ActionResult<List<IndividualCardDetailDto>>> GenerateForInstance(
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
