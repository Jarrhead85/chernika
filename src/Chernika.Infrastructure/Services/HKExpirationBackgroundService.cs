using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chernika.Infrastructure.Services;

public sealed class HKExpirationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<HKExpirationOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HKExpirationBackgroundService> _logger;

    public HKExpirationBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<HKExpirationOptions> options,
        TimeProvider time,
        ILogger<HKExpirationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Value.RunOnStartup)
        {
            _logger.LogInformation(
                "Стартовый прогон обработки сроков ХК (RunOnStartup=true), пороги: {Thresholds}.",
                string.Join(", ", _options.Value.WarningDays));

            try
            {
                await RunScopedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка стартового запуска обработки сроков действия ХК");
            }
        }
        else
        {
            _logger.LogInformation(
                "Стартовый прогон обработки сроков ХК пропущен: RunOnStartup=false.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ComputeNextDelay(_time.GetUtcNow(), _options.Value.DailyRunTimeUtc);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogInformation(
                "Ежедневный прогон обработки сроков ХК начат (расписание: {Schedule} UTC).",
                _options.Value.DailyRunTimeUtc);

            try
            {
                await RunScopedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка ежедневной обработки сроков действия ХК");
            }
        }
    }

    private async Task RunScopedAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<HKCardExpirationService>();
        await service.ProcessExpiringCardsAsync(ct);
    }

    internal static TimeSpan ComputeNextDelay(DateTimeOffset now, string dailyRunTimeUtc)
    {
        var runAt = TimeSpan.TryParse(dailyRunTimeUtc, out var parsed) ? parsed : TimeSpan.FromHours(1);
        var next = now.Date.Add(runAt);
        if (next <= now)
            next = next.AddDays(1);
        return next - now;
    }
}
