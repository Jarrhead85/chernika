using Chernika.Api.Contracts;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ProductCompositionsController : ControllerBase
{
    public record ReturnToDraftRequest(string? Comment);
    public record ApproveRequest(string? Comment);

    private readonly EquipmentService _equipService;

    public ProductCompositionsController(EquipmentService equipService) =>
        _equipService = equipService;

    [HttpGet]
    public async Task<ActionResult<List<CompositionVersionSummary>>> GetAll()
    {
        var comps = await _equipService.GetProductCompositionSummariesAsync();
        return Ok(comps);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductCompositionDto>> GetById(Guid id)
    {
        var comp = await _equipService.GetProductCompositionDetailAsync(id);
        if (comp == null) return NotFound();
        return Ok(ProductCompositionMapper.ToDetail(comp));
    }

    [HttpPost]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ProductCompositionDto>> Create([FromBody] CreateCompositionRequest request)
    {
        var created = await _equipService.CreateCompositionDraftAsync(request);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ProductCompositionMapper.ToDetail(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateCompositionDraftRequest request)
    {
        var req = request with { Id = id };
        var success = await _equipService.UpdateCompositionDraftAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    // ── Parts ───────────────────────────────────────────────────────────

    [HttpGet("parts/{partId}")]
    public async Task<ActionResult<ProductCompositionPartDto>> GetPartById(Guid partId)
    {
        var part = await _equipService.GetCompositionPartAsync(partId);
        if (part == null) return NotFound();
        return Ok(ProductCompositionMapper.ToPartDto(part));
    }

    [HttpPost("{id}/parts")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ProductCompositionPartDto>> AddPart(Guid id, [FromBody] AddPartRequest req)
    {
        var request = req with { CompositionId = id };
        var part = await _equipService.AddPartAsync(request);
        return CreatedAtAction(nameof(GetPartById), new { partId = part.Id }, ProductCompositionMapper.ToPartDto(part));
    }

    [HttpPut("parts/{partId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> UpdatePart(Guid partId, [FromBody] UpdatePartRequest request)
    {
        var req = request with { PartId = partId };
        var success = await _equipService.UpdatePartAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("parts/{partId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> RemovePart(Guid partId)
    {
        var success = await _equipService.RemovePartAsync(partId);
        if (!success) return NotFound();
        return NoContent();
    }

    // ── Aggregates ──────────────────────────────────────────────────────

    [HttpGet("aggregates/{id}")]
    public async Task<ActionResult<ProductCompositionAggregateDto>> GetAggregateById(Guid id)
    {
        var agg = await _equipService.GetCompositionAggregateAsync(id);
        if (agg == null) return NotFound();
        return Ok(ProductCompositionMapper.ToAggregateDto(agg));
    }

    [HttpPost("{compositionId}/aggregates")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ProductCompositionAggregateDto>> AddAggregate(Guid compositionId, [FromBody] AddProductCompositionAggregateRequest req)
    {
        var request = req with { ProductCompositionId = compositionId };
        var agg = await _equipService.AddAggregateAsync(request);
        return CreatedAtAction(nameof(GetAggregateById), new { id = agg.Id }, ProductCompositionMapper.ToAggregateDto(agg));
    }

    [HttpPost("aggregates/{id}/move")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> MoveAggregate(Guid id, [FromBody] MoveProductCompositionAggregateRequest req)
    {
        var request = req with { AggregateItemId = id };
        var success = await _equipService.MoveProductCompositionAggregateAsync(request.AggregateItemId, request.TargetPartId);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpPut("aggregates/{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> UpdateAggregate(Guid id, [FromBody] UpdateProductCompositionAggregateRequest req)
    {
        var request = req with { Id = id };
        var isActive = await _equipService.IsCompositionActiveByAggregateAsync(id);
        if (isActive)
            return BadRequest("Нельзя изменить количество агрегата в активном составе изделия. Создайте новую версию состава.");

        var success = await _equipService.UpdateAggregateQuantityAsync(request);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("aggregates/{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> RemoveAggregate(Guid id)
    {
        var success = await _equipService.RemoveAggregateAsync(id);
        if (!success) return NotFound();
        return NoContent();
    }

    // ── Status transitions ─────────────────────────────────────────────

    [HttpPost("{id}/submit")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> SubmitForReview(Guid id)
    {
        if (!await _equipService.SubmitForReviewAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/return")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> ReturnToDraft(Guid id, [FromBody] ReturnToDraftRequest? req)
    {
        if (!await _equipService.ReturnToDraftAsync(id, req?.Comment)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/approve")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Approve(Guid id, [FromBody] ApproveRequest? req)
    {
        if (!await _equipService.ApproveCompositionAsync(id, req?.Comment)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/archive")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Archive(Guid id)
    {
        if (!await _equipService.ArchiveCompositionAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var success = await _equipService.DeleteCompositionDraftAsync(id);
        if (!success) return NotFound();
        return NoContent();
    }
}
