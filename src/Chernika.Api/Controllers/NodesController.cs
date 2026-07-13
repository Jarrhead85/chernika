using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class NodesController : ControllerBase
{
    private readonly EquipmentService _equip;

    public NodesController(EquipmentService equip) => _equip = equip;

    [HttpGet]
    public async Task<ActionResult<List<NodeDto>>> GetAll()
    {
        var nodes = await _equip.GetNodesAsync();
        return Ok(nodes.Select(NodeMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<NodeDto>> GetById(Guid id)
    {
        var node = await _equip.GetNodeAsync(id);
        if (node == null) return NotFound();
        return Ok(NodeMapper.ToDto(node));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<NodeDto>> Create([FromBody] CreateNodeRequest request)
    {
        var node = NodeMapper.FromCreate(request);
        var created = await _equip.CreateNodeAsync(node);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, NodeMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateNodeRequest request)
    {
        var existing = await _equip.GetNodeAsync(id);
        if (existing == null) return NotFound();
        NodeMapper.ApplyUpdate(existing, request);
        await _equip.UpdateNodeAsync(existing);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _equip.DeleteNodeAsync(id)) return NotFound();
        return NoContent();
    }
}
