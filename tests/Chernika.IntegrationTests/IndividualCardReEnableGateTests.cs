using System.Reflection;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Граница повторного включения модуля индивидуальных карт.
/// <para>
/// Модуль выключен, и включать его одним изменением конфигурации
/// <b>нельзя</b>. Причина найдена инвентаризацией PR-6 и закреплена здесь тестами:
/// длина НД марки и длина поля НД в снимке ИК больше не совпадают.
/// </para>
/// <para>
/// Что именно разошлось: <c>GsmMaterials.Nd</c> — <c>text</c> без ограничения,
/// сервис принимает до 1000 символов. <c>IndividualCardItemMaterialSnapshots.Gost</c>
/// — <c>varchar(200)</c> и <b>не расширялся</b>. Модуль выключен, новых снимков не
/// создаётся, поэтому расхождение пока не проявилось; оно ударит при первом же
/// включении, когда расчёт снимка попытается записать длинный НД в 200 символов.
/// </para>
/// <para>
/// Тесты не «упоминают» расхождение, а делают его достижимым: длинный НД реально
/// принимается справочником марок, а вставка такого НД в снимок реально
/// отклоняется уровнем БД.
/// </para>
/// </summary>
[Collection("Database")]
public class IndividualCardReEnableGateTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardReEnableGateTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Заведомо длиннее снимкового поля, но в пределах сервисного лимита.</summary>
    private static string LongNd() => "ГОСТ 21743-76; " + new string('A', 274);

    [Fact]
    public async Task Module_IsDisabledByDefault_AndWriteGuardIsServerSide()
    {
        // Настройка по умолчанию — выключено, во всех средах. Случайное включение
        // в Development не должно проходить unnoticed.
        Assert.False(new Chernika.Domain.IndividualCardModuleOptions().Enabled);

        // Границу держит серверная проверка, а не UI: пока она включена, запись
        // невозможна при любой конфигурации клиента.
        var demandWrite = typeof(IndividualCardModuleGuard).GetMethod(
            "DemandWrite", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(demandWrite);

        await using var s = _fixture.CreateScope();
        s.IndividualCardModule.Disable();

        var ex = Assert.Throws<InvalidOperationException>(
            () => IndividualCardModuleGuard.DemandWrite(s.IndividualCardModule, "проверка"));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task Nd_LongerThanSnapshotGostColumn_IsAcceptedByMaterialReference()
    {
        // Достижимость расхождения: справочник марок принимает НД длиннее
        // varchar(200) поля снимка ИК. Без этого следующие тесты проверяли бы
        // невозможное.
        var longNd = LongNd();
        Assert.True(longNd.Length > SnapshotGostMaxLength, "тестовый НД должен быть длиннее снимкового поля");
        Assert.True(longNd.Length <= GsmMaterialService.NdMaxLength, "НД должен укладываться в сервисный предел");

        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var created = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Длинный НД " + Suffix(),
            Nd = longNd,
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Азотные" },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking()
            .FirstAsync(m => m.Id == created.Id);
        Assert.Equal(longNd, stored.Nd);
        Assert.True(stored.Nd!.Length > SnapshotGostMaxLength);
    }

    [Fact]
    public void SnapshotGostColumn_StaysNarrowerThanMaterialNd()
    {
        // Прямая сверка конфигурации EF: пока поля разной длины, включение модуля
        // ИК создаёт риск отказа при записи снимка. Сверяется не «схема», а
        // ровно те метаданные, которыми пользуется EF при вставке.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=configuration_probe;Username=none")
            .Options;
        using var ctx = new AppDbContext(options);

        var snapshotGost = ctx.Model
            .FindEntityType(typeof(IndividualCardItemMaterialSnapshot))!
            .FindProperty(nameof(IndividualCardItemMaterialSnapshot.Gost))!
            .GetMaxLength();

        var materialNd = ctx.Model
            .FindEntityType(typeof(GsmMaterial))!
            .FindProperty(nameof(GsmMaterial.Nd))!
            .GetMaxLength();

        // Nd — text, то есть длины не имеет.
        Assert.Null(materialNd);
        Assert.Equal(SnapshotGostMaxLength, snapshotGost);

        // Предел сервиса известен и больше снимкового поля — это и есть
        // неразрешённый контракт повторного включения.
        Assert.True(GsmMaterialService.NdMaxLength > snapshotGost!.Value);
    }

    [Fact]
    public async Task SnapshotGostColumn_CanActuallyOverflow_WhileModuleIsDisabled()
    {
        // Не «теоретическая длина», а достижимый отказ: вставка снимка с длинным
        // НД отклоняется уровнем БД кодом 22001. Пока модуль выключен, этого пути
        // в приложении нет — и именно поэтому включать его нельзя, не изменив
        // контракт.
        //
        // Вставка идёт прямым SQL: у снимка девять обязательных колонок без
        // значений по умолчанию, а FK-проверка в PostgreSQL выполняется
        // AFTER-триггером, то есть ПОСЛЕ вставки строки. Ограничение длины
        // проверяется раньше, поэтому отказ 22001 возникает независимо от
        // существования связанной строки ИК.
        var longNd = LongNd();

        await using var s = _fixture.CreateScope();
        s.IndividualCardModule.Disable();

        var before = await s.Db.IndividualCardItemMaterialSnapshots
            .IgnoreQueryFilters().CountAsync();
        var rowId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await s.Db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""IndividualCardItemMaterialSnapshots""
                    (""Id"", ""IndividualCardItemId"", ""SourceGsmMaterialId"",
                     ""MaterialName"", ""MaterialType"", ""Gost"", ""Category"",
                     ""CalculatedVolume"", ""UnitOfMeasure"", ""SortOrder"")
                VALUES (
                    {rowId}, {Guid.NewGuid()}, {Guid.NewGuid()},
                    'Марка проверки границы', 'Азотные', {longNd}, 0,
                    0, 'кг', 1)");
        });

        // 22001 = value_too_long_data_exception. Проверяется по коду, а не по
        // тексту сообщения: текст локализован и меняется вместе с локалью сервера,
        // а код ошибки — часть контракта.
        Assert.Equal("22001", ex.SqlState);
        Assert.Contains("character varying(200)", ex.MessageText, StringComparison.Ordinal);

        // Строка не появилась: отказ произошёл на вставке, а не «успех с усечением».
        Assert.Equal(before, await s.Db.IndividualCardItemMaterialSnapshots
            .IgnoreQueryFilters().CountAsync());
        Assert.False(await s.Db.IndividualCardItemMaterialSnapshots
            .IgnoreQueryFilters().AnyAsync(r => r.Id == rowId));
    }

    /// <summary>
    /// Фактическая длина <c>IndividualCardItemMaterialSnapshots.Gost</c> в схеме.
    /// Дублируется здесь намеренно: если колонку расширят, тест должен это
    /// заметить и заставить пересмотреть решение о включении модуля, а не
    /// молча остаться зелёным.
    /// </summary>
    private const int SnapshotGostMaxLength = 200;
}
