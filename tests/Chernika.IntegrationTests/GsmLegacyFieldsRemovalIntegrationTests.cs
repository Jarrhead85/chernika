using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Завершение перехода ГСМ на новые поля (PR-6, фаза B).
/// <para>
/// Прежний файл <c>GsmLegacyFieldSyncIntegrationTests</c> проверял переходный
/// режим: зеркала <c>Type</c>/<c>Gost</c>/<c>Description</c> и триггер
/// <c>TRG_GsmMaterials_LegacyFieldSync</c>. Всё это удалено, поэтому и проверять
/// нечего. Тесты переходного режима заменены проверками ОБРАТНОГО свойства:
/// переходных колонок и триггера в схеме больше нет, а три других триггера,
/// висевшие на тех же таблицах, уцелели — иначе удаление снесло бы
/// <c>TRG_GsmMaterials_ForeignFlagGuard</c> вместе с проверкой номенклатуры.
/// </para>
/// <para>
/// Сохранённые здесь проверки (независимость от имён, классификация, правка без
/// потери полей, марка без классификации) поведения не потеряли и продолжают
/// выполняться.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmLegacyFieldsRemovalIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmLegacyFieldsRemovalIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SetRefEditor(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── 1. Переходные артефакты удалены из схемы и из кода ────────────────

    [Fact]
    public void LegacyOneWayReconciliation_IsRemoved_FromRuntime()
    {
        var serviceType = typeof(GsmMaterialService);

        // Метод удалён из runtime-кода: после переключения источника истины
        // запустить сверку «legacy побеждает» нельзя ни случайно, ни намеренно.
        Assert.DoesNotContain(
            serviceType.GetMethods(),
            m => m.Name.Contains("Reconcile", StringComparison.Ordinal));

        // Тип-подтверждение также удалён: точка отзыва из PR-2 сработала.
        var domainAssembly = typeof(GsmMaterial).Assembly;
        Assert.DoesNotContain(
            domainAssembly.GetTypes(),
            t => t.Name.Contains("GsmLegacySourceOfTruth", StringComparison.Ordinal));
    }

    [Fact]
    public void GsmMaterialEntity_NoLongerDeclaresLegacyColumns()
    {
        // Колонки удалены не только из БД, но и из модели: иначе приложение
        // упало бы на первой же выборке марки.
        var properties = typeof(GsmMaterial).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Type", properties);
        Assert.DoesNotContain("Gost", properties);
        Assert.DoesNotContain("Description", properties);

        // Источник истины на месте.
        Assert.Contains("Nd", properties);
        Assert.Contains("IntendedUse", properties);
    }

    [Fact]
    public void GsmMaterialWriteRequest_DoesNotCarryRelations()
    {
        // Связи ведутся во втором справочнике (PR-4) отдельными методами сервиса.
        // Запись марки НЕ должна менять связи, иначе форма марки могла бы
        // подменить справочные данные.
        var writeRequest = typeof(GsmMaterialWriteRequest);
        Assert.DoesNotContain(
            writeRequest.GetProperties(),
            p => p.Name.Contains("Relation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Database_LegacyColumnsAndTrigger_AreGone()
    {
        await using var s = _fixture.CreateScope();

        Assert.Equal(0, await CountAsync(s, @"
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'GsmMaterials'
              AND column_name IN ('Type','Gost','Description')"));

        Assert.Equal(0, await CountAsync(s, @"
            SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            WHERE NOT t.tgisinternal
              AND t.tgname = 'TRG_GsmMaterials_LegacyFieldSync'"));

        // Функция триггера удалена вместе с ним: висячая функция означала бы, что
        // миграция сняла половину своего предмета.
        Assert.Equal(0, await CountAsync(s,
            "SELECT count(*) FROM pg_proc WHERE proname = 'fn_gsm_legacy_field_sync'"));
    }

    [Fact]
    public async Task Database_OtherTriggersAndConstraints_Survived()
    {
        await using var s = _fixture.CreateScope();

        // Триггеры, которые обязаны были уцелеть. Список неполный намеренно:
        // он фиксирует те, что легко снести вместе с переходными.
        var mustSurvive = new[]
        {
            // Проверяет флаг номенклатуры — висит на ТОЙ ЖЕ таблице,
            // что и снятый переходный триггер.
            "TRG_GsmMaterials_ForeignFlagGuard",
            "TRG_GsmMaterialClassifications_SingleGroup",
            "TRG_GsmMaterialRelations_ForeignCheck",
        };
        foreach (var name in mustSurvive)
        {
            var found = await CountAsync(s,
                "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid "
                + "WHERE NOT t.tgisinternal AND t.tgname = '" + name + "'");
            Assert.True(found == 1, name + " должен был уцелеть, найдено: " + found);
        }

        // FK на марку остался RESTRICT: удаление марки со ссылкой в строке ХК
        // по-прежнему запрещено.
        Assert.Equal(1, await CountAsync(s, @"
            SELECT count(*) FROM pg_constraint
            WHERE conname = 'FK_HKCardItemMaterials_GsmMaterials_GsmMaterialId'
              AND contype = 'f' AND confdeltype = 'r'"));

        // Частичные уникальные индексы справочных таблиц на месте.
        Assert.Equal(1, await CountAsync(s, @"
            SELECT count(*) FROM pg_indexes
            WHERE indexname = 'UX_GsmMaterialRelations_ActivePair'"));
        Assert.Equal(1, await CountAsync(s, @"
            SELECT count(*) FROM pg_indexes
            WHERE indexname = 'UX_GsmMaterialClassifications_GroupSubgroup'"));
    }

    [Fact]
    public async Task DirectSql_WritesNd_AreNoLongerMirroredAnywhere()
    {
        // Прежде правка Gost прямым SQL подхватывалась триггером и обновляла Nd.
        // Теперь прежнего поля нет, а значит и такого канала синхронизации.
        // Проверяется, что прямая запись Nd работает как обычная: сервис её не
        // перетирает, и никакой другой триггер в это не вмешивается.
        var id = await CreateMaterialAsync("ГОСТ 1", null);
        await SetNdAsync(id, "НД изменённый напрямую");
        Assert.Equal("НД изменённый напрямую", await ReadAsync(id, m => m.Nd));

        await SetIntendedUseAsync(id, "Назначение изменённое напрямую");
        var material = await ReadMaterialAsync(id);
        Assert.Equal("НД изменённый напрямую", material.Nd);
        Assert.Equal("Назначение изменённое напрямую", material.IntendedUse);
    }

    // ── 2. Запись и правка марки работают на новых полях ───────────────────

    [Fact]
    public async Task SaveAsync_WritesNewFields_AndClassification()
    {
        var id = await CreateMaterialAsync("ГОСТ 21743-76", "Для турбинных двигателей");

        Assert.Equal("ГОСТ 21743-76", await ReadAsync(id, m => m.Nd));
        Assert.Equal("Для турбинных двигателей", await ReadAsync(id, m => m.IntendedUse));

        // Классификация записывается отдельными строками и не зависит от
        // удалённой колонки Type.
        await using var s = _fixture.CreateScope();
        var classification = await s.Db.GsmMaterialClassifications
            .AsNoTracking()
            .SingleAsync(c => c.GsmMaterialId == id);
        Assert.Equal("Моторные масла", classification.GroupName);
        Assert.Equal("Для дизельных двигателей", classification.SubgroupName);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotLoseFields()
    {
        var id = await CreateMaterialAsync("ГОСТ 21743-76", "Для турбинных двигателей");

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var view = await s.GsmMaterials.GetEditViewAsync(id);
            Assert.NotNull(view);

            // Правка одного поля не должна обнулять остальные: обновление идёт
            // по полной модели записи, а не по мутации частичной сущности.
            var result = await s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = view!.Name,
                Nd = "ГОСТ 12345-99",
                InGostNomenclature = view.InGostNomenclature,
                IntendedUse = view.IntendedUse,
                SuitabilityGround = true,
                SuitabilityAir = false,
                SuitabilitySea = false,
                NatoIndex = "A-00",
                Note = "Примечание сохранённое",
                GroupName = view.GroupName,
                SubgroupNames = view.SubgroupNames.ToList(),
            });
            Assert.NotNull(result);
        }

        Assert.Equal("ГОСТ 12345-99", await ReadAsync(id, m => m.Nd));
        Assert.Equal("Для турбинных двигателей", await ReadAsync(id, m => m.IntendedUse));
        Assert.Equal("Примечание сохранённое", await ReadAsync(id, m => m.Note));
        Assert.Equal("A-00", await ReadAsync(id, m => m.NatoIndex));
        Assert.True(await ReadAsync(id, m => m.SuitabilityGround));
    }

    // ── 3. Марка без классификации остаётся читаемой и редактируемой ──────

    [Fact]
    public async Task LegacyMaterial_WithoutClassification_StaysReadable_AndEditable()
    {
        // Марка, созданная до перехода: классификации нет.
        var id = await CreateMaterialAsync("ГОСТ legacy", "Прежнее описание", classified: false);

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var view = await s.GsmMaterials.GetEditViewAsync(id);
            Assert.NotNull(view);
            Assert.Null(view!.GroupName);
            Assert.Empty(view.SubgroupNames);
        }

        // Простая правка НД/назначения/примечания не выдумывает группу.
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var result = await s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Legacy марка переименована",
                Nd = "ГОСТ legacy 2",
                IntendedUse = "Новое описание",
                Note = "Примечание",
            });
            Assert.NotNull(result);
            Assert.Null(result!.GroupName);
        }

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var view = await s.GsmMaterials.GetEditViewAsync(id);
            Assert.NotNull(view);
            Assert.Null(view!.GroupName);
            Assert.Equal("ГОСТ legacy 2", view.Nd);
            Assert.Equal("Новое описание", view.IntendedUse);
            Assert.Equal("Примечание", view.Note);
        }

        // И остаётся видимой в сводном справочнике как «без классификации».
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var page = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery
            {
                Search = "Legacy марка переименована",
            });
            var row = Assert.Single(page.Items, x => x.Id == id);
            Assert.False(row.HasClassification);
        }
    }

    // ── Фикстуры данных ────────────────────────────────────────────────────

    private async Task<Guid> CreateMaterialAsync(
        string nd, string? intendedUse, bool classified = true)
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        // Марка всегда создаётся классифицированной: сервис не примет публикацию
        // без группы. Состояние «до перехода» получаем снятием классификации
        // напрямую.
        var created = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Марка " + Suffix(),
            Nd = nd,
            IntendedUse = intendedUse,
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Для дизельных двигателей" },
        });

        if (!classified)
        {
            var classifications = await s.Db.GsmMaterialClassifications
                .Where(c => c.GsmMaterialId == created.Id).ToListAsync();
            s.Db.GsmMaterialClassifications.RemoveRange(classifications);
            await s.Db.SaveChangesAsync();
        }

        return created.Id;
    }

    // Каждый помощник меняет ровно одно поле. ExecuteSqlInterpolated превращает
    // в параметр каждую дырку, поэтому SQL собирается в каждом методе отдельно.

    private async Task SetNdAsync(Guid id, string? value)
    {
        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""Nd"" = {value} WHERE ""Id"" = {id}");
    }

    private async Task SetIntendedUseAsync(Guid id, string? value)
    {
        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""IntendedUse"" = {value} WHERE ""Id"" = {id}");
    }

    private async Task<GsmMaterial> ReadMaterialAsync(Guid id)
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
    }

    private async Task<string?> ReadAsync(Guid id, Func<GsmMaterial, string?> selector)
        => selector(await ReadMaterialAsync(id));

    private async Task<bool> ReadAsync(Guid id, Func<GsmMaterial, bool> selector)
        => selector(await ReadMaterialAsync(id));

    private static async Task<long> CountAsync(TestScope s, string sql)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
