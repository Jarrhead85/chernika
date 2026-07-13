using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class EquipmentModelsController : ControllerBase
{
    private readonly EquipmentService _equipService;

    public EquipmentModelsController(EquipmentService equipService) =>
        _equipService = equipService;

    [HttpGet]
    public async Task<ActionResult<List<EquipmentModelDto>>> GetAll()
    {
        var models = await _equipService.GetModelsAsync();
        return Ok(models.Select(EquipmentModelMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EquipmentModelDetailDto>> GetById(Guid id)
    {
        var model = await _equipService.GetModelWithDetailsAsync(id);
        if (model == null) return NotFound();
        return Ok(EquipmentModelMapper.ToDetail(model));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<EquipmentModelDto>> Create([FromBody] CreateEquipmentModelRequest request)
    {
        var model = EquipmentModelMapper.FromCreate(request);
        var created = await _equipService.CreateModelAsync(model);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, EquipmentModelMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateEquipmentModelRequest request)
    {
        var existing = await _equipService.GetModelAsync(id);
        if (existing == null) return NotFound();
        EquipmentModelMapper.ApplyUpdate(existing, request);
        var success = await _equipService.UpdateModelPropertiesAsync(id, existing);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var success = await _equipService.DeleteModelAsync(id);
        if (!success) return NotFound();
        return NoContent();
    }
}
