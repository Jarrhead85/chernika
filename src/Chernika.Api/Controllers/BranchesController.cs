using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class BranchesController : ControllerBase
{
    private readonly EquipmentService _equip;

    public BranchesController(EquipmentService equip) => _equip = equip;

    [HttpGet]
    public async Task<ActionResult<List<BranchDto>>> GetAll()
    {
        var branches = await _equip.GetBranchesAsync();
        return Ok(branches.Select(BranchMapper.ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<BranchDto>> GetById(Guid id)
    {
        var b = await _equip.GetBranchAsync(id);
        if (b == null) return NotFound();
        return Ok(BranchMapper.ToDto(b));
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<BranchDto>> Create([FromBody] CreateBranchRequest request)
    {
        var branch = BranchMapper.FromCreate(request);
        var created = await _equip.CreateBranchAsync(branch);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, BranchMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateBranchRequest request)
    {
        var b = await _equip.GetBranchAsync(id);
        if (b == null) return NotFound();
        BranchMapper.ApplyUpdate(b, request);
        await _equip.UpdateBranchAsync(b);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var (deleted, error) = await _equip.DeleteBranchAsync(id);
        if (!deleted) return error != null ? Conflict(new { Error = error }) : NotFound();
        return NoContent();
    }
}
