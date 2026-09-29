using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Атомарность сверки переходных полей ГСМ и её журнала аудита.
/// <para>
/// Дефект, найденный на приёмке: <c>AuditService.LogAsync</c> сам вызывает
/// <c>SaveChangesAsync</c>. Поэтому прежняя последовательность «сохранить данные,
/// потом писать журнал» при ошибке аудита оставляла исправленную марку без
/// записи в журнале, а сбой на третьей из пяти записей давал частичный журнал.
/// </para>
/// <para>
/// Исправление: <c>AuditService.CreateLogAsync</c> только добавляет запись в тот
/// же scoped <c>AppDbContext</c>, после чего данные и журнал уходят одной
/// операцией <c>SaveChangesAsync</c> в одной явной транзакции; <c>Commit</c>
/// выполняется только после успеха.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmReconciliationAtomicityIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmReconciliationAtomicityIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SetRefEditor(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    private const string AuditAction = "ReconcileTransitionFields";

    // Интерцептор сверяет текст команды подстрокой. Берём имя таблицы: префикс
    // "INSERT INTO" зависит от форматирования, а внутри окна сверки к AuditLogs
    // обращается только вставка журнала.
    private const string AuditInsertMarker = "AuditLogs";

    // ── 1. Ошибка записи журнала откатывает и данные ───────────────────────

    [Fact]
    public async Task Reconciliation_WhenAuditInsertFails_ChangesNothingAndWritesNoJournal()
    {
        var id = await CreateMaterialAsync();
        await SetNdAsync(id, "Устаревшее НД");

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);

            FailingCommandInterceptor.ArmAt(AuditInsertMarker, occurrence: 1);
            try
            {
                // DbUpdateException — обёртка внедрённого сбоя команды журнала.
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    s.GsmMaterials.ReconcileTransitionFieldsAsync(
                        GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth));

                // Признак внедрения читаем ДО Disarm: Disarm обнуляет AsyncLocal, и
                // Fired вернул бы устаревшее значение, скрыв отсутствие сбоя.
                Assert.True(FailingCommandInterceptor.Fired,
                    "Контролируемый сбой записи журнала не сработал");
            }
            finally
            {
                FailingCommandInterceptor.Disarm();
            }
        }

        // Проверяем БД в новом scope: данные на месте, журнала нет.
        await using var s2 = _fixture.CreateScope();
        var material = await s2.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
        Assert.Equal("Устаревшее НД", material.Nd);
        Assert.False(await HasReconcileAuditAsync(id));

        // Расхождение сохранилось — операция не «успела наполовину».
        SetRefEditor(s2);
        var divergence = Assert.Single(await s2.GsmMaterials.GetTransitionDivergencesAsync(), d => d.Id == id);
        Assert.True(divergence.NdDiffers);
        Assert.False(divergence.IntendedUseDiffers);

        await ClearDivergencesAsync();
    }

    // ── 2. Сбой записи журнала откатывает весь пакет ───────────────────────

    [Fact]
    public async Task Reconciliation_WhenJournalWriteFails_RollsBackWholeBatch()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = await CreateMaterialAsync();
            await SetNdAsync(id, $"Устаревшее НД {i}");
            ids.Add(id);
        }

        // Раньше каждая запись журнала была отдельным SaveChangesAsync, поэтому сбой
        // на третьей из пяти оставлял две записи в БД — частичный журнал. Теперь все
        // вставки журнала и все изменения данных идут одной командой в одной
        // транзакции. Проверить сбой ровно «второй из трёх» нельзя: EF/Npgsql
        // объединяет вставки в один batch, поэтому команда всего одна. Это же и
        // означает, что частичная запись журнала теперь структурно невозможна —
        // и именно это проверяется ниже: не остаётся ни одной записи и ни одного
        // изменения данных.
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);

            FailingCommandInterceptor.ArmAt(AuditInsertMarker, occurrence: 1);
            try
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    s.GsmMaterials.ReconcileTransitionFieldsAsync(
                        GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth));

                Assert.True(FailingCommandInterceptor.Fired,
                    "Контролируемый сбой записи журнала не сработал");
            }
            finally
            {
                FailingCommandInterceptor.Disarm();
            }
        }

        await using var s2 = _fixture.CreateScope();
        foreach (var id in ids)
        {
            var material = await s2.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
            Assert.StartsWith("Устаревшее НД", material.Nd);
            Assert.False(await HasReconcileAuditAsync(id));
        }

        await ClearDivergencesAsync();
    }

    // ── 3. Успешная сверка: правильные поля, по событию на марку, счётчики ──

    [Fact]
    public async Task Reconciliation_Success_WritesOneEventPerMaterial_AndTouchesOnlyExpectedFields()
    {
        var editorId = Guid.Parse(_fixture.NormAdminA.Id);
        var ndCase = await CreateMaterialAsync();
        var intendedUseCase = await CreateMaterialAsync();
        await SetNdAsync(ndCase, "Устаревшее НД");
        await SetIntendedUseAsync(intendedUseCase, "Устаревшее назначение");

        // Снимок прежних полей: их сверка не имеет права менять.
        string nameBefore, typeBefore, gostBefore, descriptionBefore;
        await using (var s = _fixture.CreateScope())
        {
            var m = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(x => x.Id == ndCase);
            nameBefore = m.Name;
            typeBefore = m.Type;
            gostBefore = m.Gost!;
            descriptionBefore = m.Description!;
        }

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var result = await s.GsmMaterials.ReconcileTransitionFieldsAsync(
                GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);

            Assert.Equal(1, result.NdFixed);
            Assert.Equal(1, result.IntendedUseFixed);
            Assert.Equal(0, result.NdCleared);
            Assert.Equal(0, result.IntendedUseCleared);
            Assert.Equal(2, result.ChangedMaterialIds.Count);
            Assert.Contains(ndCase, result.ChangedMaterialIds);
            Assert.Contains(intendedUseCase, result.ChangedMaterialIds);
            Assert.True(result.Inspected >= 2);

            // Ровно одно событие на каждую изменённую марку, ничего лишнего.
            var logs = await s.Db.AuditLogs
                .Where(a => a.EntityType == "GsmMaterial" && a.Action == AuditAction)
                .Where(a => a.EntityId == ndCase.ToString() || a.EntityId == intendedUseCase.ToString())
                .ToListAsync();
            Assert.Equal(2, logs.Count);
            Assert.All(logs, a => Assert.Equal(1, logs.Count(x => x.EntityId == a.EntityId)));
            Assert.All(logs, a => Assert.Equal(editorId, a.UserId));

            // Событие описывает, что именно изменено, и не содержит ничего постороннего.
            var ndLog = logs.Single(a => a.EntityId == ndCase.ToString());
            Assert.Contains("Nd", ndLog.Details);
            Assert.Contains("Устаревшее НД", ndLog.Details);
            Assert.Equal(nameBefore, ndLog.EntityDisplayName);

            var useLog = logs.Single(a => a.EntityId == intendedUseCase.ToString());
            Assert.Contains("IntendedUse", useLog.Details);
            Assert.Contains("Устаревшее назначение", useLog.Details);
        }

        // Данные приведены к прежним полям, прежние поля не тронуты.
        await using var s2 = _fixture.CreateScope();
        var fixedMaterial = await s2.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == ndCase);
        Assert.Equal(gostBefore, fixedMaterial.Nd);
        Assert.Equal(nameBefore, fixedMaterial.Name);
        Assert.Equal(typeBefore, fixedMaterial.Type);
        Assert.Equal(gostBefore, fixedMaterial.Gost);
        Assert.Equal(descriptionBefore, fixedMaterial.Description);

        var fixedIntendedUse = await s2.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == intendedUseCase);
        Assert.Equal(fixedIntendedUse.Description, fixedIntendedUse.IntendedUse);

        SetRefEditor(s2);
        Assert.Empty(await s2.GsmMaterials.GetTransitionDivergencesAsync());
    }

    // ── 4. Повтор без расхождений не пишет журнал ──────────────────────────

    [Fact]
    public async Task Reconciliation_WithoutDivergences_ChangesNothingAndWritesNoJournal()
    {
        // Снимаем возможный остаток от других тестов, чтобы проверка не зависела
        // от порядка выполнения.
        await ClearDivergencesAsync();

        long auditsBefore;
        await using (var s = _fixture.CreateScope())
        {
            auditsBefore = await s.Db.AuditLogs
                .CountAsync(a => a.EntityType == "GsmMaterial" && a.Action == AuditAction);
        }

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var result = await s.GsmMaterials.ReconcileTransitionFieldsAsync(
                GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);

            Assert.Empty(result.ChangedMaterialIds);
            Assert.Equal(0, result.NdFixed);
            Assert.Equal(0, result.IntendedUseFixed);
            Assert.Equal(0, result.TotalFixed);

            var auditsAfter = await s.Db.AuditLogs
                .CountAsync(a => a.EntityType == "GsmMaterial" && a.Action == AuditAction);
            Assert.Equal(auditsBefore, auditsAfter);
        }
    }

    // ── 5. Права: отчёт доступен, сверка — нет, журнала успеха тоже ────────

    [Fact]
    public async Task ReferenceView_AllowsReport_ButNotReconciliation_AndNoSuccessJournal()
    {
        var id = await CreateMaterialAsync();
        await SetNdAsync(id, "Устаревшее НД");

        await using var s = _fixture.CreateScope();
        var viewerId = await CreateViewerUserAsync(s);
        s.User.CurrentUserId = Guid.Parse(viewerId);

        // Отчёт доступен: он read-only.
        Assert.Contains(await s.GsmMaterials.GetTransitionDivergencesAsync(), d => d.Id == id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth));

        // Отказ не создаёт записи об успешной сверке.
        await using var s2 = _fixture.CreateScope();
        Assert.False(await HasReconcileAuditAsync(id));

        await ClearDivergencesAsync();
    }

    // ── 6. Триггер: следует за прежними полями, уважает явные новые ─────────

    [Fact]
    public async Task Trigger_FollowsLegacyEdits_AndRespectsExplicitNewWrites()
    {
        // a. Меняется только прежнее поле — новое следует за ним.
        var a = await CreateMaterialAsync();
        await SetGostAsync(a, "ГОСТ из UI");
        Assert.Equal("ГОСТ из UI", await ReadAsync(a, m => m.Nd));

        // b. Меняется только новое поле — оно не перетирается.
        var b = await CreateMaterialAsync();
        await SetNdAsync(b, "НД вручную");
        Assert.Equal("НД вручную", await ReadAsync(b, m => m.Nd));

        // c. Меняются прежнее и новое в одном запросе — оба сохраняются как заданы.
        var c = await CreateMaterialAsync();
        await SetGostAndNdAsync(c, "ГОСТ явный", "НД явное");
        Assert.Equal("ГОСТ явный", await ReadAsync(c, m => m.Gost));
        Assert.Equal("НД явное", await ReadAsync(c, m => m.Nd));

        // d. Меняется только прежнее описание — назначение следует за ним.
        var d = await CreateMaterialAsync();
        await SetDescriptionAsync(d, "Назначение новое");
        Assert.Equal("Назначение новое", await ReadAsync(d, m => m.IntendedUse));

        // e. Меняется только новое назначение — оно не перетирается.
        var e = await CreateMaterialAsync();
        await SetIntendedUseAsync(e, "Назначение явное");
        Assert.Equal("Назначение явное", await ReadAsync(e, m => m.IntendedUse));

        // Намеренно созданные расхождения (случаи b, c, e) снимаются, чтобы общая
        // БД оставалась чистой для проверок «отчёт пуст».
        await ClearDivergencesAsync();
    }

    // ── 7. Счётчик просмотра берётся до commit, а не после ───────────────

    [Fact]
    public async Task Reconciliation_WhenCounterQueryFails_RollsBack_InsteadOfFailingAfterCommit()
    {
        await ClearDivergencesAsync();
        var id = await CreateMaterialAsync();
        await SetNdAsync(id, "Устаревшее НД");

        // Раньше счётчик просмотра выполнялся ПОСЛЕ commit: падение запроса
        // возвращало бы вызывающему ошибку при уже записанных изменениях.
        // Теперь запрос внутри транзакции, поэтому падение откатывает всё.
        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);

            FailingCommandInterceptor.ArmAt("SELECT count(*)", occurrence: 1);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    s.GsmMaterials.ReconcileTransitionFieldsAsync(
                        GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth));

                Assert.True(FailingCommandInterceptor.Fired, "Сбой запроса счётчика не сработал");
            }
            finally
            {
                FailingCommandInterceptor.Disarm();
            }
        }

        // Откат: данные и журнала нет. На прежнем коде здесь были бы записанные
        // значения, и проверка падала бы.
        await using var s2 = _fixture.CreateScope();
        var material = await s2.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
        Assert.Equal("Устаревшее НД", material.Nd);
        Assert.False(await HasReconcileAuditAsync(id));

        await ClearDivergencesAsync();
    }

    [Fact]
    public async Task Reconciliation_ReportsInspectedCount_OnSuccess()
    {
        await ClearDivergencesAsync();
        var id = await CreateMaterialAsync();
        await SetNdAsync(id, "Устаревшее НД");

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var result = await s.GsmMaterials.ReconcileTransitionFieldsAsync(
                GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);

            // Счётчик просмотра отражает реальное число марок, включая soft-deleted,
            // и не зависит от того, что он посчитан до или после commit.
            Assert.True(result.Inspected >= 1, "Счётчик просмотра не заполнен");
            Assert.Equal(1, result.NdFixed);
        }

        await using var s2 = _fixture.CreateScope();
        var fixedMaterial = await s2.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == id);
        Assert.Equal(fixedMaterial.Gost, fixedMaterial.Nd);
    }

    // ── Фикстуры данных ────────────────────────────────────────────────────

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

    // Каждый помощник меняет ровно одно поле: никаких неявных обнулений.
    // SQL собирается в каждом методе отдельно: ExecuteSqlInterpolated превращает
    // в параметр КАЖДУЮ дырку, поэтому динамическую часть SET сюда подставлять нельзя.

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

    private async Task<bool> HasReconcileAuditAsync(Guid id)
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "GsmMaterial"
            && a.Action == AuditAction
            && a.EntityId == id.ToString());
    }

    private async Task ClearDivergencesAsync()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        await s.GsmMaterials.ReconcileTransitionFieldsAsync(GsmLegacySourceOfTruth.LegacyGostIsSourceOfTruth);
    }

    /// <summary>Собственный пользователь только для чтения: чужие тесты выдают
    /// фикстурному гостю персональные override и не отзывают их.</summary>
    private async Task<string> CreateViewerUserAsync(TestScope s)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "gsm_atomic_viewer_" + Suffix(),
            FullName = "Проверка прав " + Suffix(),
            BranchId = _fixture.BranchA,
            IsActive = true,
        };
        Assert.True((await s.Users.CreateAsync(user)).Succeeded);
        Assert.True((await s.Users.AddToRoleAsync(user, nameof(UserRole.Guest))).Succeeded);

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
}
