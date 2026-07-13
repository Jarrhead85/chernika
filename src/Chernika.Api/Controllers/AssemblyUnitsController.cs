using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AssemblyUnitsController : ControllerBase
{
    private readonly EquipmentService _equip;

    public AssemblyUnitsController(EquipmentService equip) => _equip = equip;

    [HttpGet]
    public async Task<ActionResult<List<AssemblyUnitDto>>> GetAll()
    {
        var units = await _equip.GetAssemblyUnitsAsync();
        return Ok(units.Select(AssemblyUnitMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AssemblyUnitDto>> GetById(Guid id)
    {
        var u = await _equip.GetAssemblyUnitAsync(id);
        if (u == null) return NotFound();
        return Ok(AssemblyUnitMapper.ToDto(u));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<AssemblyUnitDto>> Create([FromBody] CreateAssemblyUnitRequest request)
    {
        var unit = AssemblyUnitMapper.FromCreate(request);
        var created = await _equip.CreateAssemblyUnitAsync(unit);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, AssemblyUnitMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateAssemblyUnitRequest request)
    {
        var u = await _equip.GetAssemblyUnitAsync(id);
        if (u == null) return NotFound();
        AssemblyUnitMapper.ApplyUpdate(u, request);
        await _equip.UpdateAssemblyUnitAsync(u);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _equip.DeleteAssemblyUnitAsync(id)) return NotFound();
        return NoContent();
    }
}
