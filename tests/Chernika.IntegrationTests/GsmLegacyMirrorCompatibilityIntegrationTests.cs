using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Корректировка PR-3: семантика legacy-полей, пределы длин переходных зеркал,
/// запрет мультиподгруппных марок в строках ХК и отсутствие ложного отказа
/// после успешного commit.
/// </summary>
[Collection("Database")]
public class GsmLegacyMirrorCompatibilityIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmLegacyMirrorCompatibilityIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SetRefEditor(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── §3: legacy Type = подгруппа ───────────────────────────────────────

    [Fact]
    public async Task Create_TwoSubgroups_MirrorsAlphabeticallyFirstSubgroupIntoType()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Зеркало Type " + Suffix(),
            Nd = "ГОСТ 1",
            GroupName = "Моторные масла",
            // Порядок ввода обратен алфавитному: правило обязано быть
            // детерминированным, а не «первой попавшейся».
            SubgroupNames = new List<string> { "Ясные", "Азотные", "Белое золото" },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);

        // Первая ПО АЛФАВИТУ: «Азотные».
        Assert.Equal("Азотные", stored.Type);
        Assert.NotEqual("Моторные масла", stored.Type);
        Assert.Equal(3, view.SubgroupNames.Count);
    }

    [Fact]
    public async Task Create_SingleSubgroup_MirrorsThatSubgroup()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Одна подгруппа " + Suffix(),
            GroupName = "Пластичные смазки",
            SubgroupNames = new List<string> { "Для подшипников" },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);
        Assert.Equal("Для подшипников", stored.Type);
    }

    [Fact]
    public async Task Update_ChangingSubgroups_RecomputesTypeMirror()
    {
        var id = await CreateMaterialAsync("Группа", new List<string> { "Альфа", "Бета" });

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            await s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Смена подгрупп " + Suffix(),
                GroupName = "Группа",
                SubgroupNames = new List<string> { "Гамма", "Дельта" },
            });
        }

        Assert.Equal("Гамма", await ReadAsync(id, m => m.Type));
    }

    [Fact]
    public async Task Update_LegacyMaterialWithoutClassification_PreservesHistoricalType()
    {
        // Legacy-марка: классификации нет, Type хранит историческое значение.
        var id = await CreateLegacyMaterialAsync("Legacy " + Suffix(), "Исторический тип");

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var result = await s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Legacy переименована " + Suffix(),
                Nd = "ГОСТ legacy",
                IntendedUse = "Новое назначение",
                Note = "Примечание",
            });
            Assert.NotNull(result);
            Assert.Null(result!.GroupName);
        }

        // Исторический Type не затирается и группа в него не пишется.
        Assert.Equal("Исторический тип", await ReadAsync(id, m => m.Type));
        Assert.Equal("ГОСТ legacy", await ReadAsync(id, m => m.Nd));
        Assert.Equal("Новое назначение", await ReadAsync(id, m => m.IntendedUse));
        Assert.Equal("Примечание", await ReadAsync(id, m => m.Note));
    }

    // ── A1: правила формы и сервиса совпадают ────────────────────────────

    [Fact]
    public async Task Update_ClassifiedMaterial_WithoutGroupAndSubgroups_IsRejected()
    {
        // Явное удаление классификации не поддерживается: молча терять её нельзя.
        var id = await CreateMaterialAsync("Классифицированная", new List<string> { "Подгруппа" });

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Попытка снять классификацию",
                Nd = "ГОСТ 1",
            }));

        Assert.Contains("классификац", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Классификация на месте.
        Assert.Equal(1, await CountClassificationsAsync(id));
        Assert.Equal("Подгруппа", await ReadAsync(id, m => m.Type));
    }

    [Fact]
    public async Task Update_SubgroupsWithoutGroup_IsRejected()
    {
        var id = await CreateLegacyMaterialAsync("Без группы " + Suffix(), "Исторический тип");

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Подгруппы без группы",
                SubgroupNames = new List<string> { "Одинокая" },
            }));

        Assert.Contains("групп", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountClassificationsAsync(id));
        Assert.Equal("Исторический тип", await ReadAsync(id, m => m.Type));
    }

    [Fact]
    public async Task Update_GroupWithoutSubgroups_IsRejected()
    {
        var id = await CreateLegacyMaterialAsync("Без подгрупп " + Suffix(), "Исторический тип");

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Группа без подгрупп",
                GroupName = "Моторные масла",
                SubgroupNames = new List<string>(),
            }));

        Assert.Contains("подгрупп", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountClassificationsAsync(id));
    }

    [Fact]
    public async Task Update_LegacyMaterial_CanAcquireClassification()
    {
        // Обратный переход разрешён: legacy-марку можно классифицировать обычной
        // правкой, после чего она становится доступной для новых строк ХК.
        var id = await CreateLegacyMaterialAsync("Классифицируемая " + Suffix(), "Исторический тип");

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var result = await s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
        {
            Name = "Классифицируемая " + Suffix(),
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Для турбин" },
        });

        Assert.NotNull(result);
        Assert.Equal("Моторные масла", result!.GroupName);
        Assert.Equal(1, await CountClassificationsAsync(id));
        Assert.Equal("Для турбин", await ReadAsync(id, m => m.Type));
    }

    private async Task<int> CountClassificationsAsync(Guid materialId)
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.GsmMaterialClassifications.AsNoTracking()
            .CountAsync(c => c.GsmMaterialId == materialId);
    }

    // ── §4: длина НД ──────────────────────────────────────────────────────
    //
    // До PR-5 предел 200 символов держался на двух внешних ограничениях: прежнем
    // зеркале Gost (varchar(128), позже 256) и снимке ИК (varchar(200)). Оба
    // перестали действовать: Gost в PR-5 не заполняется, модуль ИК законсервирован
    // и новых снимков не создаёт. Собственная колонка Nd — text.

    [Fact]
    public async Task Nd_LongerThanLegacyColumn_IsAccepted_AndStoredWithoutTruncation()
    {
        // 129..200 символов: прежние varchar(128) такое значение не вмещали.
        var longNd = "ГОСТ 21743-76; " + new string('A', 180);
        Assert.True(longNd.Length > 128 && longNd.Length <= 200, "длина тестового НД вне диапазона: " + longNd.Length);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Длинное НД " + Suffix(),
            Nd = longNd,
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Для турбин" },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);
        Assert.Equal(longNd, stored.Nd);

        // Переходное зеркало не заполняется, поэтому скрытого переполнения
        // прежнего varchar(256) здесь быть не может.
        Assert.Null(stored.Gost);
    }

    [Theory]
    [InlineData(128)]  // ровно прежний предел Type
    [InlineData(129)]  // первый символ сверх прежнего предела
    [InlineData(200)]  // прежний предел снимка ИК
    [InlineData(201)]  // прежний предел +1: раньше отклонялось, теперь принимается
    [InlineData(1000)] // ровно текущий предел действующего поля
    public async Task Nd_IsAcceptedUpToCurrentLimit_AndStoredWithoutTruncation(int length)
    {
        var nd = new string('Н', length);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = $"НД {length} " + Suffix(),
            Nd = nd,
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Для турбин" },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);
        Assert.Equal(nd, stored.Nd);
        Assert.Null(stored.Gost);
    }

    [Fact]
    public async Task Nd_ExactlyAtCurrentLimit_IsAccepted()
    {
        // Граница: ровно 1000 символов — предельное допустимое значение, 1001
        // отклоняется (отдельный тест). Усечения на границе быть не должно.
        var atLimit = new string('Д', 1000);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "НД на границе " + Suffix(),
            Nd = atLimit,
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Для турбин" },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);
        Assert.Equal(atLimit, stored.Nd);
    }

    [Fact]
    public async Task Nd_LongerThanCurrentLimit_IsRejectedBeforeWrite_WithClearMessage()
    {
        // Предел проверяется сервисом ДО записи понятным сообщением, а не падает
        // на уровне БД, и значение при этом НЕ усекается.
        var tooLong = new string('Б', 1001);
        var name = "Слишком длинное НД " + Suffix();

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
            {
                Name = name,
                Nd = tooLong,
                GroupName = "Моторные масла",
                SubgroupNames = new List<string> { "Для турбин" },
            }));

        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
        Assert.False(await s.Db.GsmMaterials.AnyAsync(m => m.Name == name || m.Nd == tooLong));
    }

    [Theory]
    [InlineData(128)]  // ровно прежний предел Type
    [InlineData(129)]  // первый символ сверх прежнего предела
    [InlineData(200)]  // предел новой модели для подгруппы
    public async Task Subgroup_AtLegacyBoundaries_FitsLegacyTypeAfterWidening(int length)
    {
        var subgroup = new string('П', length);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = $"Подгруппа {length} " + Suffix(),
            GroupName = "Группа",
            SubgroupNames = new List<string> { subgroup },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);
        Assert.Equal(subgroup, stored.Type);
    }

    [Fact]
    public async Task Subgroup_LongerThan128_FitsLegacyTypeAfterWidening()
    {
        // Подгруппа до 200 символов не помещалась в прежний varchar(128) Type.
        var longSubgroup = new string('В', 200);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Длинная подгруппа " + Suffix(),
            GroupName = "Группа",
            SubgroupNames = new List<string> { longSubgroup },
        });

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);
        Assert.Equal(longSubgroup, stored.Type);
    }

    [Fact]
    public async Task LegacyColumns_WereWidenedTo256_AndNdIsUnboundedText()
    {
        await using var s = _fixture.CreateScope();
        Assert.Equal("256", await ColumnLengthAsync(s, "GsmMaterials", "Type"));
        Assert.Equal("256", await ColumnLengthAsync(s, "GsmMaterials", "Gost"));

        // Снимки ИК не расширялись, но они больше не пишутся: модуль ИК
        // законсервирован и новых карт не создаёт. Действующее поле НД —
        // Nd, у него ограничения длины нет вовсе.
        Assert.Equal("200", await ColumnLengthAsync(s, "IndividualCardItemMaterialSnapshots", "Gost"));
        Assert.Null(await ColumnLengthAsync(s, "GsmMaterials", "Nd"));
    }

    // ── §3: мультиподгруппные марки разрешены в новых строках ХК (PR-5) ────

    [Fact]
    public async Task HkCreate_WithMultiSubgroupMaterial_IsAccepted()
    {
        // Переходный запрет PR-3 снят: несколько подгрупп больше не блокируют
        // новое назначение, потому что действующие потребители читают Nd и
        // классификацию, а не прежний Type.
        var multiId = await CreateMaterialAsync("Группа ХК", new List<string> { "Альфа", "Бета" });
        var singleId = await CreateMaterialAsync("Группа ХК", new List<string> { "Гамма" });

        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var created = await s.HK.CreateAsync(BuildCard(nodeId, unitId, multiId, singleId));

        Assert.NotNull(created);
        Assert.True(await s.Db.HKCardItemMaterials.AnyAsync(r => r.GsmMaterialId == multiId));
        Assert.True(await s.Db.HKCardItemMaterials.AnyAsync(r => r.GsmMaterialId == singleId));
    }

    [Fact]
    public async Task HkUpdate_ReplacingWithMultiSubgroupMaterial_IsAccepted()
    {
        var singleId = await CreateMaterialAsync("Группа ХК " + Suffix(), new List<string> { "Гамма" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        Guid cardId;
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var created = await s.HK.CreateAsync(BuildCard(nodeId, unitId, singleId));
            cardId = created.Id;
        }

        var multiId = await CreateMaterialAsync("Замена " + Suffix(), new List<string> { "Дельта", "Эпсилон" });

        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);
        var card = await s2.HK.GetByIdAsync(cardId);
        Assert.NotNull(card);
        var row = card!.Items.First().Materials.First();
        var originalRowId = row.Id;
        row.GsmMaterialId = multiId;

        var updated = await s2.HK.UpdateAsync(card);
        Assert.NotNull(updated);

        // Ссылка заменена, а идентификатор строки ХК прежний: правка не
        // пересоздаёт строку и не трогает её категорию.
        await using var s3 = _fixture.CreateScope();
        var saved = await s3.Db.HKCardItemMaterials.AsNoTracking()
            .SingleAsync(r => r.Id == originalRowId);
        Assert.Equal(multiId, saved.GsmMaterialId);
    }

    [Fact]
    public async Task HkCreate_WithSingleSubgroupMaterial_IsAccepted()
    {
        var singleId = await CreateMaterialAsync("Группа ХК " + Suffix(), new List<string> { "Гамма" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var created = await s.HK.CreateAsync(BuildCard(nodeId, unitId, singleId));
        Assert.NotNull(created);
        Assert.True(await s.Db.HKCardItemMaterials.AnyAsync(r => r.GsmMaterialId == singleId));
    }

    [Fact]
    public async Task HkUpdate_WithSingleSubgroupMaterial_Succeeds_AndPersists()
    {
        // Граница запрета: марка с одной подгруппой ограничением не отклоняется,
        // а правка действительно сохраняется. Тест требовал успеха UpdateAsync и
        // подтверждает результат новым чтением — прежняя версия ловила
        // InvalidOperationException и тем самым разрешала сломанное сохранение.
        var singleId = await CreateMaterialAsync("Группа ХК " + Suffix(), new List<string> { "Гамма" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        Guid cardId;
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            cardId = (await s.HK.CreateAsync(BuildCard(nodeId, unitId, singleId))).Id;
        }

        await using (var s2 = _fixture.CreateScope())
        {
            SetRefEditor(s2);
            var card = await s2.HK.GetByIdAsync(cardId);
            Assert.NotNull(card);
            card!.Notes = "Проверка";

            var updated = await s2.HK.UpdateAsync(card);
            Assert.NotNull(updated);
        }

        await using var s3 = _fixture.CreateScope();
        var saved = await s3.HK.GetByIdAsync(cardId);
        Assert.NotNull(saved);
        Assert.Equal("Проверка", saved!.Notes);
        Assert.Single(saved.Items.SelectMany(i => i.Materials));
    }

    // ── §5: нет запросов после commit ─────────────────────────────────────

    [Fact]
    public async Task Create_BuildsResponseBeforeCommit_NoReadAfterCommit()
    {
        // Прежняя реализация после commit читала карточку из БД (в том числе
        // набор классификаций). Сбой на этом чтении превращал бы успешное
        // сохранение в ложный отказ. Теперь ответа достаточно из памяти, поэтому
        // вооружённый маркер не срабатывает ВООБЩЕ.
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        FailingCommandInterceptor.ArmAt("FROM \"GsmMaterialClassifications\"", occurrence: 1);
        try
        {
            var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
            {
                Name = "Ответ из памяти " + Suffix(),
                Nd = "ГОСТ 42",
                GroupName = "Группа",
                SubgroupNames = new List<string> { "Подгруппа" },
            });

            Assert.NotEqual(Guid.Empty, view.Id);
            Assert.Equal("Группа", view.GroupName);
            Assert.Equal(new[] { "Подгруппа" }, view.SubgroupNames);
            Assert.False(FailingCommandInterceptor.Fired,
                "После commit выполнено чтение набора классификаций — отчёт мог бы оказаться ложным отказом");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        // Данные действительно записаны.
        await using var s2 = _fixture.CreateScope();
        Assert.True(await s2.Db.GsmMaterials.AnyAsync(m => m.Nd == "ГОСТ 42"));
    }

    [Fact]
    public async Task Update_ReturnsCard_AndRollsBackWhenClassificationWriteFails()
    {
        var id = await CreateMaterialAsync("Группа отката", new List<string> { "Старая" });

        // Счётчик audit до попытки: подтверждаем, что запись действительно
        // появилась бы, иначе проверка «ничего не записано» ничего не значила бы.
        var auditsBefore = await CountAuditsAsync(id);
        Assert.Equal(1, auditsBefore); // создание марки

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            FailingCommandInterceptor.ArmAt("INSERT INTO \"GsmMaterialClassifications\"", occurrence: 1);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
                    {
                        Name = "Откат " + Suffix(),
                        GroupName = "Группа отката",
                        SubgroupNames = new List<string> { "Новая-1", "Новая-2" },
                    }));
                Assert.True(FailingCommandInterceptor.Fired, "Контролируемый сбой не сработал");
            }
            finally
            {
                FailingCommandInterceptor.Disarm();
            }
        }

        // Ни марка, ни классификации, ни audit не изменились.
        await using var s2 = _fixture.CreateScope();
        var rows = await s2.Db.GsmMaterialClassifications.AsNoTracking()
            .Where(c => c.GsmMaterialId == id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Старая", rows[0].SubgroupName);
        Assert.Equal("Старая", await ReadAsync(id, m => m.Type));
        Assert.Equal(auditsBefore, await CountAuditsAsync(id));
    }

    private async Task<int> CountAuditsAsync(Guid materialId)
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityType == "GsmMaterial" && a.EntityId == materialId.ToString());
    }

    // ── Фикстуры данных ────────────────────────────────────────────────────

    private async Task<Guid> CreateMaterialAsync(string group, List<string> subgroups)
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        var created = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Марка " + Suffix(),
            GroupName = group,
            SubgroupNames = subgroups,
        });
        return created.Id;
    }

    /// <summary>Legacy-марка с заданным историческим Type: классификация снимается,
    /// Type пишется напрямую — так выглядит состояние до перехода.</summary>
    private async Task<Guid> CreateLegacyMaterialAsync(string name, string historicalType)
    {
        var id = await CreateMaterialAsync("Группа " + Suffix(), new List<string> { "Подгруппа" });
        await using var s = _fixture.CreateScope();
        var rows = await s.Db.GsmMaterialClassifications.Where(c => c.GsmMaterialId == id).ToListAsync();
        s.Db.GsmMaterialClassifications.RemoveRange(rows);
        await s.Db.SaveChangesAsync();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""Type"" = {historicalType} WHERE ""Id"" = {id}");
        return id;
    }

    private async Task<string?> ReadAsync(Guid id, Func<GsmMaterial, string?> selector)
    {
        await using var s = _fixture.CreateScope();
        var material = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
        return selector(material);
    }

    private static async Task<string?> ColumnLengthAsync(TestScope s, string table, string column)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT character_maximum_length::text FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column";
        cmd.Parameters.Add(CreateParameter(cmd, "@table", table));
        cmd.Parameters.Add(CreateParameter(cmd, "@column", column));
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static System.Data.Common.DbParameter CreateParameter(
        System.Data.Common.DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }

    // ── ХК: карточка со строками материалов ───────────────────────────────

    private async Task<(Guid NodeId, Guid UnitId)> CreateNodeAndUnitAsync()
    {
        await using var s = _fixture.CreateScope();
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        return (node.Id, unit.Id);
    }

    private static HKCard BuildCard(Guid nodeId, Guid unitId, params Guid[] materialIds) => new()
    {
        ObjectLevel = HKObjectLevel.Node,
        NodeId = nodeId,
        Purpose = "Проверка переходной совместимости",
        NormativeBasis = "ГОСТ",
        Items = new List<HKCardItem>
        {
            new()
            {
                AssemblyUnitId = unitId,
                Quantity = 1,
                Volume = 1m,
                UnitOfMeasure = "кг",
                SortOrder = 1,
                Materials = materialIds
                    .Select(id => new HKCardItemMaterial
                    {
                        Id = Guid.NewGuid(),
                        GsmMaterialId = id,
                        Category = GsmCategory.Primary,
                    })
                    .ToList(),
            },
        },
    };
}
