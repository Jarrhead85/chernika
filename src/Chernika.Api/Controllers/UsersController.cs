using Chernika.Api.Contracts;
using Chernika.Domain;
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
            request.UserName, request.Password, request.FullName, request.Position, request.Role, request.BranchId);
        if (!success) return BadRequest(new { Error = error });
        return Ok(new { Message = "Пользователь создан" });
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "ManageUsers")]
    public async Task<ActionResult> Update(string id, [FromBody] UpdateUserRequest request)
    {
        var (success, error) = await _users.UpdateUserAsync(id, request.FullName, request.Position, request.Role, request.BranchId);
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

    [HttpPost("{id}/delete")]
    [Authorize(Policy = "ManageUsers")]
    public async Task<ActionResult> Delete(string id, [FromBody] DeleteUserRequest request)
    {
        var (success, error) = await _users.DeleteUserAsync(id, request.Reason);
        if (!success) return BadRequest(new { Error = error });
        return NoContent();
    }

    [HttpPost("{id}/restore")]
    [Authorize(Policy = "ManageUsers")]
    public async Task<ActionResult> Restore(string id, [FromBody] RestoreUserRequest request)
    {
        var (success, error) = await _users.RestoreUserAsync(id, request.Role, request.BranchId);
        if (!success) return BadRequest(new { Error = error });
        return NoContent();
    }

    [HttpGet("{id}/effective-permissions")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult<UserEffectivePermissionsDto>> GetEffectivePermissions(string id)
    {
        var result = await _users.GetEffectivePermissionsAsync(id);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("{id}/grant")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult<UserEffectivePermissionsDto>> GrantPermission(string id, [FromBody] PermissionOverrideRequest request)
    {
        var (result, error) = await _users.GrantPermissionAsync(id, request.PermissionCode, request.Reason);
        if (error != null) return BadRequest(new { Error = error });
        return Ok(result);
    }

    [HttpPost("{id}/deny")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult<UserEffectivePermissionsDto>> DenyPermission(string id, [FromBody] PermissionOverrideRequest request)
    {
        var (result, error) = await _users.DenyPermissionAsync(id, request.PermissionCode, request.Reason);
        if (error != null) return BadRequest(new { Error = error });
        return Ok(result);
    }

    [HttpPost("{id}/revoke")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult<UserEffectivePermissionsDto>> RevokePermission(string id, [FromQuery] string code)
    {
        var (result, error) = await _users.RevokePermissionAsync(id, code);
        if (error != null) return BadRequest(new { Error = error });
        return Ok(result);
    }

    [HttpGet("{id}/overrides")]
    [Authorize(Policy = "ManageRoles")]
    public async Task<ActionResult<List<UserOverrideDto>>> GetOverrides(string id)
    {
        var overrides = await _users.GetOverridesAsync(id);
        return Ok(overrides.Select(UserMapper.ToDto).ToList());
    }
}
