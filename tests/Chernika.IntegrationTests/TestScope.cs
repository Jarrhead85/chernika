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

    public TestScope(AsyncServiceScope scope, FakeCurrentUser user, TestTimeProvider clock, TestLogCollector logs)
    {
        _scope = scope;
        User = user;
        Clock = clock;
        Logs = logs;
        Db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Tasks = scope.ServiceProvider.GetRequiredService<TaskService>();
        HK = scope.ServiceProvider.GetRequiredService<HKCardService>();
        Expiration = scope.ServiceProvider.GetRequiredService<HKCardExpirationService>();
        Forecast = scope.ServiceProvider.GetRequiredService<HKExpirationForecastService>();
        Notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        Audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        Permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        Users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Equipment = scope.ServiceProvider.GetRequiredService<EquipmentService>();
        CoeffService = scope.ServiceProvider.GetRequiredService<CoefficientService>();
        GsmMaterials = scope.ServiceProvider.GetRequiredService<GsmMaterialService>();
        Search = scope.ServiceProvider.GetRequiredService<SearchService>();
        IndividualCardModule = scope.ServiceProvider.GetRequiredService<TestIndividualCardModuleState>();
        IndividualCards = scope.ServiceProvider.GetRequiredService<IndividualCardService>();
        Reports = scope.ServiceProvider.GetRequiredService<ReportService>();
    }

    public FakeCurrentUser User { get; }

    /// <summary>Управляемое время фикстуры. Даты сроков проверяются только через
    /// него: иначе тесты «день до / день срока / день после» зависели бы от
    /// часа запуска.</summary>
    public TestTimeProvider Clock { get; }

    /// <summary>Журнал текущего теста: позволяет утверждать, что ошибка записана.</summary>
    public TestLogCollector Logs { get; }
    public AppDbContext Db { get; }
    public TaskService Tasks { get; }
    public HKCardService HK { get; }
    public HKCardExpirationService Expiration { get; }

    /// <summary>Прогноз обработки сроков: только чтение. Нужен, чтобы узнать,
    /// что сделает worker, не выполняя это.</summary>
    public HKExpirationForecastService Forecast { get; }
    public NotificationService Notifications { get; }
    public AuditService Audit { get; }
    public IPermissionService Permissions { get; }
    public UserManager<ApplicationUser> Users { get; }
    public EquipmentService Equipment { get; }
    public CoefficientService CoeffService { get; }
    public GsmMaterialService GsmMaterials { get; }

    /// <summary>Поиск по всем разделам. Нужен, чтобы проверить, что после удаления
    /// переходных колонок марки ГСМ находятся по НД и по названию группы.</summary>
    public SearchService Search { get; }

    /// <summary>Состояние модуля ИК в этом scope: по умолчанию включён.</summary>
    public TestIndividualCardModuleState IndividualCardModule { get; }

    public IndividualCardService IndividualCards { get; }

    /// <summary>Нужен для проверки контроллеров API отчётов законсервированного модуля.</summary>
    public ReportService Reports { get; }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
    }
}
