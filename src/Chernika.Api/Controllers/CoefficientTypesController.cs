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
        [FromQuery] CoefficientTypeListQuery query, CancellationToken ct)
    {
        var result = await _svc.GetCoefficientTypesAsync(query, ct);
        return Ok(result);
    }

    [HttpGet("select")]
    public async Task<ActionResult<List<CoefficientTypeListItemDto>>> GetForSelect(CancellationToken ct)
        => Ok(await _svc.GetActiveCoefficientTypesForSelectAsync(ct));

    [HttpPost]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientTypeListItemDto>> Create(
        [FromBody] CreateCoefficientTypeRequest request, CancellationToken ct)
    {
        var created = await _svc.CreateCoefficientTypeAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CoefficientTypeListItemDto>> GetById(Guid id, CancellationToken ct)
    {
        var item = await _svc.GetCoefficientTypeByIdAsync(id, includeArchived: false, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientTypeListItemDto>> Update(
        Guid id, [FromBody] UpdateCoefficientTypeRequest request, CancellationToken ct)
    {
        if (id != request.Id)
            return BadRequest("Идентификатор в маршруте не совпадает с телом запроса.");

        var updated = await _svc.UpdateCoefficientTypeAsync(request, ct);
        return Ok(updated);
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Archive(Guid id, CancellationToken ct)
    {
        await _svc.ArchiveCoefficientTypeAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Restore(Guid id, CancellationToken ct)
    {
        await _svc.RestoreCoefficientTypeAsync(id, ct);
        return NoContent();
    }
}
