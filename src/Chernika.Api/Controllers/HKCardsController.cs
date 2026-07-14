using Chernika.Api.Contracts;
using Chernika.Domain;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class HKCardsController : ControllerBase
{
    private readonly HKCardService _hkCards;
    private readonly IAuthorizationService _authorization;

    public HKCardsController(
        HKCardService hkCards,
        IAuthorizationService authorization)
    {
        _hkCards = hkCards;
        _authorization = authorization;
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

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteHK")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var (success, error) = await _hkCards.DeleteAsync(id);
        if (!success) return BadRequest(error ?? "Невозможно удалить карточку в текущем статусе.");
        return NoContent();
    }
}

