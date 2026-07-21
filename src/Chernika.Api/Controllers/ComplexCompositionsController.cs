using Chernika.Api.Contracts;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ComplexCompositionsController : ControllerBase
{
    public record ReturnToDraftRequest(string? Comment);
    public record ApproveRequest(string? Comment);

    private readonly EquipmentService _equip;

    public ComplexCompositionsController(EquipmentService equip) => _equip = equip;

    [HttpGet("by-complex/{complexId}")]
    public async Task<ActionResult<List<ComplexCompositionDto>>> GetByComplex(Guid complexId)
    {
        var comps = await _equip.GetComplexCompositionsAsync(complexId);
        return Ok(comps.Select(ComplexCompositionMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ComplexCompositionDto>> GetById(Guid id)
    {
        var comp = await _equip.GetComplexCompositionAsync(id);
        if (comp == null) return NotFound();
        return Ok(ComplexCompositionMapper.ToDto(comp));
    }

    [HttpPost]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ComplexCompositionDto>> Create([FromBody] CreateComplexCompositionRequest request)
    {
        var created = await _equip.CreateComplexCompositionAsync(request);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ComplexCompositionMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateComplexCompositionDraftRequest request)
    {
        var req = request with { Id = id };
        var success = await _equip.UpdateComplexCompositionDraftAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    // ── Items ───────────────────────────────────────────────────

    [HttpPost("{id}/items")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ComplexCompositionItemDto>> AddItem(Guid id, [FromBody] AddComplexCompositionItemRequest req)
    {
        var request = req with { CompositionId = id };
        var item = await _equip.AddComplexCompositionItemAsync(request);
        return CreatedAtAction(nameof(GetById), new { id }, ComplexCompositionMapper.ToItemDto(item));
    }

    [HttpPut("items/{itemId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> UpdateItem(Guid itemId, [FromBody] UpdateComplexCompositionItemRequest request)
    {
        var req = request with { Id = itemId };
        var success = await _equip.UpdateComplexCompositionItemAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("items/{itemId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> RemoveItem(Guid itemId)
    {
        var success = await _equip.RemoveComplexCompositionItemAsync(itemId);
        if (!success) return NotFound();
        return NoContent();
    }

    // ── Status transitions ──────────────────────────────────────

    [HttpPost("{id}/submit")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> SubmitForReview(Guid id)
    {
        if (!await _equip.SubmitComplexCompositionForReviewAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/return")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> ReturnToDraft(Guid id, [FromBody] ReturnToDraftRequest? req)
    {
        if (!await _equip.ReturnComplexCompositionToDraftAsync(id, req?.Comment)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/approve")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Approve(Guid id, [FromBody] ApproveRequest? req)
    {
        if (!await _equip.ApproveComplexCompositionAsync(id, req?.Comment)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/archive")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Archive(Guid id)
    {
        if (!await _equip.ArchiveComplexCompositionAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var success = await _equip.DeleteComplexCompositionDraftAsync(id);
        if (!success) return NotFound();
        return NoContent();
    }
}
