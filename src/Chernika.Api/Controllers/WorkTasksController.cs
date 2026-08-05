using Chernika.Api.Contracts;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
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
    public async Task<ActionResult<List<WorkTaskListItemDto>>> GetActive([FromQuery] int limit = 100)
    {
        var result = await _tasks.GetMyTasksAsync(new WorkTaskQuery
        {
            Page = 1,
            PageSize = Math.Clamp(limit, 1, 200)
        });
        return Ok(result.Items
            .Where(t => t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled)
            .ToList());
    }

    [HttpGet("completed")]
    public async Task<ActionResult<List<WorkTaskListItemDto>>> GetCompleted([FromQuery] int limit = 20)
    {
        var result = await _tasks.GetMyTasksAsync(new WorkTaskQuery
        {
            Page = 1,
            PageSize = Math.Clamp(limit, 1, 200),
            Status = WorkTaskStatus.Completed
        });
        return Ok(result.Items.ToList());
    }

    [HttpGet("count")]
    public async Task<ActionResult<int>> GetActiveCount()
    {
        return Ok(await _tasks.GetOpenTaskCountAsync());
    }

    [HttpPost]
    public async Task<ActionResult<WorkTaskDto>> Create([FromBody] CreateWorkTaskRequest request)
    {
        var created = await _tasks.CreateAsync(new CreateWorkTaskCommand(
            Title: request.Title,
            Type: (WorkTaskType)request.Type,
            Priority: (WorkTaskPriority)request.Priority,
            Description: request.Description,
            AssignedToUserId: request.AssignedToUserId,
            AssignedRole: request.AssignedRole,
            EntityType: request.EntityType,
            EntityId: request.EntityId,
            DueDateUtc: request.DueDateUtc,
            NotifyAssignee: true));
        return Ok(created);
    }

    [HttpPut("{id}/complete")]
    public async Task<ActionResult> Complete(Guid id, [FromBody] CompleteWorkTaskRequest? request = null)
    {
        await _tasks.CompleteAsync(new CompleteWorkTaskCommand(id, request?.Comment));
        return NoContent();
    }

    [HttpPost("{id}/start")]
    public async Task<ActionResult<WorkTaskDto>> Start(Guid id)
    {
        var dto = await _tasks.StartAsync(id);
        return Ok(dto);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<WorkTaskDto>> GetById(Guid id)
    {
        var dto = await _tasks.GetByIdAsync(id);
        if (dto == null)
            return NotFound();
        return Ok(dto);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(Guid id)
    {
        await _tasks.CancelAsync(new CancelWorkTaskCommand(id, "Удалено через API"));
        return NoContent();
    }
}
