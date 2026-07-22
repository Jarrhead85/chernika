using Chernika.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "SystemConfig")]
public class SecurityController : ControllerBase
{
    private readonly ISecurityDataRepairService _repairService;

    public SecurityController(ISecurityDataRepairService repairService)
    {
        _repairService = repairService;
    }

    [HttpGet("diagnostics")]
    public async Task<IActionResult> GetDiagnostics(CancellationToken ct)
    {
        var diagnostics = await _repairService.GetDiagnosticsAsync(ct);
        return Ok(diagnostics);
    }

    [HttpPost("repair")]
    public async Task<IActionResult> Repair(CancellationToken ct)
    {
        var result = await _repairService.RepairAsync(ct);
        return Ok(result);
    }
}
