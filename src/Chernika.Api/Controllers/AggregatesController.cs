using Chernika.Api.Contracts;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AggregatesController : ControllerBase
{
    private readonly EquipmentService _equip;

    public AggregatesController(EquipmentService equip) => _equip = equip;

    [HttpGet]
    public async Task<ActionResult<List<AggregateDto>>> GetAll()
    {
        var aggregates = await _equip.GetAggregatesAsync();
        return Ok(aggregates.Select(AggregateMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AggregateDto>> GetById(Guid id)
    {
        var a = await _equip.GetAggregateAsync(id);
        if (a == null) return NotFound();
        return Ok(AggregateMapper.ToDto(a));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<AggregateDto>> Create([FromBody] CreateAggregateApiRequest request)
    {
        var req = new CreateAggregateRequest(request.Code, request.Name, request.Description);
        var created = await _equip.CreateAggregateAsync(req);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, AggregateMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateAggregateApiRequest request)
    {
        var req = new UpdateAggregateRequest(id, request.Code, request.Name, request.Description);
        var success = await _equip.UpdateAggregateAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var (deleted, error) = await _equip.DeleteAggregateAsync(id);
        if (!deleted) return error != null ? BadRequest(error) : NotFound();
        return NoContent();
    }
}
