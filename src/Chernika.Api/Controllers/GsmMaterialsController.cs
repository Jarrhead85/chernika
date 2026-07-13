using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class GsmMaterialsController : ControllerBase
{
    private readonly EquipmentService _equip;

    public GsmMaterialsController(EquipmentService equip) => _equip = equip;

    [HttpGet]
    public async Task<ActionResult<List<GsmMaterialDto>>> GetAll()
    {
        var materials = await _equip.GetGsmMaterialsAsync();
        return Ok(materials.Select(GsmMaterialMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<GsmMaterialDto>> GetById(Guid id)
    {
        var m = await _equip.GetGsmMaterialAsync(id);
        if (m == null) return NotFound();
        return Ok(GsmMaterialMapper.ToDto(m));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<GsmMaterialDto>> Create([FromBody] CreateGsmMaterialRequest request)
    {
        var material = GsmMaterialMapper.FromCreate(request);
        var created = await _equip.CreateGsmMaterialAsync(material);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, GsmMaterialMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateGsmMaterialRequest request)
    {
        var m = await _equip.GetGsmMaterialAsync(id);
        if (m == null) return NotFound();
        GsmMaterialMapper.ApplyUpdate(m, request);
        await _equip.UpdateGsmMaterialAsync(m);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _equip.DeleteGsmMaterialAsync(id)) return NotFound();
        return NoContent();
    }
}
