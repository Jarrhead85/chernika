using Chernika.Domain;

namespace Chernika.IntegrationTests;

/// <summary>
/// Состояние модуля ИК для интеграционных тестов.
/// <para>
/// Тестовая замена боевой <c>ConfiguredIndividualCardModuleState</c>, читающей
/// конфигурацию. Регистрируется SCOPED, поэтому каждый тест получает свой
/// экземпляр: по умолчанию модуль включён — сохранённый код ИК и его покрытие
/// продолжают работать и понадобятся при возобновлении модуля. Тест, проверяющий
/// ОТКАЗ, выключает модуль в своём scope и не влияет на остальные.
/// </para>
/// </summary>
public sealed class TestIndividualCardModuleState : IIndividualCardModuleState
{
    /// <summary>
    /// По умолчанию включено: тесты ИК проверяют сохранённый код, а не выключенный
    /// модуль. Боевые среды выключены (см. IndividualCardModuleOptions).
    /// </summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>Выключает модуль в пределах текущего scope.</summary>
    public void Disable() => IsEnabled = false;
}
