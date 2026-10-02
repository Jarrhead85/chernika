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
        // Справочник марок в целом, а не выбор для строк ХК: переходный фильтр
        // «ровно одна подгруппа» (GetActiveForSelectionAsync) здесь скрыл бы
        // legacy-марки без классификации и марки с несколькими подгруппами.
        var materials = await _gsmService.GetSelectableForRelationAsync();
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
        try
        {
            var created = await _gsmService.CreateAsync(GsmMaterialMapper.ToWriteRequest(request));
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, GsmMaterialMapper.ToDto(created));
        }
        catch (InvalidOperationException ex)
        {
            // Отказ по правилам марки (пустое имя, предел НД, группа без
            // подгруппы) — не серверная ошибка. Тот же дефект, что был найден в
            // контроллере связей: без перехвата клиент получал 500 с text/plain.
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] GsmMaterialWriteApiRequest request)
    {
        try
        {
            var updated = await _gsmService.UpdateAsync(id, GsmMaterialMapper.ToWriteRequest(request));
            if (updated == null) return NotFound();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        try
        {
            if (!await _gsmService.DeleteAsync(id)) return NotFound();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}
