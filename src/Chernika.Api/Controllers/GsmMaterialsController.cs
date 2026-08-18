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
    private readonly GsmMaterialService _gsmService;

    public GsmMaterialsController(GsmMaterialService gsmService) => _gsmService = gsmService;

    [HttpGet]
    public async Task<ActionResult<List<GsmMaterialDto>>> GetAll()
    {
        var materials = await _gsmService.GetActiveForSelectionAsync();
        return Ok(materials.Select(GsmMaterialMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<GsmMaterialDto>> GetById(Guid id)
    {
        var m = await _gsmService.GetByIdAsync(id);
        if (m == null) return NotFound();
        return Ok(GsmMaterialMapper.ToDto(m));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<GsmMaterialDto>> Create([FromBody] CreateGsmMaterialRequest request)
    {
        var material = GsmMaterialMapper.FromCreate(request);
        var created = await _gsmService.CreateAsync(material);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, GsmMaterialMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateGsmMaterialRequest request)
    {
        var m = await _gsmService.GetByIdAsync(id);
        if (m == null) return NotFound();
        GsmMaterialMapper.ApplyUpdate(m, request);
        await _gsmService.UpdateAsync(m);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _gsmService.DeleteAsync(id)) return NotFound();
        return NoContent();
    }
}
