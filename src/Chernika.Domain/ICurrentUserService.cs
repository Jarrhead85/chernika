namespace Chernika.Domain;

public interface ICurrentUserService
{
    Guid? GetUserId();
    Guid GetRequiredUserId();
}
