namespace Chernika.Domain;

/// <summary>
/// Часовой пояс приложения.
/// <para>
/// Данные хранятся и сравниваются в UTC - это не меняется. Часовой пояс влияет
/// только на границу интерфейса: что видит пользователь и что он вводит.
/// Раньше показ опирался на часовой пояс операционной системы, а на сервере он
/// равнялся UTC, поэтому введённые и показанные даты не совпадали с рабочими.
/// </para>
/// <para>
/// Смещение задаётся конфигурацией <c>TimeZone:OffsetHours</c> и по умолчанию
/// равно +3. Значение фиксируется при старте и не меняется в течение работы
/// процесса: иначе одни и те же данные показывались бы по-разному в пределах
/// одной сессии.
/// </para>
/// </summary>
public static class AppTimeZone
{
    /// <summary>Смещение по умолчанию: UTC+3.</summary>
    public const int DefaultOffsetHours = 3;

    /// <summary>Наименьшее допустимое смещение в часах.</summary>
    public const int MinOffsetHours = -12;

    /// <summary>Наибольшее допустимое смещение в часах.</summary>
    public const int MaxOffsetHours = 14;

    private static TimeSpan _offset = TimeSpan.FromHours(DefaultOffsetHours);

    /// <summary>
    /// Смещение часового пояса приложения относительно UTC.
    /// <para>
    /// Значение только читается здесь: запись выполняется единственный раз
    /// при старте хоста, иначе два экрана в одной сессии могли бы показать
    /// разное время для одного и того же события.
    /// </para>
    /// </summary>
    public static TimeSpan Offset => _offset;

    /// <summary>
    /// Применяет смещение из конфигурации.
    /// <para>
    /// Приводится к целому числу минут с проверкой диапазона: молчаливое
    /// принятие неверного значения дало бы неверные даты в отчётах, которые
    /// заметнее всего приходит проверять уже по факту.
    /// </para>
    /// </summary>
    public static void Configure(int offsetHours)
    {
        if (offsetHours < MinOffsetHours || offsetHours > MaxOffsetHours)
        {
            throw new InvalidOperationException(
                $"Смещение часового пояса {offsetHours} ч вне допустимого диапазона "
                + $"[{MinOffsetHours}..{MaxOffsetHours}].");
        }

        _offset = TimeSpan.FromHours(offsetHours);
    }
}

/// <summary>
/// Преобразование времени на границе интерфейса.
/// <para>
/// Хранилище остаётся в UTC: сущности, сравнения сроков и отчёты по
/// просрочкам считают UTC. Эти методы применяются только при показе и при
/// разборе ввода пользователя.
/// </para>
/// <para>
/// Значения, приходящие из базы, имеют <see cref="DateTimeKind.Unspecified"/>:
/// драйвер отдаёт <c>timestamp without time zone</c>. По соглашению проекта
/// они считаются UTC, иначе показ зависел бы от того, каким флагом помечено
/// значение.
/// </para>
/// </summary>
public static class AppTime
{
    /// <summary>Текущее время в часовом поясе приложения.</summary>
    public static DateTime Now => ToDisplay(DateTime.UtcNow);

    /// <summary>
    /// Переводит момент в часовой пояс приложения. Метки вида
    /// <see cref="DateTimeKind.Local"/> и <see cref="DateTimeKind.Utc"/>
    /// трактуются как UTC, потому что сейчас они появляются только при
    /// разборе ввода, где значение уже записано в часовом поясе приложения.
    /// </summary>
    public static DateTime ToDisplay(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Utc => value,
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return DateTime.SpecifyKind(utc + AppTimeZone.Offset, DateTimeKind.Unspecified);
    }

    /// <summary>Переводит введённое пользователем время обратно в UTC.</summary>
    public static DateTime ToUtc(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Utc
            ? ToDisplay(value)
            : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

        return DateTime.SpecifyKind(local - AppTimeZone.Offset, DateTimeKind.Utc);
    }

    /// <summary>
    /// Форматирует дату в часовом поясе приложения по заданной маске.
    /// Пустое значение даёт пустую строку, а не «01.01.0001».
    /// </summary>
    public static string Fmt(DateTime? value, string format) =>
        value is { } v ? ToDisplay(v).ToString(format) : string.Empty;

    /// <inheritdoc cref="Fmt(DateTime?, string)" />
    public static string Fmt(DateTime value, string format) =>
        ToDisplay(value).ToString(format);

    /// <summary>
    /// Форматирует дату, а при её отсутствии возвращает указанный текст.
    /// <para>
    /// Заменяет конструкцию <c>value?.ToString(f) ?? "—"</c>: у той пустое
    /// значение даёт <c>""</c>, а не <c>null</c>, поэтому запасной текст
    /// не срабатывал бы и вместо тире осталась бы пустая ячейка.
    /// </para>
    /// </summary>
    public static string FmtOr(DateTime? value, string format, string fallback) =>
        value is { } v ? ToDisplay(v).ToString(format) : fallback;

    /// <summary>Формат по умолчанию для таблиц: дата.</summary>
    public const string DateFormat = "dd.MM.yyyy";

    /// <summary>Формат по умолчанию для таблиц: дата со временем.</summary>
    public const string DateTimeFormat = "dd.MM.yyyy HH:mm";

    /// <summary>Дата в часовом поясе приложения, пусто вместо нулевой даты.</summary>
    public static string Date(DateTime? value) => Fmt(value, DateFormat);

    /// <inheritdoc cref="Date(DateTime?)" />
    public static string Date(DateTime value) => Fmt(value, DateFormat);

    /// <summary>Дата со временем в часовом поясе приложения.</summary>
    public static string DateTimeText(DateTime? value) => Fmt(value, DateTimeFormat);

    /// <inheritdoc cref="DateTimeText(DateTime?)" />
    public static string DateTimeText(DateTime value) => Fmt(value, DateTimeFormat);

    /// <summary>
    /// Разбирает введённую пользователем дату и переводит её в UTC.
    /// Возвращает <see langword="null"/>, если ввод пуст или не разобран.
    /// </summary>
    public static DateTime? ParseInput(string? text) =>
        DateTime.TryParse(text, out var parsed) ? ToUtc(parsed) : null;
}