using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class EquipmentInstancesController : ControllerBase
{
    private readonly EquipmentService _equipment;

    public EquipmentInstancesController(EquipmentService equipment) => _equipment = equipment;

    [HttpGet]
    public async Task<ActionResult<PagedResponse<EquipmentInstanceDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var result = await _equipment.GetInstancesPagedAsync(page, pageSize);
        return Ok(new PagedResponse<EquipmentInstanceDto>(
            result.Items.Select(EquipmentMapper.ToDto).ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EquipmentInstanceDto>> GetById(Guid id)
    {
        var inst = await _equipment.GetInstanceAsync(id);
        if (inst == null) return NotFound();
        return Ok(EquipmentMapper.ToDto(inst));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<EquipmentInstanceDto>> Create([FromBody] CreateEquipmentInstanceRequest request)
    {
        var inst = EquipmentMapper.FromCreate(request);
        var created = await _equipment.CreateInstanceAsync(inst);
        var loaded = await _equipment.GetInstanceAsync(created.Id);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, EquipmentMapper.ToDto(loaded!));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateEquipmentInstanceRequest request)
    {
        var inst = await _equipment.GetInstanceAsync(id);
        if (inst == null) return NotFound();
        EquipmentMapper.ApplyUpdate(inst, request);
        await _equipment.UpdateInstanceAsync(inst);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _equipment.DeleteInstanceAsync(id))
            return NotFound();
        return NoContent();
    }
}
