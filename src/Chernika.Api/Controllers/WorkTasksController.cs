using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class WorkTasksController : ControllerBase
{
    private readonly TaskService _tasks;

    public WorkTasksController(TaskService tasks) => _tasks = tasks;

    [HttpGet]
    public async Task<ActionResult<List<WorkTaskDto>>> GetActive([FromQuery] string? assigneeId = null)
    {
        var tasks = await _tasks.GetActiveTasksAsync(assigneeId);
        return Ok(tasks.Select(WorkTaskMapper.ToDto).ToList());
    }

    [HttpGet("completed")]
    public async Task<ActionResult<List<WorkTaskDto>>> GetCompleted(
        [FromQuery] string? assigneeId = null,
        [FromQuery] int limit = 20)
    {
        var tasks = await _tasks.GetCompletedTasksAsync(assigneeId, Math.Clamp(limit, 1, 200));
        return Ok(tasks.Select(WorkTaskMapper.ToDto).ToList());
    }

    [HttpGet("count")]
    public async Task<ActionResult<int>> GetActiveCount([FromQuery] string? assigneeId = null)
    {
        return Ok(await _tasks.GetActiveCountAsync(assigneeId));
    }

    [HttpPost]
    [Authorize(Policy = "ViewTasks")]
    public async Task<ActionResult<WorkTaskDto>> Create([FromBody] CreateWorkTaskRequest request)
    {
        var created = await _tasks.CreateTaskAsync(
            request.Title, request.AssigneeId, request.Description,
            request.EntityType, request.EntityId, request.DueDate);
        return Ok(WorkTaskMapper.ToDto(created));
    }

    [HttpPut("{id}/complete")]
    [Authorize(Policy = "ViewTasks")]
    public async Task<ActionResult> Complete(Guid id)
    {
        if (!await _tasks.CompleteTaskAsync(id)) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "ViewTasks")]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (!await _tasks.DeleteTaskAsync(id)) return NotFound();
        return NoContent();
    }
}
