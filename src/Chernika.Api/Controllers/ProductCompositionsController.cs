using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ProductCompositionsController : ControllerBase
{
    private readonly EquipmentService _equipService;

    public ProductCompositionsController(EquipmentService equipService) =>
        _equipService = equipService;

    public record AddPartRequest(string Name, int SortOrder = 0, string? Description = null);
    public record AddNodeRequest(Guid NodeId, int Quantity);
    public record UpdateNodeRequest(int Quantity);

    [HttpGet]
    public async Task<ActionResult<List<ProductCompositionDto>>> GetAll()
    {
        var comps = await _equipService.GetCompositionsAsync();
        return Ok(comps.Select(ProductCompositionMapper.ToDetail).ToList());
    }

    [HttpGet("parts/{partId}")]
    public async Task<ActionResult<ProductCompositionPartDto>> GetPartById(Guid partId)
    {
        var part = await _equipService.GetCompositionPartAsync(partId);
        if (part == null) return NotFound();
        return Ok(ProductCompositionMapper.ToPartDto(part));
    }

    [HttpGet("nodes/{nodeId}")]
    public async Task<ActionResult<ProductCompositionNodeDto>> GetNodeById(Guid nodeId)
    {
        var node = await _equipService.GetCompositionNodeAsync(nodeId);
        if (node == null) return NotFound();
        return Ok(ProductCompositionMapper.ToNodeDto(node));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductCompositionDto>> GetById(Guid id)
    {
        var comp = await _equipService.GetCompositionAsync(id);
        if (comp == null) return NotFound();
        return Ok(ProductCompositionMapper.ToDetail(comp));
    }

    [HttpPost]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ProductCompositionDto>> Create([FromBody] CreateProductCompositionRequest request)
    {
        var comp = ProductCompositionMapper.FromCreate(request);
        var created = await _equipService.CreateCompositionAsync(comp);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ProductCompositionMapper.ToDetail(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Update(Guid id, [FromBody] CreateProductCompositionRequest request)
    {
        var success = await _equipService.UpdateCompositionPropertiesAsync(id, request.EquipmentModelId, request.Comment);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/activate")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Activate(Guid id)
    {
        if (!await _equipService.SetActiveCompositionAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/parts")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ProductCompositionPartDto>> AddPart(Guid id, [FromBody] AddPartRequest req)
    {
        var part = await _equipService.AddPartAsync(id, req.Name, req.SortOrder, req.Description);
        return CreatedAtAction(
            nameof(GetPartById),
            new { partId = part.Id },
            ProductCompositionMapper.ToPartDto(part));
    }

    [HttpPost("parts/{partId}/nodes")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult<ProductCompositionNodeDto>> AddNode(Guid partId, [FromBody] AddNodeRequest req)
    {
        var node = await _equipService.AddNodeAsync(partId, req.NodeId, req.Quantity);
        return CreatedAtAction(
            nameof(GetNodeById),
            new { nodeId = node.Id },
            ProductCompositionMapper.ToNodeDto(node));
    }

    [HttpPut("nodes/{nodeId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> UpdateNode(Guid nodeId, [FromBody] UpdateNodeRequest req)
    {
        var isActive = await _equipService.IsCompositionActiveByNodeAsync(nodeId);
        if (isActive)
            return BadRequest("Нельзя изменить количество узла в активном составе изделия. Создайте новую версию состава.");

        var success = await _equipService.UpdateNodeQuantityAsync(nodeId, req.Quantity);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("nodes/{nodeId}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> RemoveNode(Guid nodeId)
    {
        var success = await _equipService.RemoveNodeAsync(nodeId);
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

    [HttpDelete("{id}")]
    [Authorize(Policy = "ManageComposition")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var success = await _equipService.DeleteCompositionAsync(id);
        if (!success) return NotFound();
        return NoContent();
    }
}
