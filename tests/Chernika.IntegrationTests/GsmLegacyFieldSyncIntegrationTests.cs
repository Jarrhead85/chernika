using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Reconciliation переходных полей ГСМ (дополнение к PR-2).
/// <para>
/// Дефект: бэкфилл переносит <c>Gost → Nd</c> только один раз
/// (<c>WHERE "Nd" IS NULL</c>), а штатный UI и API продолжают писать <c>Gost</c>.
/// Поэтому после любого редактирования прежнего поля новое поле отстаёт, а повтор
/// того же бэкфилла расхождение не устраняет.
/// </para>
/// <para>
/// Закрывается двумя механизмами: <c>TRG_GsmMaterials_LegacyFieldSync</c> не даёт
/// окну открыться (новое поле следует за прежним), а сервисная сверка
/// <c>ReconcileTransitionFieldsAsync</c> — обязательный gate перед переключением
/// источника истины на <c>Nd</c>.
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

    // ── Сценарий из замечания к приёмке ───────────────────────────────────

    [Fact]
    public async Task EditLegacyGost_NewNdEqualsLastSavedGost_WithoutManualStep()
    {
        // Марка создаётся прежним путём: сервис пишет только Gost/Description.
        var id = await CreateMaterialAsync();
        Assert.Equal("ГОСТ первоначальный", await ReadAsync(id, m => m.Nd));

        // Пользователь правит ГОСТ ровно тем каналом, которым пользуется UI/API.
        // Требование приёмки: новое НД должно равняться последнему сохранённому ГОСТ.
        await UpdateGostThroughLegacyServiceAsync(id, "ГОСТ после правки");

        Assert.Equal("ГОСТ после правки", await ReadAsync(id, m => m.Gost));
        Assert.Equal("ГОСТ после правки", await ReadAsync(id, m => m.Nd));

        // Отчёт пуст, а сверка — уже ничего не делает: расхождения не возникает.
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            Assert.Empty(await s.GsmMaterials.GetTransitionDivergencesAsync());
            var result = await s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);
            Assert.Equal(0, result.TotalFixed);
        }
    }

    [Fact]
    public async Task HistoricalDivergence_AfterLegacyGostEdit_ThenReconcile_NdEqualsLastSavedGost()
    {
        var id = await CreateMaterialAsync();
        await UpdateGostThroughLegacyServiceAsync(id, "ГОСТ после правки");

        // Воспроизводим накопленное расхождение, как будто правка произошла до
        // установки защиты: Nd откатываем к прежнему значению.
        await ForceNdAsync(id, "ГОСТ первоначальный");

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        // Расхождение видно отчёту и указывает, что запишет сверка.
        var divergence = Assert.Single(await s.GsmMaterials.GetTransitionDivergencesAsync(), d => d.Id == id);
        Assert.True(divergence.NdDiffers);
        Assert.Equal("ГОСТ после правки", divergence.ExpectedNd);

        var result = await s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);
        Assert.Equal(1, result.NdFixed);
        Assert.Contains(id, result.ChangedMaterialIds);

        // Новое НД равно последнему сохранённому ГОСТ, прежние поля не тронуты.
        Assert.Equal("ГОСТ после правки", await ReadAsync(id, m => m.Nd));
        Assert.Equal("ГОСТ после правки", await ReadAsync(id, m => m.Gost));

        // Gate закрыт: расхождений не осталось.
        Assert.Empty(await s.GsmMaterials.GetTransitionDivergencesAsync());
    }

    [Fact]
    public async Task OldBackfillSql_DoesNotFixDivergence_ButReconciliationDoes()
    {
        var id = await CreateMaterialAsync();
        await UpdateGostThroughLegacyServiceAsync(id, "ГОСТ обновлённый");
        await ForceNdAsync(id, "ГОСТ устаревшее");

        // Прежний бэкфилл (как в применённой миграции) расхождение НЕ устраняет:
        // условие WHERE "Nd" IS NULL уже не выполняется.
        await RunOldBackfillSqlAsync();
        Assert.Equal("ГОСТ устаревшее", await ReadAsync(id, m => m.Nd));

        // Сверка приводит новое поле к последнему сохранённому прежнему.
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        await s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);
        Assert.Equal("ГОСТ обновлённый", await ReadAsync(id, m => m.Nd));
    }

    // ── Триггер: окно расхождения не открывается ──────────────────────────

    [Fact]
    public async Task LegacyGostUpdate_SyncsNdImmediately()
    {
        var id = await CreateMaterialAsync();

        await UpdateGostThroughLegacyServiceAsync(id, "ГОСТ синхронизирован");

        // НД следует за прежним полем сразу, без отдельной операции сверки.
        Assert.Equal("ГОСТ синхронизирован", await ReadAsync(id, m => m.Nd));

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        Assert.Empty(await s.GsmMaterials.GetTransitionDivergencesAsync());
    }

    [Fact]
    public async Task LegacyDescriptionUpdate_SyncsIntendedUse()
    {
        var id = await CreateMaterialAsync();

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        var material = await s.Db.GsmMaterials.FirstAsync(m => m.Id == id);
        material.Description = "Новое назначение";
        await s.Db.SaveChangesAsync();

        Assert.Equal("Новое назначение", await ReadAsync(id, m => m.IntendedUse));
    }

    [Fact]
    public async Task ExplicitNdWrite_IsNotOverriddenByTrigger()
    {
        // Явная запись нового поля (например, инструментом будущего PR) не
        // перехватывается: триггер уважает изменённое значение.
        var id = await CreateMaterialAsync();

        await using (var s = _fixture.CreateScope())
        {
            await s.Db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE ""GsmMaterials"" SET ""Nd"" = 'НД задано явно' WHERE ""Id"" = {id}");
        }

        Assert.Equal("НД задано явно", await ReadAsync(id, m => m.Nd));

        // Правка прежнего поля при этом не затирает явно заданное Nd:
        // расхождение фиксируется отчётом, а не перетирается молча.
        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);
        var divergences = await s2.GsmMaterials.GetTransitionDivergencesAsync();
        Assert.Contains(divergences, d => d.Id == id && d.NdDiffers);

        // Снимаем намеренно созданное расхождение, чтобы общая БД оставалась чистой.
        await ClearDivergencesAsync();
    }

    [Fact]
    public async Task Insert_WithoutExplicitNd_DerivesNdFromGost()
    {
        var id = await CreateMaterialAsync();
        Assert.Equal("ГОСТ первоначальный", await ReadAsync(id, m => m.Nd));
    }

    [Fact]
    public async Task Insert_WithBlankGost_LeavesNdNull()
    {
        // Правило NULLIF(btrim(...), '') соблюдается и на вставке.
        await using var s = _fixture.CreateScope();
        s.Db.GsmMaterials.Add(new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "Марка " + Suffix(),
            Type = "Тестовая " + Suffix(),
            Gost = "   ",
        });
        await s.Db.SaveChangesAsync();

        var id = await s.Db.GsmMaterials.Where(m => m.Gost == "   ").Select(m => m.Id).FirstAsync();
        Assert.Null(await ReadAsync(id, m => m.Nd));
    }

    [Fact]
    public async Task Trigger_Exists_AfterMigration()
    {
        await using var s = _fixture.CreateScope();
        Assert.Equal(1, await CountAsync(s,
            @"SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
              WHERE NOT t.tgisinternal AND c.relname = 'GsmMaterials'
                AND t.tgname = 'TRG_GsmMaterials_LegacyFieldSync'"));
        Assert.Equal(1, await CountAsync(s,
            @"SELECT count(*) FROM pg_proc WHERE proname = 'fn_gsm_legacy_field_sync'"));
    }

    // ── Отчёт и сверка ────────────────────────────────────────────────────

    [Fact]
    public async Task DivergenceReport_CoversBothFields_AndSoftDeletedRows()
    {
        var gostCase = await CreateMaterialAsync();
        var descriptionCase = await CreateMaterialAsync();
        var deletedCase = await CreateMaterialAsync();

        // Расхождение по НД, по назначению и на soft-deleted марке.
        await ForceNdAsync(gostCase, "Устаревшее НД");
        await ForceIntendedUseAsync(descriptionCase, "Устаревшее назначение");
        await ForceNdAsync(deletedCase, "Устаревшее НД");

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var material = await s.Db.GsmMaterials.FirstAsync(m => m.Id == deletedCase);
            material.IsDeleted = true;
            material.DeletedAt = DateTime.UtcNow;
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);
        var divergences = await s2.GsmMaterials.GetTransitionDivergencesAsync();

        var byGost = divergences.Single(d => d.Id == gostCase);
        Assert.True(byGost.NdDiffers);
        Assert.False(byGost.IntendedUseDiffers);
        Assert.Equal("ГОСТ первоначальный", byGost.ExpectedNd);

        var byDescription = divergences.Single(d => d.Id == descriptionCase);
        Assert.True(byDescription.IntendedUseDiffers);
        Assert.False(byDescription.NdDiffers);

        // Soft-deleted марки не скрываются query-фильтром: их прежние поля тоже
        // участвуют в переносе, иначе после восстановления появится расхождение.
        var byDeleted = divergences.Single(d => d.Id == deletedCase);
        Assert.True(byDeleted.IsDeleted);
        Assert.True(byDeleted.NdDiffers);

        SetRefEditor(s2);
        var result = await s2.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);

        Assert.Equal(2, result.NdFixed);
        Assert.Equal(1, result.IntendedUseFixed);
        Assert.Equal(3, result.ChangedMaterialIds.Count);
        Assert.Empty(await s2.GsmMaterials.GetTransitionDivergencesAsync());
    }

    [Fact]
    public async Task Reconciliation_ClearsNewField_WhenLegacyFieldBecameEmpty()
    {
        var id = await CreateMaterialAsync();

        // Прежнее поле очистили (прежний сервис пишет null при пустом вводе).
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var material = await s.Db.GsmMaterials.FirstAsync(m => m.Id == id);
            material.Gost = null;
            await s.Db.SaveChangesAsync();
        }

        // Триггер уже обнулил Nd — расхождения нет.
        Assert.Null(await ReadAsync(id, m => m.Nd));
        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);
        Assert.Empty(await s2.GsmMaterials.GetTransitionDivergencesAsync());

        // Если Nd всё же остался (правка до установки защиты) — сверка очистит его.
        await ForceNdAsync(id, "Оставшееся НД");
        await using var s3 = _fixture.CreateScope();
        SetRefEditor(s3);
        var result = await s3.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);

        Assert.Equal(1, result.NdCleared);
        Assert.Equal(0, result.NdFixed);
        Assert.Null(await ReadAsync(id, m => m.Nd));
    }

    [Fact]
    public async Task Reconciliation_IsIdempotent_SecondRunChangesNothing()
    {
        var id = await CreateMaterialAsync();
        await ForceNdAsync(id, "Устаревшее НД");

        // Первым запуском снимаем расхождение этой марки и любой остаток,
        // оставшийся в общей БД, — результат не зависит от порядка тестов.
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            await s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);
        }

        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);
        var second = await s2.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);

        Assert.Equal(0, second.TotalFixed);
        Assert.Empty(second.ChangedMaterialIds);
        Assert.DoesNotContain(id, second.ChangedMaterialIds);
        Assert.Equal("ГОСТ первоначальный", await ReadAsync(id, m => m.Nd));
    }

    [Fact]
    public async Task Reconciliation_RejectsUnconfirmedAcknowledgement()
    {
        var id = await CreateMaterialAsync();
        await ForceNdAsync(id, "Устаревшее НД");

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        // Подтверждение задаётся перечислением с единственным членом, а не просто
        // флагом: любое другое значение отвергается, а удаление члена перечисления
        // делает оставшиеся вызовы сверки некомпилируемыми.

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.ReconcileTransitionFieldsAsync((GsmLegacySourceOfTruth)999));
        Assert.Contains("источником истины", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Данные не тронуты, записи об успешной сверке нет.
        Assert.Equal("Устаревшее НД", await ReadAsync(id, m => m.Nd));
        Assert.False(await HasReconcileAuditAsync(id));

        await ClearDivergencesAsync();
    }

    [Fact]
    public async Task Reconciliation_WritesAudit_WithChangedIds()
    {
        var id = await CreateMaterialAsync();
        await ForceNdAsync(id, "Устаревшее НД");

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        await s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);

        await using var s2 = _fixture.CreateScope();
        Assert.True(await s2.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "GsmMaterial"
            && a.EntityId == id.ToString()
            && a.Action == "ReconcileTransitionFields"));
    }

    [Fact]
    public async Task DivergenceReport_DemandsAuthentication()
    {
        // Отчёт — чтение справочника, поэтому требует пользователя с правом
        // Reference.View; без аутентификации отказ.
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.GetTransitionDivergencesAsync());
    }

    [Fact]
    public async Task DivergenceReport_AllowedForViewer_ButReconcileDenied()
    {
        var id = await CreateMaterialAsync();
        await ForceNdAsync(id, "Устаревшее НД");

        await using var s = _fixture.CreateScope();

        // Собственный пользователь с явно запрещённым Reference.Edit: общие тесты
        // выдают гостю персональные override и не отзывают их, поэтому опираться
        // на фикстурного гостя здесь нельзя.
        var viewerId = await CreateViewerUserAsync(s);
        s.User.CurrentUserId = Guid.Parse(viewerId);

        // Отчёт read-only — доступен.
        var divergences = await s.GsmMaterials.GetTransitionDivergencesAsync();
        Assert.Contains(divergences, d => d.Id == id);

        // Сверка изменяет данные — недоступна.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth));

        await ClearDivergencesAsync();
    }

    /// <summary>Пользователь только для чтения справочника, созданный этим тестом.</summary>
    private async Task<string> CreateViewerUserAsync(TestScope s)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "gsm_viewer_" + Suffix(),
            FullName = "Проверка прав " + Suffix(),
            BranchId = _fixture.BranchA,
            IsActive = true,
        };
        Assert.True((await s.Users.CreateAsync(user)).Succeeded);
        Assert.True((await s.Users.AddToRoleAsync(user, nameof(UserRole.Guest))).Succeeded);

        // Явный запрет правки — тест не должен зависеть от состояния, которое
        // оставляют после себя другие тесты на общей фикстуре.
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionCode = PermissionCodes.ReferenceEdit,
            IsGranted = false,
            Reason = "Test",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
        return user.Id;
    }

    // ── Фикстуры данных ────────────────────────────────────────────────────

    /// <summary>Создаёт марку прежним путём (сервис пишет только Gost/Description).</summary>
    private async Task<Guid> CreateMaterialAsync()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        var material = await s.GsmMaterials.CreateAsync(new GsmMaterial
        {
            Name = "Марка " + Suffix(),
            Type = "Моторное масло " + Suffix(),
            Gost = "ГОСТ первоначальный",
            Description = "Назначение " + Suffix(),
        });
        return material.Id;
    }

    /// <summary>Правка прежнего поля ровно тем путём, которым пользуется UI.</summary>
    private async Task UpdateGostThroughLegacyServiceAsync(Guid id, string gost)
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        var material = await s.GsmMaterials.GetByIdAsync(id);
        Assert.NotNull(material);
        material!.Gost = gost;
        Assert.True(await s.GsmMaterials.UpdateAsync(material));
    }

    private async Task ForceNdAsync(Guid id, string? nd)
    {
        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""Nd"" = {nd} WHERE ""Id"" = {id}");
    }

    private async Task ForceIntendedUseAsync(Guid id, string? intendedUse)
    {
        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials"" SET ""IntendedUse"" = {intendedUse} WHERE ""Id"" = {id}");
    }

    private async Task<bool> HasReconcileAuditAsync(Guid id)
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "GsmMaterial"
            && a.Action == "ReconcileTransitionFields"
            && a.EntityId == id.ToString());
    }

    /// <summary>Снимает расхождения, намеренно оставленные тестом, чтобы общая БД
    /// оставалась чистой для последующих проверок «отчёт пуст».</summary>
    private async Task ClearDivergencesAsync()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        await s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);
    }

    private async Task RunOldBackfillSqlAsync()
    {
        await using var s = _fixture.CreateScope();
        // Ровно те два оператора, что были в применённой миграции PR-2.
        await s.Db.Database.ExecuteSqlRawAsync(@"
            UPDATE ""GsmMaterials""
            SET ""Nd"" = NULLIF(btrim(""Gost""), '')
            WHERE ""Nd"" IS NULL AND ""Gost"" IS NOT NULL;

            UPDATE ""GsmMaterials""
            SET ""IntendedUse"" = ""Description""
            WHERE ""IntendedUse"" IS NULL AND ""Description"" IS NOT NULL;");
    }

    private async Task<string?> ReadAsync(Guid id, Func<GsmMaterial, string?> selector)
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
