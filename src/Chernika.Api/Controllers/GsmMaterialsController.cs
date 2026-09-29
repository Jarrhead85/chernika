using Chernika.Api.Contracts;
using Chernika.Domain;
using Chernika.Domain.Models;
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
        return Ok(materials.Select(m => GsmMaterialMapper.ToDto(new GsmMaterialEditView
        {
            Id = m.Id,
            Name = m.Name,
            Nd = m.Nd,
            InGostNomenclature = m.InGostNomenclature,
            IntendedUse = m.IntendedUse,
            SuitabilityGround = m.SuitabilityGround,
            SuitabilityAir = m.SuitabilityAir,
            SuitabilitySea = m.SuitabilitySea,
            NatoIndex = m.NatoIndex,
            Note = m.Note,
        })).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<GsmMaterialDto>> GetById(Guid id)
    {
        var m = await _gsmService.GetEditViewAsync(id);
        if (m == null) return NotFound();
        return Ok(GsmMaterialMapper.ToDto(m));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<GsmMaterialDto>> Create([FromBody] GsmMaterialWriteApiRequest request)
    {
        var created = await _gsmService.CreateAsync(GsmMaterialMapper.ToWriteRequest(request));
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, GsmMaterialMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] GsmMaterialWriteApiRequest request)
    {
        var updated = await _gsmService.UpdateAsync(id, GsmMaterialMapper.ToWriteRequest(request));
        if (updated == null) return NotFound();
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
