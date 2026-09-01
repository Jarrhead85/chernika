using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Chernika.IntegrationTests;

public sealed class TestScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;

    public TestScope(AsyncServiceScope scope, FakeCurrentUser user)
    {
        _scope = scope;
        User = user;
        Db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Tasks = scope.ServiceProvider.GetRequiredService<TaskService>();
        HK = scope.ServiceProvider.GetRequiredService<HKCardService>();
        Expiration = scope.ServiceProvider.GetRequiredService<HKCardExpirationService>();
        Notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        Audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        Permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        Users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Equipment = scope.ServiceProvider.GetRequiredService<EquipmentService>();
        CoeffService = scope.ServiceProvider.GetRequiredService<CoefficientService>();
        GsmMaterials = scope.ServiceProvider.GetRequiredService<GsmMaterialService>();
    }

    public FakeCurrentUser User { get; }
    public AppDbContext Db { get; }
    public TaskService Tasks { get; }
    public HKCardService HK { get; }
    public HKCardExpirationService Expiration { get; }
    public NotificationService Notifications { get; }
    public AuditService Audit { get; }
    public IPermissionService Permissions { get; }
    public UserManager<ApplicationUser> Users { get; }
    public EquipmentService Equipment { get; }
    public CoefficientService CoeffService { get; }
    public GsmMaterialService GsmMaterials { get; }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
    }
}
