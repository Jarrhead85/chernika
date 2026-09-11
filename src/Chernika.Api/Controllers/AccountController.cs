using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly AccountService _account;

    public AccountController(AccountService account) => _account = account;

    [HttpGet, Route("api/account/me")]
    public async Task<ActionResult<MyAccountDto>> Me(CancellationToken ct)
    {
        try
        {
            return Ok(await _account.GetMyAccountAsync(ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPut, Route("api/account/me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateMyProfileRequest request, CancellationToken ct)
    {
        try
        {
            await _account.UpdateMyProfileAsync(request, ct);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost, Route("api/account/me/change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangeMyPasswordRequest request, CancellationToken ct)
    {
        try
        {
            await _account.ChangeMyPasswordAsync(request, ct);
            return Ok(new { message = "Пароль успешно изменён." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost, Route("api/account/me/avatar")]
    public async Task<IActionResult> UploadAvatar(
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        if (file?.OpenReadStream() is not { } stream)
            return BadRequest(new { error = "Файл не выбран." });

        try
        {
            await _account.UploadMyAvatarAsync(
                file.OpenReadStream(),
                file.FileName,
                file.ContentType,
                file.Length,
                ct);
            return Ok(new { message = "Фото обновлено." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete, Route("api/account/me/avatar")]
    public async Task<IActionResult> DeleteAvatar(CancellationToken ct)
    {
        await _account.DeleteMyAvatarAsync(ct);
        return NoContent();
    }

    [HttpGet, Route("api/account/me/avatar/content")]
    public async Task<IActionResult> GetAvatarContent(CancellationToken ct)
    {
        var content = await _account.GetMyAvatarAsync(ct);
        if (content is null)
            return NotFound();
        return File(content.Content, content.ContentType, enableRangeProcessing: false);
    }

    [HttpPost, Route("api/users/{id}/reset-password")]
    public async Task<ActionResult<ResetUserPasswordResult>> ResetPassword(
        string id,
        [FromBody] ResetUserPasswordByAdminRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _account.ResetUserPasswordByAdminAsync(id, request, ct);
            return Ok(new { success = result.Success, error = result.Error });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }
}
