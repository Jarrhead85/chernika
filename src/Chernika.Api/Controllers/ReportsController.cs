using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    private readonly HKCardService _hkCards;
    private readonly ReportService _reports;

    public ReportsController(HKCardService hkCards, ReportService reports)
    {
        _hkCards = hkCards;
        _reports = reports;
    }

    [HttpGet("hk-registry")]
    public async Task<ActionResult> ExportHKRegistry(
        [FromQuery] HKCardStatus? status = null,
        [FromQuery] Guid? branchId = null)
    {
        var cards = await _hkCards.GetFilteredForExportAsync(status, branchId);
        var query = cards.AsQueryable();
        var stream = await _reports.GenerateHKRegistryExcelAsync(query);
        return File(stream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "hk-registry.xlsx");
    }
}
