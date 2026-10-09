using Chernika.Domain;
using Xunit;

namespace Chernika.UnitTests;

/// <summary>
/// Часовой пояс приложения: показ сдвигается, хранение остаётся в UTC.
/// <para>
/// Проверяется именно пара «показал и принял»: если сдвиг применяется только
/// при показе, то введённое пользователем время сохранялось бы со сдвигом и
/// возвращалось бы на экране другим.
/// </para>
/// </summary>
public class AppTimeTests : IDisposable
{
    private readonly TimeSpan _original = AppTimeZone.Offset;

    public void Dispose() => AppTimeZone.Configure((int)_original.TotalHours);

    [Fact]
    public void Offset_DefaultsToPlusThree()
    {
        AppTimeZone.Configure(AppTimeZone.DefaultOffsetHours);

        Assert.Equal(TimeSpan.FromHours(3), AppTimeZone.Offset);
    }

    [Fact]
    public void Display_AddsConfiguredOffset()
    {
        AppTimeZone.Configure(3);

        var utc = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 3, 15, 15, 0, 0), AppTime.ToDisplay(utc));
    }

    [Fact]
    public void Display_TreatsUnspecifiedValuesAsUtc()
    {
        AppTimeZone.Configure(3);

        // Значения из базы приходят без метки времени и по соглашению проекта
        // являются UTC. Если бы они считались локальными, показ зависел бы
        // от того, какой флаг установил драйвер.
        var fromDatabase = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateTime(2026, 3, 15, 15, 0, 0), AppTime.ToDisplay(fromDatabase));
    }

    [Fact]
    public void Input_RoundTripReturnsSameInstant()
    {
        AppTimeZone.Configure(3);

        var typedByUser = new DateTime(2026, 3, 15, 15, 30, 0);

        var stored = AppTime.ToUtc(typedByUser);
        var shownAgain = AppTime.ToDisplay(stored);

        Assert.Equal(new DateTime(2026, 3, 15, 12, 30, 0, DateTimeKind.Utc), stored);
        Assert.Equal(typedByUser, shownAgain);
    }

    [Fact]
    public void ParseInput_ConvertsEnteredValueToUtc()
    {
        AppTimeZone.Configure(3);

        var parsed = AppTime.ParseInput("15.03.2026 15:30");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, 3, 15, 12, 30, 0, DateTimeKind.Utc), parsed!.Value);
    }

    [Fact]
    public void ParseInput_UnparsableValueYieldsNull()
    {
        AppTimeZone.Configure(3);

        Assert.Null(AppTime.ParseInput("не дата"));
        Assert.Null(AppTime.ParseInput(""));
        Assert.Null(AppTime.ParseInput(null));
    }

    [Fact]
    public void Formatting_UsesShiftedValueAndEmptyForMissing()
    {
        AppTimeZone.Configure(3);

        var value = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("15.03.2026", AppTime.Date(value));
        Assert.Equal("15.03.2026 15:00", AppTime.DateTimeText(value));

        Assert.Equal(string.Empty, AppTime.Date((DateTime?)null));
        Assert.Equal("—", AppTime.FmtOr(null, "dd.MM.yyyy", "—"));
    }

    [Fact]
    public void FmtOr_KeepsFallbackWhenValueMissing()
    {
        AppTimeZone.Configure(3);

        // Раньше конструкция «x?.ToString(f) ?? "—"» давала пустую ячейку:
        // пустое значение форматируется как "", а не как null, и запасной
        // текст не срабатывал.
        Assert.Equal("—", AppTime.FmtOr(null, "dd.MM.yyyy", "—"));
        Assert.Equal("15.03.2026", AppTime.FmtOr(
            new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), "dd.MM.yyyy", "—"));
    }

    [Fact]
    public void Configure_RejectsOffsetOutsideAllowedRange()
    {
        Assert.Throws<InvalidOperationException>(() => AppTimeZone.Configure(-13));
        Assert.Throws<InvalidOperationException>(() => AppTimeZone.Configure(15));
    }

    [Fact]
    public void Now_IsShiftedAgainstUtc()
    {
        AppTimeZone.Configure(3);

        var now = AppTime.Now;

        // Смещение обязано совпадать, а не быть близким: проверка на «примерно
        // три часа» пропустила бы смещение, заданное в неверном часовом поясе.
        var diff = now - DateTime.UtcNow;

        Assert.InRange(diff, TimeSpan.FromHours(2.9), TimeSpan.FromHours(3.1));
    }
}