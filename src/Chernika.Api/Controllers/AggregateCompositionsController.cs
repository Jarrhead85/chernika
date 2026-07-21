using Chernika.Api.Contracts;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AggregateCompositionsController : ControllerBase
{
    public record ReturnToDraftRequest(string? Comment);
    public record ApproveRequest(string? Comment);

    private readonly EquipmentService _equip;

    public AggregateCompositionsController(EquipmentService equip) => _equip = equip;

    [HttpGet("by-aggregate/{aggregateId}")]
    public async Task<ActionResult<List<AggregateCompositionDto>>> GetByAggregate(Guid aggregateId)
    {
        var comps = await _equip.GetAggregateCompositionsAsync(aggregateId);
        return Ok(comps.Select(AggregateCompositionMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AggregateCompositionDto>> GetById(Guid id)
    {
        var comp = await _equip.GetAggregateCompositionAsync(id);
        if (comp == null) return NotFound();
        return Ok(AggregateCompositionMapper.ToDto(comp));
    }

    [HttpPost]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<AggregateCompositionDto>> Create([FromBody] CreateAggregateCompositionRequest request)
    {
        var created = await _equip.CreateAggregateCompositionAsync(request);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, AggregateCompositionMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateAggregateCompositionDraftRequest request)
    {
        var req = request with { Id = id };
        var success = await _equip.UpdateAggregateCompositionDraftAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    // ── Nodes ───────────────────────────────────────────────────

    [HttpPost("{id}/nodes")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<AggregateCompositionNodeDto>> AddNode(Guid id, [FromBody] AddAggregateCompositionNodeRequest req)
    {
        var request = req with { AggregateCompositionId = id };
        var node = await _equip.AddAggregateCompositionNodeAsync(request);
        return CreatedAtAction(nameof(GetById), new { id }, AggregateCompositionMapper.ToNodeDto(node));
    }

    [HttpPut("nodes/{nodeId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> UpdateNode(Guid nodeId, [FromBody] UpdateAggregateCompositionNodeRequest request)
    {
        var req = request with { Id = nodeId };
        var success = await _equip.UpdateAggregateCompositionNodeAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("nodes/{nodeId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> RemoveNode(Guid nodeId)
    {
        var success = await _equip.RemoveAggregateCompositionNodeAsync(nodeId);
        if (!success) return NotFound();
        return NoContent();
    }

    // ── Status transitions ──────────────────────────────────────

    [HttpPost("{id}/submit")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> SubmitForReview(Guid id)
    {
        if (!await _equip.SubmitAggregateCompositionForReviewAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/return")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> ReturnToDraft(Guid id, [FromBody] ReturnToDraftRequest? req)
    {
        if (!await _equip.ReturnAggregateCompositionToDraftAsync(id, req?.Comment)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/approve")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Approve(Guid id, [FromBody] ApproveRequest? req)
    {
        if (!await _equip.ApproveAggregateCompositionAsync(id, req?.Comment)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/archive")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Archive(Guid id)
    {
        if (!await _equip.ArchiveAggregateCompositionAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var success = await _equip.DeleteAggregateCompositionDraftAsync(id);
        if (!success) return NotFound();
        return NoContent();
    }
}
