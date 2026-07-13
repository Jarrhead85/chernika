using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class CoefficientsController : ControllerBase
{
    private readonly IndividualCardService _svc;

    public CoefficientsController(IndividualCardService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<List<CoefficientDto>>> GetAll()
    {
        var coefficients = await _svc.GetAllCoefficientsAsync();
        return Ok(coefficients.Select(CoefficientMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CoefficientDto>> GetById(Guid id)
    {
        var c = await _svc.GetCoefficientAsync(id);
        if (c == null) return NotFound();
        return Ok(CoefficientMapper.ToDto(c));
    }

    [HttpPost]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult<CoefficientDto>> Create([FromBody] CreateCoefficientRequest request)
    {
        var coefficient = CoefficientMapper.FromCreate(request);
        var created = await _svc.CreateCoefficientAsync(coefficient);
        var loaded = await _svc.GetCoefficientAsync(created.Id);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, CoefficientMapper.ToDto(loaded!));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateCoefficientRequest request)
    {
        var c = await _svc.GetCoefficientAsync(id);
        if (c == null) return NotFound();
        CoefficientMapper.ApplyUpdate(c, request);
        await _svc.UpdateCoefficientAsync(c);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "ManageCoefficients")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _svc.DeleteCoefficientAsync(id)) return NotFound();
        return NoContent();
    }
}
