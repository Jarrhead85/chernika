using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Совместимость перехода на новые поля ГСМ (PR-3).
/// <para>
/// Источником записи стали <c>Nd</c>/<c>IntendedUse</c> и классификации, а
/// прежние <c>Type</c>/<c>Gost</c>/<c>Description</c> остались зеркалами для
/// legacy-потребителей. Здесь закреплено: зеркала не расходятся с новыми полями,
/// переходный триггер не перетирает явно заданные значения, а однонаправленная
/// сверка «legacy побеждает» после переключения недоступна для вызова.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmLegacyFieldSyncIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmLegacyFieldSyncIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SetRefEditor(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── 1. Однонаправленная сверка больше недоступна ───────────────────────

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
    public void GsmMaterialWriteRequest_DoesNotCarryRelations()
    {
        // Связи ведутся во втором справочнике (PR-4) отдельными методами сервиса.
        // Этот тест фиксирует разделением ответственности: запись марки НЕ должна
        // менять связи, иначе форма марки могла бы подменить справочные данные.
        // Прежняя формулировка «у сервиса вообще нет методов связей» была границей
        // PR-3 и снята именно этим PR.
        var writeRequest = typeof(Chernika.Domain.Models.GsmMaterialWriteRequest);
        Assert.DoesNotContain(
            writeRequest.GetProperties(),
            p => p.Name.Contains("Relation", StringComparison.OrdinalIgnoreCase));
    }

    // ── 2. Legacy-поля больше не заполняются сервисом ──────────────────────

    [Fact]
    public async Task SaveAsync_WritesNewFields_AndLeavesLegacyColumnsEmpty()
    {
        var id = await CreateMaterialAsync("ГОСТ 21743-76", "Для турбинных двигателей");

        // Источник истины записан полностью.
        Assert.Equal("ГОСТ 21743-76", await ReadAsync(id, m => m.Nd));
        Assert.Equal("Для турбинных двигателей", await ReadAsync(id, m => m.IntendedUse));

        // PR-5: Gost/Description больше не пишутся — действующих читателей у них
        // не осталось, а колонки удаляются вместе с ними в PR-6.
        Assert.Null(await ReadAsync(id, m => m.Gost));
        Assert.Null(await ReadAsync(id, m => m.Description));

        // Отчёт о расхождении Gost↔Nd удалён в фазе A PR-6 как неверный
        // критерий: он измерял состояние ДАННЫХ, а спрашивал про КОД. Проверка
        // готовности к удалению колонок — карта обращений и тесты, см.
        // GsmInventorySchemaIntegrationTests.

        // Type продолжает заполняться: колонка NOT NULL до PR-6.
        Assert.False(string.IsNullOrWhiteSpace(await ReadAsync(id, m => m.Type)));
    }

    [Fact]
    public async Task UpdateAsync_DoesNotTouchLegacyColumns_AndDoesNotLoseFields()
    {
        var id = await CreateMaterialAsync("ГОСТ 21743-76", "Для турбинных двигателей");

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var view = await s.GsmMaterials.GetEditViewAsync(id);
            Assert.NotNull(view);

            // Правка одного поля не должна обнулять остальные: обновление идёт
            // по полной модели записи, а не по мутации частичной сущности.
            var result = await s.GsmMaterials.UpdateAsync(id, new Chernika.Domain.Models.GsmMaterialWriteRequest
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

        // Переходные колонки остались пустыми: правка НД их не «подтянула».
        Assert.Null(await ReadAsync(id, m => m.Gost));
        Assert.Null(await ReadAsync(id, m => m.Description));
    }

    // ── 3. Переходный триггер не перетирает явные новые значения ───────────

    [Fact]
    public async Task Trigger_RespectsExplicitNewFieldWrites()
    {
        // Меняется только прежнее поле — новое следует за ним (переходный режим).
        var a = await CreateMaterialAsync("ГОСТ 1", null);
        await SetGostAsync(a, "ГОСТ изменённый");
        Assert.Equal("ГОСТ изменённый", await ReadAsync(a, m => m.Nd));

        // Меняется только новое поле — триггер его не перетирает.
        var b = await CreateMaterialAsync("ГОСТ 2", null);
        await SetNdAsync(b, "НД явное");
        Assert.Equal("НД явное", await ReadAsync(b, m => m.Nd));

        // Меняются оба в одном запросе — оба сохраняются как заданы.
        var c = await CreateMaterialAsync("ГОСТ 3", null);
        await SetGostAndNdAsync(c, "ГОСТ явный", "НД явное");
        Assert.Equal("ГОСТ явный", await ReadAsync(c, m => m.Gost));
        Assert.Equal("НД явное", await ReadAsync(c, m => m.Nd));

        // Меняется только прежнее описание — назначение следует за ним.
        var d = await CreateMaterialAsync("ГОСТ 4", "Прежнее");
        await SetDescriptionAsync(d, "Новое описание");
        Assert.Equal("Новое описание", await ReadAsync(d, m => m.IntendedUse));

        // Меняется только новое назначение — не перетирается.
        var e = await CreateMaterialAsync("ГОСТ 5", "Прежнее");
        await SetIntendedUseAsync(e, "Назначение явное");
        Assert.Equal("Назначение явное", await ReadAsync(e, m => m.IntendedUse));
    }

    [Fact]
    public async Task LegacyGostUpdate_ThroughOldServiceChannel_SyncsNd()
    {
        // Правка прежнего поля прямым SQL — канал, которым пользуются ещё не
        // переведённые потребители, — не оставляет Nd отстающим.
        var id = await CreateMaterialAsync("ГОСТ старый", null);
        await SetGostAsync(id, "ГОСТ новый");
        Assert.Equal("ГОСТ новый", await ReadAsync(id, m => m.Nd));
    }

    [Fact]
    public async Task Trigger_Exists_AfterMigration()
    {
        await using var s = _fixture.CreateScope();
        Assert.Equal(1, await CountAsync(s,
            @"SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
              WHERE NOT t.tgisinternal AND c.relname = 'GsmMaterials'
                AND t.tgname = 'TRG_GsmMaterials_LegacyFieldSync'"));
    }

    // ── 4. Legacy-марка без классификации ──────────────────────────────────

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
            var result = await s.GsmMaterials.UpdateAsync(id, new Chernika.Domain.Models.GsmMaterialWriteRequest
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
            var page = await s.GsmMaterials.GetPagedAsync(new Chernika.Domain.Models.GsmMaterialQuery
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
        // без группы. Legacy-состояние получаем снятием классификации напрямую.
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

    private async Task SetGostAsync(Guid id, string? value)
    {
        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""Gost"" = {value} WHERE ""Id"" = {id}");
    }

    private async Task SetDescriptionAsync(Guid id, string? value)
    {
        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""Description"" = {value} WHERE ""Id"" = {id}");
    }

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

    private async Task SetGostAndNdAsync(Guid id, string? gost, string? nd)
    {
        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""Gost"" = {gost}, ""Nd"" = {nd} WHERE ""Id"" = {id}");
    }

    private async Task<string?> ReadAsync(Guid id, Func<GsmMaterial, string?> selector)
    {
        await using var s = _fixture.CreateScope();
        var material = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
        return selector(material);
    }

    private async Task<bool> ReadAsync(Guid id, Func<GsmMaterial, bool> selector)
    {
        await using var s = _fixture.CreateScope();
        var material = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
        return selector(material);
    }

    private static async Task<long> CountAsync(TestScope s, string sql)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
