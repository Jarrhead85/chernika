using Chernika.Api.Contracts;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly UserManagementService _users;

    public UsersController(UserManagementService users) => _users = users;

    [HttpGet]
    [Authorize(Policy = "ManageUsers")]
    public async Task<ActionResult<List<UserDto>>> GetAll()
    {
        var users = await _users.GetUsersAsync();
        return Ok(users.Select(UserMapper.ToDto).ToList());
    }

    [HttpPost]
    [Authorize(Policy = "ManageUsers")]
    public async Task<ActionResult> Create([FromBody] CreateUserRequest request)
    {
        var (success, error) = await _users.CreateUserAsync(
            request.UserName, request.Password, request.FullName, request.Position, request.Role);
        if (!success) return BadRequest(new { Error = error });
        return Ok(new { Message = "Пользователь создан" });
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageUsers")]
    public async Task<ActionResult> Update(string id, [FromBody] UpdateUserRequest request)
    {
        var (success, error) = await _users.UpdateUserAsync(id, request.FullName, request.Position, request.Role);
        if (!success) return BadRequest(new { Error = error });
        return NoContent();
    }

    [HttpPost("{id}/toggle-block")]
    [Authorize(Policy = "ManageUsers")]
    public async Task<ActionResult> ToggleBlock(string id)
    {
        var (success, error) = await _users.ToggleBlockAsync(id);
        if (!success) return BadRequest(new { Error = error });
        return NoContent();
    }

    [HttpGet("{id}/overrides")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult<List<UserOverrideDto>>> GetOverrides(string id)
    {
        var overrides = await _users.GetOverridesAsync(id);
        return Ok(overrides.Select(UserMapper.ToDto).ToList());
    }

    [HttpPost("{id}/overrides")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult> SetOverride(string id, [FromBody] SetOverrideRequest request)
    {
        var (success, error) = await _users.SetOverrideAsync(id, request.PermissionCode, request.IsGranted, request.Reason);
        if (!success) return BadRequest(new { Error = error });
        return NoContent();
    }

    [HttpDelete("{id}/overrides")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult> RemoveOverride(string id, [FromQuery] string code)
    {
        var (success, error) = await _users.RemoveOverrideAsync(id, code);
        if (!success) return BadRequest(new { Error = error });
        return NoContent();
    }
}
