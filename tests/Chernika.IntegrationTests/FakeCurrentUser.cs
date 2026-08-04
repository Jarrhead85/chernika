using Chernika.Domain;

namespace Chernika.IntegrationTests;

public sealed class FakeCurrentUser : ICurrentUserService
{
    public Guid? CurrentUserId { get; set; }

    public Guid? GetUserId() => CurrentUserId;

    public Guid GetRequiredUserId() =>
        CurrentUserId ?? throw new UnauthorizedAccessException("Пользователь не аутентифицирован.");
}
