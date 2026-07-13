using System.Security.Claims;
using Microsoft.AspNetCore.Http;

using Chernika.Domain;

namespace Chernika.Infrastructure.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public Guid? GetUserId()
    {
        var id = _httpContextAccessor.HttpContext?.User
            .FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }

    public Guid GetRequiredUserId() =>
        GetUserId() ?? throw new UnauthorizedAccessException("Пользователь не аутентифицирован.");
}
