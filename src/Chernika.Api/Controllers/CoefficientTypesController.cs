using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class CoefficientTypesController : ControllerBase
{
    private readonly IndividualCardService _svc;

    public CoefficientTypesController(IndividualCardService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<List<CoefficientTypeDto>>> GetAll()
    {
        var types = await _svc.GetCoefficientTypesAsync();
        return Ok(types.Select(CoefficientTypeMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CoefficientTypeDto>> GetById(Guid id)
    {
        var t = await _svc.GetCoefficientTypeAsync(id);
        if (t == null) return NotFound();
        return Ok(CoefficientTypeMapper.ToDto(t));
    }

    [HttpPost]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientTypeDto>> Create([FromBody] CreateCoefficientTypeRequest request)
    {
        var type = CoefficientTypeMapper.FromCreate(request);
        var created = await _svc.CreateCoefficientTypeAsync(type);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, CoefficientTypeMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateCoefficientTypeRequest request)
    {
        var t = await _svc.GetCoefficientTypeAsync(id);
        if (t == null) return NotFound();
        CoefficientTypeMapper.ApplyUpdate(t, request);
        await _svc.UpdateCoefficientTypeAsync(t);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var (deleted, error) = await _svc.DeleteCoefficientTypeAsync(id);
        if (!deleted) return error != null ? Conflict(new { Error = error }) : NotFound();
        return NoContent();
    }
}
