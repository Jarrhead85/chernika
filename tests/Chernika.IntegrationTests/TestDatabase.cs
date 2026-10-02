using Microsoft.Extensions.Configuration;

namespace Chernika.IntegrationTests;

/// <summary>
/// Единственное место в тестовом проекте, где собирается строка подключения к
/// локальному PostgreSQL.
/// <para>
/// Причина выделения: раньше литерал с учётными данными повторялся в каждой
/// фикстуре. При добавлении HTTP-инфраструктуры это дало бы ТРЕТЬЕ повторение,
/// а правка сервера или пароля разъезжалась бы по файлам. Дублирование строк
/// подключения — не стилистика: расхождение в <c>Database=</c> незаметно уводит
/// тесты в чужую базу.
/// </para>
/// <para>
/// <b>Пароль в репозитории не хранится.</b> Берётся из переменной окружения
/// <c>POSTGRES_PASSWORD</c> — тем же способом, что и само приложение
/// (<c>DatabaseConnection.Build</c> и шаблон <c>appsettings.Development.template.json</c>).
/// Если переменная не задана, используется пустая строка: локальный сервер с
/// доверенной аутентификацией (trust) от неё не зависит, а сервер с паролем
/// даст понятную ошибку подключения, а не тихо уедет в другую базу.
/// </para>
/// </summary>
public static class TestDatabase
{
    private static string Password() =>
        Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? string.Empty;

    /// <summary>Сервер без указания базы: нужен для CREATE/DROP DATABASE.</summary>
    public static string LocalServerConnectionString() =>
        "Host=localhost;Port=5432;Database=postgres;Username=postgres"
        + PasswordSuffix();

    /// <summary>Общая интеграционная база. Пересоздаётся фикстурой.</summary>
    public const string IntegrationDbName = "chernika_test";

    /// <summary>
    /// Строка подключения к указанной базе.
    /// <c>Pooling=false</c> — обязателен: без него фикстура не успевает закрыть
    /// соединения, и следующий прогон падает на <c>DROP DATABASE</c> с
    /// «being accessed by other users».
    /// </summary>
    public static string For(string dbName) =>
        "Host=localhost;Port=5432;Database=" + dbName
        + ";Username=postgres" + PasswordSuffix() + ";Pooling=false";

    private static string PasswordSuffix()
    {
        var password = Password();
        return string.IsNullOrEmpty(password) ? string.Empty : ";Password=" + password;
    }
}
