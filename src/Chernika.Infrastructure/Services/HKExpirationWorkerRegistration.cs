using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Регистрация фоновой обработки сроков действия ХК.
/// <para>
/// Существует ровно один владелец worker'а — хост, у которого
/// <c>HKExpiration:WorkerEnabled = true</c>. Обычно это
/// <c>Chernika.Web</c>: именно его запускает <c>start_app.cmd</c>, и именно в
/// нём живёт весь интерфейс.
/// </para>
/// <para>
/// Почему регистрация не «просто перенесена» из API в Web, а сделана через
/// настройку. Если просто перенести строку <c>AddHostedService</c>, то при
/// запуске обоих хостов (что при развёртывании легко происходит: Web отдельно,
/// API отдельно) появится два экземпляра обработки на одной базе. Настройка
/// делает владельца явным и проверяемым: тест читает конфигурацию обоих хостов
/// и утверждает, что ровно в одном worker включён.
/// </para>
/// <para>
/// Второй уровень защиты от двух экземпляров — advisory lock на уровне БД внутри
/// <see cref="HKCardExpirationService"/>. Настройка отвечает за «кто запускает
/// worker», lock — за «не выполнять два прогона одновременно», что важно при
/// нескольких экземплярах одного хоста.
/// </para>
/// </summary>
public static class HKExpirationWorkerRegistration
{
    /// <summary>
    /// Регистрирует worker, если текущий хост объявлен владельцем.
    /// </summary>
    /// <param name="services">Контейнер служб.</param>
    /// <param name="configuration">Конфигурация с разделом <c>HKExpiration</c>.</param>
    /// <param name="hostName">Имя хоста для журнала: кто стал владельцем.</param>
    public static IServiceCollection AddHKExpirationWorker(
        this IServiceCollection services, IConfiguration configuration, string hostName)
    {
        var options = new HKExpirationOptions();
        configuration.GetSection("HKExpiration").Bind(options);

        if (!options.WorkerEnabled)
        {
            // Не ошибка и не предупреждение: хост сознательно не владелец.
            // Сообщение INFO, чтобы в журнале запуска было видно решение, а не
            // его отсутствие.
            services.AddSingleton(new HKExpirationWorkerOwnership(hostName, false));
            return services;
        }

        services.AddHostedService<HKExpirationBackgroundService>();
        services.AddSingleton(new HKExpirationWorkerOwnership(hostName, true));
        return services;
    }
}

/// <summary>
/// Зафиксированное решение о владении worker'ом. Регистрируется всегда — и
/// когда хот стал владельцем, и когда нет, — чтобы в журнале запуска было
/// видно оба случая.
/// </summary>
public sealed record HKExpirationWorkerOwnership(string HostName, bool IsOwner);