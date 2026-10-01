using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class CoefficientsController : ControllerBase
{
    private readonly CoefficientService _svc;

    public CoefficientsController(CoefficientService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<PagedResult<CoefficientListItemDto>>> GetPaged(
        [FromQuery] CoefficientListQuery query, CancellationToken ct)
    {
        var result = await _svc.GetCoefficientsAsync(query, ct);
        return Ok(result);
    }

    [HttpGet("select")]
    public async Task<ActionResult<List<CoefficientListItemDto>>> GetForSelect(
        [FromQuery] Guid? coefficientTypeId, CancellationToken ct)
        => Ok(await _svc.GetWorkingCoefficientsForSelectAsync(coefficientTypeId, ct));

    [HttpPost]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientListItemDto>> Create(
        [FromBody] CreateCoefficientRequest request, CancellationToken ct)
    {
        try
        {
            var created = await _svc.CreateCoefficientAsync(request, ct);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            // Справочник законсервирован вместе с модулем ИК: запрет операции —
            // контролируемый ответ, а не серверная ошибка (см. Individuals).
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CoefficientListItemDto>> GetById(Guid id, CancellationToken ct)
    {
        var item = await _svc.GetCoefficientByIdAsync(id, includeArchived: false, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientListItemDto>> Update(
        Guid id, [FromBody] UpdateCoefficientRequest request, CancellationToken ct)
    {
        if (id != request.Id)
            return BadRequest("Идентификатор в маршруте не совпадает с телом запроса.");

        try
        {
            var updated = await _svc.UpdateCoefficientAsync(request, ct);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Archive(Guid id, CancellationToken ct)
    {
        try
        {
            await _svc.ArchiveCoefficientAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Restore(Guid id, CancellationToken ct)
    {
        try
        {
            await _svc.RestoreCoefficientAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}
