using Chernika.Domain;
using Microsoft.Extensions.Options;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Единая точка отказа законсервированного модуля индивидуальных карт.
/// <para>
/// Отказ контролируемый и происходит ДО любой записи в БД: операция не должна
/// ни создавать черновик, ни пересчитывать, ни менять исторический документ.
/// Исключение — <see cref="InvalidOperationException"/>, который действующие
/// контроллеры и страницы уже показывают пользователю как понятную ошибку.
/// </para>
/// </summary>
public static class IndividualCardModuleGuard
{
    /// <summary>
    /// Текст отказа. Постоянен, чтобы один и тот же вызов из интерфейса и из
    /// прямого API давал одинаковое объяснение.
    /// </summary>
    private const string Reason =
        "Индивидуальные карты выведены из действующего функционала проекта и законсервированы. " +
        "Сохранённые карты доступны только для чтения: создание, пересчёт, изменение и экспорт отключены.";

    /// <summary>
    /// Запрещает операцию записи, если модуль ИК выключен.
    /// </summary>
    /// <param name="module">Текущее состояние модуля.</param>
    /// <param name="operation">
    /// Имя операции в понятной форме («создание черновика ИК»). Попадает в текст
    /// отказа, чтобы пользователь понимал, что именно недоступно.
    /// </param>
    public static void DemandWrite(IIndividualCardModuleState module, string operation)
    {
        if (module.IsEnabled) return;
        throw new InvalidOperationException($"{Reason} Недоступная операция: {operation}.");
    }
}

/// <summary>
/// Состояние модуля ИК по настройке раздела <c>IndividualCards</c>.
/// Отсутствие раздела в конфигурации равнозначно <c>Enabled=false</c>: выключенный
/// модуль — это штатный режим, а не ошибка развёртывания.
/// </summary>
public sealed class ConfiguredIndividualCardModuleState : IIndividualCardModuleState
{
    private readonly IOptionsMonitor<IndividualCardModuleOptions> _options;

    public ConfiguredIndividualCardModuleState(IOptionsMonitor<IndividualCardModuleOptions> options) =>
        _options = options;

    public bool IsEnabled => _options.CurrentValue.Enabled;
}
