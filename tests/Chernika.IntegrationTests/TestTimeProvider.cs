namespace Chernika.IntegrationTests;

/// <summary>
/// Управляемый <see cref="TimeProvider"/> для тестов обработки сроков.
/// <para>
/// Поведение по умолчанию — реальное системное время, поэтому подмена ничего не
/// меняет для всех существующих тестов. Тесты дат и границ сроков фиксируют
/// момент явно, иначе проверки «день до / день срока / день после» зависели бы
/// от того, в какой час суток их запустили.
/// </para>
/// <para>
/// Класс намеренно изменяемый и общий для фикстуры: обработчик сроков
/// регистрируется как singleton, иначе подмена не дошла бы до сервиса.
/// Поэтому фикстура сбрасывает время при каждом создании scope — иначе
/// закреплённый момент утекал бы в следующий тест.
/// </para>
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset? _fixedUtcNow;

    /// <summary>Закрепить момент. <c>null</c> возвращает системное время.</summary>
    public void SetUtcNow(DateTimeOffset? instant) => _fixedUtcNow = instant;

    /// <summary>Вернуть системное время.</summary>
    public void Reset() => _fixedUtcNow = null;

    /// <summary>Регистрация синглтона в DI: сервисы получают его как TimeProvider.</summary>
    public static TestTimeProvider SystemShim() => new();

    public override DateTimeOffset GetUtcNow()
        => _fixedUtcNow ?? global::System.TimeProvider.System.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone
        => global::System.TimeProvider.System.LocalTimeZone;
}