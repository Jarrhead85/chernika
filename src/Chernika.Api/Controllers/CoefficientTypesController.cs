using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class CoefficientTypesController : ControllerBase
{
    private readonly CoefficientService _svc;

    public CoefficientTypesController(CoefficientService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<PagedResult<CoefficientTypeListItemDto>>> GetPaged(
        [FromQuery] CoefficientTypeListQuery query)
    {
        var result = await _svc.GetCoefficientTypesAsync(query);
        return Ok(result);
    }

    [HttpGet("select")]
    public async Task<ActionResult<List<CoefficientTypeListItemDto>>> GetForSelect()
        => Ok(await _svc.GetActiveCoefficientTypesForSelectAsync());

    [HttpPost]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientTypeListItemDto>> Create([FromBody] CreateCoefficientTypeRequest request)
    {
        var created = await _svc.CreateCoefficientTypeAsync(request);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CoefficientTypeListItemDto>> GetById(Guid id)
    {
        var type = _svc.GetById(id);
        if (type == null) return NotFound();

        var all = await _svc.GetActiveCoefficientTypesForSelectAsync();
        var match = all.FirstOrDefault(t => t.Id == id);
        return match == null ? NotFound() : Ok(match);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientTypeListItemDto>> Update(Guid id, [FromBody] UpdateCoefficientTypeRequest request)
    {
        if (id != request.Id)
            return BadRequest("Идентификатор в маршруте не совпадает с телом запроса.");

        var updated = await _svc.UpdateCoefficientTypeAsync(request);
        return Ok(updated);
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Archive(Guid id)
    {
        await _svc.ArchiveCoefficientTypeAsync(id);
        return NoContent();
    }

    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Restore(Guid id)
    {
        await _svc.RestoreCoefficientTypeAsync(id);
        return NoContent();
    }
}
