using Microsoft.Extensions.Logging;

namespace Chernika.IntegrationTests;

/// <summary>
/// Собирает записи журнала, чтобы тест мог утверждать, что ошибка ПОПАЛА в
/// журнал, а не просто не уронила цикл.
/// <para>
/// Подмена ILogger одного сервиса «извне» невозможна: он приходит в
/// конструктор. Поэтому провайдер один на всю фикстуру, а набор записей
/// очищается перед каждым тестом.
/// </para>
/// <para>
/// Уровень Error собирается всегда; остальные — по требованию теста, чтобы
/// проверка «ошибка попала в журнал» не зависела от фильтра.
/// </para>
/// </summary>
public sealed class TestLogCollector : ILoggerProvider
{
    private readonly List<Entry> _entries = new();

    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    public void Clear()
    {
        lock (_entries) _entries.Clear();
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_entries) return _entries.ToArray();
    }

    /// <summary>Записи уровня Error, в тексте которых встречается подстрока.</summary>
    public bool HasError(string categoryContains, string messageContains) =>
        Snapshot().Any(e => e.Level >= LogLevel.Error
            && e.Category.Contains(categoryContains, StringComparison.Ordinal)
            && e.Message.Contains(messageContains, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CollectingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<Entry> _target;

        public CollectingLogger(string category, List<Entry> target)
        {
            _category = category;
            _target = target;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            var entry = new Entry(_category, logLevel, formatter(state, exception), exception);
            lock (_target) _target.Add(entry);
        }
    }
}