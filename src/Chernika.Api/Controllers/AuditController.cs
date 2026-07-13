using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize(Policy = "ViewAuditLog")]
[Route("api/[controller]")]
public class AuditController : ControllerBase
{
    private readonly AuditService _audit;

    public AuditController(AuditService audit) => _audit = audit;

    [HttpGet]
    public async Task<ActionResult<PagedResponse<AuditLogDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var logs = await _audit.GetLogsAsync(page, pageSize);
        var total = await _audit.GetTotalCountAsync();
        var totalPages = (int)Math.Ceiling((double)total / pageSize);
        return Ok(new PagedResponse<AuditLogDto>(
            logs.Select(AuditLogMapper.ToDto).ToList(),
            total, page, pageSize, totalPages));
    }

    [HttpGet("by-entity")]
    public async Task<ActionResult<List<AuditLogDto>>> GetByEntity(
        [FromQuery] string entityType,
        [FromQuery] string entityId)
    {
        var logs = await _audit.GetLogsByEntityAsync(entityType, entityId);
        return Ok(logs.Select(AuditLogMapper.ToDto).ToList());
    }
}
