using Chernika.Api.Contracts;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ComplexesController : ControllerBase
{
    private readonly EquipmentService _equip;

    public ComplexesController(EquipmentService equip) => _equip = equip;

    [HttpGet]
    public async Task<ActionResult<List<ComplexDto>>> GetAll()
    {
        var complexes = await _equip.GetComplexesAsync();
        return Ok(complexes.Select(ComplexMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ComplexDto>> GetById(Guid id)
    {
        var c = await _equip.GetComplexAsync(id);
        if (c == null) return NotFound();
        return Ok(ComplexMapper.ToDto(c));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<ComplexDto>> Create([FromBody] CreateComplexApiRequest request)
    {
        var req = new CreateComplexRequest(request.Code, request.Name, request.Description);
        var created = await _equip.CreateComplexAsync(req);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ComplexMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateComplexApiRequest request)
    {
        var req = new UpdateComplexRequest(id, request.Code, request.Name, request.Description);
        var success = await _equip.UpdateComplexAsync(req);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var (deleted, error) = await _equip.DeleteComplexAsync(id);
        if (!deleted) return error != null ? BadRequest(error) : NotFound();
        return NoContent();
    }
}
