using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data.Common;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// PR-2 (expand): схема инвентаризации ГСМ — новые поля GsmMaterials, таблицы
/// классификаций и связей, DB-защита инвариантов A/B и правила Foreign, перевод
/// FK строки ХК на RESTRICT, сервисные guard-и против опасного soft-delete.
///
/// Проверки идут по фактическому состоянию БД тестовой фикстуры: значения
/// сравниваются в пределах одной БД, «бумажные» числа окружения не используются.
/// </summary>
[Collection("Database")]
public class GsmInventorySchemaIntegrationTests
{
    // SQLSTATE, которые PostgreSQL отдаёт для наших ограничений.
    private const string RestrictViolation = "23001";   // ON DELETE RESTRICT
    private const string UniqueViolation = "23505";     // уникальный индекс
    private const string CheckViolation = "23514";      // CHECK / RAISE ... ERRCODE check_violation

    private readonly TestDatabaseFixture _fixture;

    public GsmInventorySchemaIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SetRefEditor(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── Схема: GsmMaterials ────────────────────────────────────────────────

    [Fact]
    public async Task GsmMaterials_HasNewColumns_AndKeepsLegacyColumns()
    {
        await using var s = _fixture.CreateScope();
        var columns = await GetColumnNamesAsync(s, "GsmMaterials");

        // Расширение схемы.
        Assert.Contains("Nd", columns);
        Assert.Contains("InGostNomenclature", columns);
        Assert.Contains("IntendedUse", columns);
        Assert.Contains("SuitabilityGround", columns);
        Assert.Contains("SuitabilityAir", columns);
        Assert.Contains("SuitabilitySea", columns);
        Assert.Contains("NatoIndex", columns);
        Assert.Contains("Note", columns);

        // Переходные поля на месте — старый код продолжает их читать и писать.
        Assert.Contains("Type", columns);
        Assert.Contains("Gost", columns);
        Assert.Contains("Description", columns);
        Assert.Contains("IsDraft", columns);
        Assert.Contains("IsDeleted", columns);
        Assert.Contains("DeletedAt", columns);
    }

    [Fact]
    public async Task GsmMaterials_NewColumns_AreNullableTextOrBooleanWithDefault()
    {
        await using var s = _fixture.CreateScope();

        // Nd/IntendedUse/Note — text (несколько НД и произвольное примечание).
        Assert.Equal("text", await ColumnTypeAsync(s, "GsmMaterials", "Nd"));
        Assert.Equal("text", await ColumnTypeAsync(s, "GsmMaterials", "IntendedUse"));
        Assert.Equal("text", await ColumnTypeAsync(s, "GsmMaterials", "Note"));
        Assert.Equal("character varying(50)", await ColumnTypeAsync(s, "GsmMaterials", "NatoIndex"));

        // Флаги NOT NULL DEFAULT false — существующая строка не может остаться «пустой».
        foreach (var flag in new[] { "InGostNomenclature", "SuitabilityGround", "SuitabilityAir", "SuitabilitySea" })
        {
            Assert.Equal("NO", await ColumnNullableAsync(s, "GsmMaterials", flag));
            Assert.Equal("false", await ColumnDefaultAsync(s, "GsmMaterials", flag));
        }
    }

    [Fact]
    public async Task GsmMaterialClassifications_HasExpectedSchema()
    {
        await using var s = _fixture.CreateScope();
        var columns = await GetColumnNamesAsync(s, "GsmMaterialClassifications");

        Assert.Contains("Id", columns);
        Assert.Contains("GsmMaterialId", columns);
        Assert.Contains("GroupName", columns);
        Assert.Contains("SubgroupName", columns);

        Assert.Equal("character varying(200)", await ColumnTypeAsync(s, "GsmMaterialClassifications", "GroupName"));
        Assert.Equal("character varying(200)", await ColumnTypeAsync(s, "GsmMaterialClassifications", "SubgroupName"));
        Assert.Equal("NO", await ColumnNullableAsync(s, "GsmMaterialClassifications", "GroupName"));
        Assert.Equal("NO", await ColumnNullableAsync(s, "GsmMaterialClassifications", "SubgroupName"));

        Assert.True(await CheckConstraintExistsAsync(s, "GsmMaterialClassifications", "CK_GsmMaterialClassifications_GroupName"));
        Assert.True(await CheckConstraintExistsAsync(s, "GsmMaterialClassifications", "CK_GsmMaterialClassifications_SubgroupName"));
        Assert.Equal("r", await ForeignKeyDeleteRuleAsync(s, "FK_GsmMaterialClassifications_GsmMaterials_GsmMaterialId"));
    }

    [Fact]
    public async Task GsmMaterialRelations_HasExpectedSchema()
    {
        await using var s = _fixture.CreateScope();
        var columns = await GetColumnNamesAsync(s, "GsmMaterialRelations");

        Assert.Contains("Id", columns);
        Assert.Contains("PrimaryGsmMaterialId", columns);
        Assert.Contains("RelatedGsmMaterialId", columns);
        Assert.Contains("RelationType", columns);
        Assert.Contains("Note", columns);
        Assert.Contains("IsDeleted", columns);

        Assert.True(await CheckConstraintExistsAsync(s, "GsmMaterialRelations", "CK_GsmMaterialRelations_NoSelfReference"));
        Assert.True(await CheckConstraintExistsAsync(s, "GsmMaterialRelations", "CK_GsmMaterialRelations_RelationType"));
        Assert.Equal("r", await ForeignKeyDeleteRuleAsync(s, "FK_GsmMaterialRelations_GsmMaterials_PrimaryGsmMaterialId"));
        Assert.Equal("r", await ForeignKeyDeleteRuleAsync(s, "FK_GsmMaterialRelations_GsmMaterials_RelatedGsmMaterialId"));
        Assert.True(await IndexExistsAsync(s, "GsmMaterialRelations", "IX_GsmMaterialRelations_PrimaryGsmMaterialId"));
        Assert.True(await IndexExistsAsync(s, "GsmMaterialRelations", "IX_GsmMaterialRelations_RelatedGsmMaterialId"));
    }

    [Fact]
    public async Task GsmInventory_CustomIndexes_Exist()
    {
        await using var s = _fixture.CreateScope();

        // Инвариант A: уникальность нормализованной тройки.
        Assert.True(await IndexExistsAsync(s, "GsmMaterialClassifications", "UX_GsmMaterialClassifications_GroupSubgroup"));
        // Уникальность активной направленной пары (частичный индекс).
        Assert.True(await IndexExistsAsync(s, "GsmMaterialRelations", "UX_GsmMaterialRelations_ActivePair"));
    }

    [Fact]
    public async Task GsmInventory_DbGuards_Exist()
    {
        await using var s = _fixture.CreateScope();

        Assert.True(await TriggerExistsAsync(s, "GsmMaterialClassifications", "TRG_GsmMaterialClassifications_SingleGroup"));
        Assert.True(await TriggerExistsAsync(s, "GsmMaterialRelations", "TRG_GsmMaterialRelations_ForeignCheck"));
        Assert.True(await TriggerExistsAsync(s, "GsmMaterials", "TRG_GsmMaterials_ForeignFlagGuard"));
        Assert.True(await FunctionExistsAsync(s, "fn_gsm_classification_single_group"));
        Assert.True(await FunctionExistsAsync(s, "fn_gsm_relation_foreign_check"));
        Assert.True(await FunctionExistsAsync(s, "fn_gsm_material_foreign_flag_guard"));
    }

    [Fact]
    public async Task RelationType_IsStoredAsString_AndDomainIsClosed()
    {
        var (primary, related) = await CreateMaterialPairAsync();
        var relationId = await InsertRelationAsync(primary, related, GsmRelationType.DuplicateAndReserve);

        await using var s = _fixture.CreateScope();
        Assert.Equal("character varying(32)", await ColumnTypeAsync(s, "GsmMaterialRelations", "RelationType"));

        // В БД значение лежит строкой, а не числом.
        var stored = await ScalarStringAsync(s,
            @"SELECT ""RelationType"" FROM ""GsmMaterialRelations"" WHERE ""Id"" = @id", ("@id", relationId));
        Assert.Equal("DuplicateAndReserve", stored);

        // Числовой код в этот столбец не принимается — набор значений закрыт.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => s.Db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""GsmMaterialRelations"" (""Id"", ""PrimaryGsmMaterialId"", ""RelatedGsmMaterialId"", ""RelationType"")
            VALUES (gen_random_uuid(), {primary}, {related}, '1')"));
        Assert.Equal(CheckViolation, ex.SqlState);
    }

    // ── FK строки ХК: CASCADE больше недопустим ────────────────────────────

    [Fact]
    public async Task HkCardItemMaterialForeignKey_IsRestrict_NotCascade()
    {
        await using var s = _fixture.CreateScope();

        // 'r' = RESTRICT, 'a' = NO ACTION, 'c' = CASCADE.
        var rule = await ForeignKeyDeleteRuleAsync(s, "FK_HKCardItemMaterials_GsmMaterials_GsmMaterialId");
        Assert.True(rule is "r" or "a", "Ожидался RESTRICT/NO ACTION, получено: " + rule);
    }

    [Fact]
    public async Task PhysicalDelete_OfMaterialUsedInHkCard_IsRejected_AndRowSurvives()
    {
        var materialId = await CreateMaterialAsync();
        var rows = await CreateHkItemsWithMaterialAsync(materialId, HKCardStatus.Approved);
        var rowId = rows.Single();

        // Физическое удаление марки со ссылкой из ХК отвергается БД.
        await using (var s = _fixture.CreateScope())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                s.Db.Database.ExecuteSqlInterpolatedAsync(
                    $@"DELETE FROM ""GsmMaterials"" WHERE ""Id"" = {materialId}"));
            Assert.Equal(RestrictViolation, ex.SqlState);
        }

        // Строка ХК осталась на месте, значения не изменились.
        await using var s2 = _fixture.CreateScope();
        var row = await s2.Db.HKCardItemMaterials.AsNoTracking().FirstAsync(m => m.Id == rowId);
        Assert.Equal(materialId, row.GsmMaterialId);
        Assert.Equal(1, await s2.Db.HKCardItemMaterials.CountAsync(m => m.GsmMaterialId == materialId));
    }

    [Fact]
    public async Task PhysicalDelete_OfUnusedMaterial_IsAllowed()
    {
        var materialId = await CreateMaterialAsync();

        await using var s = _fixture.CreateScope();
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""GsmMaterials"" WHERE ""Id"" = {materialId}");
        Assert.False(await s.Db.GsmMaterials.IgnoreQueryFilters().AnyAsync(m => m.Id == materialId));
    }

    // ── Backfill ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillSql_CopiesGostToNd_AndDescriptionToIntendedUse_WithoutTouchingLegacy()
    {
        var gostWithValue = "ГОСТ 8581-78 " + Suffix();
        var blankGost = "   ";
        var manualName = "Марка вручную " + Suffix();
        var manualDescription = "Назначение " + Suffix();

        // 1) обычная legacy-марка, 2) марка с пустым Gost, 3) марка, где новые
        // поля уже заполнены вручную (backfill не должен их перетирать).
        var fromGost = await InsertLegacyMaterialAsync(gostWithValue, "Назначение " + Suffix());
        var withBlankGost = await InsertLegacyMaterialAsync(blankGost, "Без НД " + Suffix());
        var alreadyFilled = await InsertLegacyMaterialAsync("ГОСТ 23652-79", manualDescription,
            nd: "НД вручную", intendedUse: "Не перетирать", name: manualName);

        // Ровно те два оператора, что выполняет миграция; прогоняем повторно
        // (проверка идемпотентности).
        await RunBackfillSqlAsync();
        await RunBackfillSqlAsync();

        await using var s = _fixture.CreateScope();
        var rows = await s.Db.GsmMaterials.AsNoTracking()
            .Where(m => m.Id == fromGost || m.Id == withBlankGost || m.Id == alreadyFilled)
            .ToListAsync();
        Assert.Equal(3, rows.Count);

        var copied = rows.Single(m => m.Id == fromGost);
        Assert.Equal(gostWithValue, copied.Nd);
        Assert.Equal(copied.Description, copied.IntendedUse);

        // Пустой Gost не превращается в пустую строку Nd.
        Assert.Null(rows.Single(m => m.Id == withBlankGost).Nd);

        // Уже заполненные новые значения не перетираются.
        var manual = rows.Single(m => m.Id == alreadyFilled);
        Assert.Equal("НД вручную", manual.Nd);
        Assert.Equal("Не перетирать", manual.IntendedUse);

        // Прежние поля не изменились.
        Assert.Equal("ГОСТ 23652-79", manual.Gost);
        Assert.Equal(manualDescription, manual.Description);
        Assert.False(string.IsNullOrWhiteSpace(manual.Type));

        // Расхождение Nd↔Gost, созданное вручную, убирается напрямую, чтобы
        // общая БД оставалась чистой для остальных тестов. Проверять его отчётом
        // о переносе больше нельзя: отчёт удалён в фазе A PR-6 как неверный
        // критерий — он измерял данные, а спрашивал про код. Проверка готовности
        // к удалению колонок — карта обращений и тесты (см.
        // GsmActiveConsumerSwitchIntegrationTests).
        await using var s3 = _fixture.CreateScope();
        SetRefEditor(s3);
        await s3.Db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""GsmMaterials""
                SET ""Nd"" = NULLIF(btrim(""Gost""), ''),
                    ""IntendedUse"" = ""Description""
                WHERE ""Id"" = {alreadyFilled}");

        var restored = await s3.Db.GsmMaterials.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.Id == alreadyFilled);
        // После выравнивания Nd повторяет Gost, а IntendedUse — Description.
        Assert.Equal(restored.Gost, restored.Nd);
        Assert.Equal(restored.Description, restored.IntendedUse);
    }

    [Fact]
    public async Task LegacyMaterial_WithoutGroup_GetsNoFabricatedClassification()
    {
        var materialId = await CreateMaterialAsync();

        await using var s = _fixture.CreateScope();
        // Ни одна классификация не создаётся «из одного лишь Type».
        Assert.False(await s.Db.GsmMaterialClassifications.AnyAsync(c => c.GsmMaterialId == materialId));

        // Старые читатели видят марку как прежде.
        var material = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == materialId);
        Assert.False(string.IsNullOrWhiteSpace(material.Type));

        // Новые поля не содержат выдуманных значений: это копия прежних
        // (см. TRG_GsmMaterials_LegacyFieldSync), а не новый источник истины.
        Assert.Equal(material.Gost, material.Nd);
        Assert.Equal(material.Description, material.IntendedUse);
        Assert.False(material.InGostNomenclature);
    }

    [Fact]
    public async Task LegacyReadPath_ThroughHkCardMaterialRow_StillResolvesMaterial()
    {
        var materialId = await CreateMaterialAsync();
        await CreateHkItemsWithMaterialAsync(materialId, HKCardStatus.Approved, HKCardStatus.Deleted);
        var itemId = await GetItemIdAsync(materialId);

        await using var s = _fixture.CreateScope();
        var loaded = await s.Db.HKCardItems
            .AsNoTracking()
            .Include(i => i.Materials).ThenInclude(m => m.GsmMaterial)
            .FirstAsync(i => i.Id == itemId);

        Assert.Single(loaded.Materials);
        var materialRow = loaded.Materials.Single();
        Assert.Equal(materialId, materialRow.GsmMaterialId);
        Assert.NotNull(materialRow.GsmMaterial);
        Assert.False(string.IsNullOrWhiteSpace(materialRow.GsmMaterial!.Type));
    }

    // ── Инвариант A: уникальность нормализованной пары ─────────────────────

    [Fact]
    public async Task TwoSubgroupsOfSameGroup_AreAllowed()
    {
        var materialId = await CreateMaterialAsync();
        var group = "Группа " + Suffix();

        await using (var s = _fixture.CreateScope())
        {
            s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
            { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = group, SubgroupName = "Подгруппа 1" });
            s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
            { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = group, SubgroupName = "Подгруппа 2" });
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        Assert.Equal(2, await s2.Db.GsmMaterialClassifications.CountAsync(c => c.GsmMaterialId == materialId));
    }

    [Fact]
    public async Task DuplicateGroupSubgroupPair_IsRejected_IgnoringCaseAndSpaces()
    {
        var materialId = await CreateMaterialAsync();
        var group = "Группа " + Suffix();

        await using (var s = _fixture.CreateScope())
        {
            s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
            { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = group, SubgroupName = "Для смазки" });
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        s2.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        {
            Id = Guid.NewGuid(),
            GsmMaterialId = materialId,
            GroupName = "  " + group.ToUpperInvariant() + " ",
            SubgroupName = "для СМАЗКИ",
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s2.Db.SaveChangesAsync());
        Assert.Equal(UniqueViolation, PostgresSqlState(ex));
    }

    [Fact]
    public async Task BlankGroupOrSubgroup_IsRejected()
    {
        var materialId = await CreateMaterialAsync();

        await using (var s = _fixture.CreateScope())
        {
            s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
            { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = "   ", SubgroupName = "Подгруппа" });
            await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        }

        await using var s2 = _fixture.CreateScope();
        s2.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = "Группа " + Suffix(), SubgroupName = "  " });
        await Assert.ThrowsAsync<DbUpdateException>(() => s2.Db.SaveChangesAsync());
    }

    // ── Инвариант B: у марки одна группа ───────────────────────────────────

    [Fact]
    public async Task SecondGroupForSameMaterial_IsRejected()
    {
        var materialId = await CreateMaterialAsync();

        await using (var s = _fixture.CreateScope())
        {
            s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
            { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = "Смазочные " + Suffix(), SubgroupName = "Подгруппа 1" });
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        s2.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = "Топливные " + Suffix(), SubgroupName = "Подгруппа 1" });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s2.Db.SaveChangesAsync());
        Assert.Equal(CheckViolation, PostgresSqlState(ex));
    }

    [Fact]
    public async Task SameClassification_ForDifferentMaterials_IsAllowed()
    {
        var (m1, m2) = await CreateMaterialPairAsync();
        var group = "Общая " + Suffix();
        var sub = "Подгруппа " + Suffix();

        await using var s = _fixture.CreateScope();
        s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        { Id = Guid.NewGuid(), GsmMaterialId = m1, GroupName = group, SubgroupName = sub });
        s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        { Id = Guid.NewGuid(), GsmMaterialId = m2, GroupName = group, SubgroupName = sub });
        await s.Db.SaveChangesAsync();

        Assert.Equal(2, await s.Db.GsmMaterialClassifications.CountAsync(c => c.GsmMaterialId == m1 || c.GsmMaterialId == m2));
    }

    [Fact]
    public async Task ConcurrentDifferentGroups_DoNotLeaveTwoGroups()
    {
        var materialId = await CreateMaterialAsync();

        await using var ctx1 = NewContext();
        await using var ctx2 = NewContext();
        await using var tx1 = await ctx1.Database.BeginTransactionAsync();
        await using var tx2 = await ctx2.Database.BeginTransactionAsync();

        // Обе транзакции уже открыты; вставки стартуют по общему сигналу.
        var gate = new TaskCompletionSource();

        async Task<bool> TryInsertAsync(AppDbContext ctx, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, string group)
        {
            ctx.GsmMaterialClassifications.Add(new GsmMaterialClassification
            { Id = Guid.NewGuid(), GsmMaterialId = materialId, GroupName = group, SubgroupName = "Подгруппа" });
            await gate.Task;
            try
            {
                await ctx.SaveChangesAsync();
                await tx.CommitAsync();
                return true;
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync();
                return false;
            }
        }

        var t1 = Task.Run(() => TryInsertAsync(ctx1, tx1, "Группа A " + Suffix()));
        var t2 = Task.Run(() => TryInsertAsync(ctx2, tx2, "Группа B " + Suffix()));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);

        // Сериализация по родительской марке: успешной может быть только одна вставка.
        Assert.Equal(1, results.Count(r => r));

        await using var s = _fixture.CreateScope();
        var rows = await s.Db.GsmMaterialClassifications.AsNoTracking()
            .Where(c => c.GsmMaterialId == materialId).ToListAsync();
        Assert.Single(rows);
    }

    // ── Связи: направленность, уникальность, Foreign ──────────────────────

    [Fact]
    public async Task SelfRelation_IsRejected()
    {
        var materialId = await CreateMaterialAsync();
        await using var s = _fixture.CreateScope();

        s.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
        {
            Id = Guid.NewGuid(),
            PrimaryGsmMaterialId = materialId,
            RelatedGsmMaterialId = materialId,
            RelationType = GsmRelationType.Duplicate,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Equal(CheckViolation, PostgresSqlState(ex));
    }

    [Fact]
    public async Task DuplicateActiveDirectedPair_IsRejected_AndAllowedAgainAfterSoftDelete()
    {
        var (primary, related) = await CreateMaterialPairAsync();
        var relationId = await InsertRelationAsync(primary, related, GsmRelationType.Duplicate);

        // Повтор активной пары запрещён независимо от типа связи.
        await using (var s = _fixture.CreateScope())
        {
            s.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
            {
                Id = Guid.NewGuid(),
                PrimaryGsmMaterialId = primary,
                RelatedGsmMaterialId = related,
                RelationType = GsmRelationType.Reserve,
            });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
            Assert.Equal(UniqueViolation, PostgresSqlState(ex));
        }

        // Обратное направление — другая пара, допустимо.
        await using (var s = _fixture.CreateScope())
        {
            s.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
            {
                Id = Guid.NewGuid(),
                PrimaryGsmMaterialId = related,
                RelatedGsmMaterialId = primary,
                RelationType = GsmRelationType.Reserve,
            });
            await s.Db.SaveChangesAsync();
        }

        // После мягкого удаления пара освобождается.
        await using (var s = _fixture.CreateScope())
        {
            var row = await s.Db.GsmMaterialRelations.FirstAsync(r => r.Id == relationId);
            row.IsDeleted = true;
            await s.Db.SaveChangesAsync();
        }

        await using (var s = _fixture.CreateScope())
        {
            s.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
            {
                Id = Guid.NewGuid(),
                PrimaryGsmMaterialId = primary,
                RelatedGsmMaterialId = related,
                RelationType = GsmRelationType.Duplicate,
            });
            await s.Db.SaveChangesAsync();

            // Активны: исходная пара (возвращённая после soft-delete) и обратное направление.
            Assert.Equal(2, await s.Db.GsmMaterialRelations.CountAsync(r => !r.IsDeleted
                && ((r.PrimaryGsmMaterialId == primary && r.RelatedGsmMaterialId == related)
                    || (r.PrimaryGsmMaterialId == related && r.RelatedGsmMaterialId == primary))));

            // Второй дубль той же активной пары по-прежнему запрещён.
            s.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
            {
                Id = Guid.NewGuid(),
                PrimaryGsmMaterialId = primary,
                RelatedGsmMaterialId = related,
                RelationType = GsmRelationType.Reserve,
            });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
            Assert.Equal(UniqueViolation, PostgresSqlState(ex));
        }
    }

    [Fact]
    public async Task ForeignRelation_ToMaterialInGostNomenclature_IsRejected()
    {
        var (primary, related) = await CreateMaterialPairAsync();
        await using (var s = _fixture.CreateScope())
        {
            var m = await s.Db.GsmMaterials.FirstAsync(x => x.Id == related);
            m.InGostNomenclature = true;
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        s2.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
        {
            Id = Guid.NewGuid(),
            PrimaryGsmMaterialId = primary,
            RelatedGsmMaterialId = related,
            RelationType = GsmRelationType.Foreign,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s2.Db.SaveChangesAsync());
        Assert.Equal(CheckViolation, PostgresSqlState(ex));
    }

    [Fact]
    public async Task SettingNomenclatureFlag_WithActiveForeignRelation_IsRejected()
    {
        var (primary, related) = await CreateMaterialPairAsync();
        await InsertRelationAsync(primary, related, GsmRelationType.Foreign);

        await using var s = _fixture.CreateScope();
        var m = await s.Db.GsmMaterials.FirstAsync(x => x.Id == related);
        m.InGostNomenclature = true;
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Equal(CheckViolation, PostgresSqlState(ex));

        // Флаг не изменился.
        await using var s2 = _fixture.CreateScope();
        Assert.False(await s2.Db.GsmMaterials.AsNoTracking().AnyAsync(x => x.Id == related && x.InGostNomenclature));
    }

    [Fact]
    public async Task SoftDeletedForeignRelation_DoesNotBlockNomenclatureFlag()
    {
        var (primary, related) = await CreateMaterialPairAsync();
        var relationId = await InsertRelationAsync(primary, related, GsmRelationType.Foreign);

        await using (var s = _fixture.CreateScope())
        {
            var row = await s.Db.GsmMaterialRelations.FirstAsync(r => r.Id == relationId);
            row.IsDeleted = true;
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        var m = await s2.Db.GsmMaterials.FirstAsync(x => x.Id == related);
        m.InGostNomenclature = true;
        await s2.Db.SaveChangesAsync();

        Assert.True(await s2.Db.GsmMaterials.AsNoTracking().AnyAsync(x => x.Id == related && x.InGostNomenclature));
    }

    // ── Сервисные guard-и ──────────────────────────────────────────────────

    [Theory]
    [InlineData(HKCardStatus.Draft)]
    [InlineData(HKCardStatus.Approved)]
    [InlineData(HKCardStatus.Archived)]
    [InlineData(HKCardStatus.Deleted)]
    public async Task DeleteGsmMaterial_IsBlocked_WhenUsedInAnyCardStatus(HKCardStatus status)
    {
        var materialId = await CreateMaterialAsync();
        await CreateHkItemsWithMaterialAsync(materialId, status);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.GsmMaterials.DeleteAsync(materialId));
        Assert.Contains("ХК", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Марка осталась живой, строка ХК — на месте.
        Assert.True(await s.Db.GsmMaterials.IgnoreQueryFilters().AnyAsync(m => m.Id == materialId && !m.IsDeleted));
        Assert.Equal(1, await s.Db.HKCardItemMaterials.CountAsync(m => m.GsmMaterialId == materialId));
    }

    [Fact]
    public async Task DeleteGsmMaterial_AndRestore_Work_WhenUnreferenced()
    {
        var materialId = await CreateMaterialAsync();

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            Assert.True(await s.GsmMaterials.DeleteAsync(materialId));
        }

        await using (var s = _fixture.CreateScope())
        {
            // Soft-delete скрывает марку из обычных запросов.
            Assert.False(await s.Db.GsmMaterials.AnyAsync(m => m.Id == materialId));
            Assert.True(await s.Db.GsmMaterials.IgnoreQueryFilters().AnyAsync(m => m.Id == materialId && m.IsDeleted));
        }

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            Assert.True(await s.GsmMaterials.RestoreAsync(materialId));
            Assert.True(await s.Db.GsmMaterials.AnyAsync(m => m.Id == materialId && !m.IsDeleted));
        }
    }

    [Fact]
    public async Task RejectProposal_WithHkReference_Refuses_AndKeepsStubAndStatus()
    {
        var (proposalId, stubId) = await CreatePendingGsmProposalAsync();
        var rowId = (await CreateHkItemsWithMaterialAsync(stubId, HKCardStatus.Draft)).Single();

        await using var s = _fixture.CreateScope();
        var (success, error) = await s.HK.RejectProposalAsync(proposalId);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("черновик", error!, StringComparison.OrdinalIgnoreCase);

        // Ничего не изменилось: предложение «Ожидает», stub и строка ХК на месте.
        await using var s2 = _fixture.CreateScope();
        Assert.Equal(ProposalStatus.Pending, (await s2.Db.ReferenceProposals.FindAsync(proposalId))!.Status);
        Assert.True(await s2.Db.GsmMaterials.IgnoreQueryFilters().AnyAsync(m => m.Id == stubId));
        Assert.True(await s2.Db.HKCardItemMaterials.AnyAsync(m => m.Id == rowId));
        Assert.False(await s2.Db.AuditLogs.AnyAsync(a => a.EntityType == "ReferenceProposal"
            && a.EntityId == proposalId.ToString() && a.Action == "Rejected"));
    }

    [Fact]
    public async Task RejectProposal_WithoutHkReference_Rejects_AndDeletesStub()
    {
        var (proposalId, stubId) = await CreatePendingGsmProposalAsync();

        await using var s = _fixture.CreateScope();
        var (success, error) = await s.HK.RejectProposalAsync(proposalId);

        Assert.True(success);
        Assert.Null(error);

        await using var s2 = _fixture.CreateScope();
        Assert.Equal(ProposalStatus.Rejected, (await s2.Db.ReferenceProposals.FindAsync(proposalId))!.Status);
        Assert.False(await s2.Db.GsmMaterials.IgnoreQueryFilters().AnyAsync(m => m.Id == stubId));
        Assert.True(await s2.Db.AuditLogs.AnyAsync(a => a.EntityType == "ReferenceProposal"
            && a.EntityId == proposalId.ToString() && a.Action == "Rejected"));
    }

    [Fact]
    public async Task RejectProposal_AlreadyResolved_ReturnsRefusal()
    {
        var (proposalId, _) = await CreatePendingGsmProposalAsync();

        await using var s = _fixture.CreateScope();
        Assert.True((await s.HK.RejectProposalAsync(proposalId)).Success);

        await using var s2 = _fixture.CreateScope();
        var (success, error) = await s2.HK.RejectProposalAsync(proposalId);
        Assert.False(success);
        Assert.NotNull(error);
    }

    // ── Фикстуры данных ────────────────────────────────────────────────────

    private async Task<Guid> CreateMaterialAsync()
    {
        return await InsertLegacyMaterialAsync("ГОСТ 8581-78", "Масло " + Suffix());
    }

    /// <summary>Вставляет марку напрямую в БД, чтобы задать любые значения полей,
    /// включая переходные Nd/IntendedUse (сервис их пока не пишет).</summary>
    private async Task<Guid> InsertLegacyMaterialAsync(
        string gost, string description, string? nd = null, string? intendedUse = null, string? name = null)
    {
        await using var s = _fixture.CreateScope();
        var material = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = name ?? ("Марка " + Suffix()),
            Type = "Моторное масло " + Suffix(),
            Gost = gost,
            Description = description,
            Nd = nd,
            IntendedUse = intendedUse,
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.GsmMaterials.Add(material);
        await s.Db.SaveChangesAsync();
        return material.Id;
    }

    private async Task<(Guid First, Guid Second)> CreateMaterialPairAsync()
    {
        var first = await InsertLegacyMaterialAsync("ГОСТ 1", "Первая " + Suffix());
        var second = await InsertLegacyMaterialAsync("ГОСТ 2", "Вторая " + Suffix());
        return (first, second);
    }

    private async Task<Guid> InsertRelationAsync(Guid primary, Guid related, GsmRelationType type)
    {
        await using var s = _fixture.CreateScope();
        var relation = new GsmMaterialRelation
        {
            Id = Guid.NewGuid(),
            PrimaryGsmMaterialId = primary,
            RelatedGsmMaterialId = related,
            RelationType = type,
        };
        s.Db.GsmMaterialRelations.Add(relation);
        await s.Db.SaveChangesAsync();
        return relation.Id;
    }

    /// <summary>Создаёт отдельную ХК+строку+строку материала для каждого статуса.
    /// Возвращает идентификаторы созданных строк HKCardItemMaterials.</summary>
    private async Task<List<Guid>> CreateHkItemsWithMaterialAsync(Guid materialId, params HKCardStatus[] statuses)
    {
        await using var s = _fixture.CreateScope();

        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();

        var rowIds = new List<Guid>();
        foreach (var status in statuses)
        {
            var hk = new HKCard
            {
                Id = Guid.NewGuid(),
                Code = "HK-" + Suffix(),
                Version = "v1",
                Status = status,
                ObjectLevel = HKObjectLevel.Node,
                NodeId = node.Id,
                BranchId = _fixture.BranchA,
                CreatedAt = DateTime.UtcNow,
                ApprovedDate = status == HKCardStatus.Approved ? DateTime.UtcNow : null,
            };
            s.Db.HKCards.Add(hk);

            var item = new HKCardItem
            {
                Id = Guid.NewGuid(),
                HKCardId = hk.Id,
                AssemblyUnitId = unit.Id,
                Quantity = 1,
                Volume = 10,
                SortOrder = 1,
            };
            s.Db.HKCardItems.Add(item);

            var row = new HKCardItemMaterial
            {
                Id = Guid.NewGuid(),
                HKCardItemId = item.Id,
                GsmMaterialId = materialId,
                Category = GsmCategory.Primary,
            };
            s.Db.HKCardItemMaterials.Add(row);
            rowIds.Add(row.Id);
        }

        await s.Db.SaveChangesAsync();
        return rowIds;
    }

    private async Task<Guid> GetItemIdAsync(Guid materialId)
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.HKCardItemMaterials
            .Where(m => m.GsmMaterialId == materialId)
            .Select(m => m.HKCardItemId)
            .FirstAsync();
    }

    private async Task<(Guid ProposalId, Guid StubId)> CreatePendingGsmProposalAsync()
    {
        await using var s = _fixture.CreateScope();

        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);

        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v1",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
        };
        s.Db.HKCards.Add(hk);

        // Draft-stub марки, созданный предложением.
        var stub = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "Черновик " + Suffix(),
            Type = "Черновик " + Suffix(),
            IsDraft = true,
            IsDeleted = false,
        };
        s.Db.GsmMaterials.Add(stub);

        var proposal = new ReferenceProposal
        {
            Id = Guid.NewGuid(),
            HKCardId = hk.Id,
            TargetType = ProposalTargetType.GsmMaterial,
            Code = "П-" + Suffix(),
            Name = "Черновик " + Suffix(),
            Status = ProposalStatus.Pending,
            CreatedStubGsmMaterialId = stub.Id,
            CreatedByUserId = _fixture.NormAdminA.Id,
            CreatedAt = DateTime.UtcNow,
        };
        s.Db.ReferenceProposals.Add(proposal);
        await s.Db.SaveChangesAsync();

        return (proposal.Id, stub.Id);
    }

    private AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new AppDbContext(options);
    }

    private async Task RunBackfillSqlAsync()
    {
        await using var s = _fixture.CreateScope();
        // Ровно те два оператора, что выполняет миграция.
        await s.Db.Database.ExecuteSqlRawAsync(@"
            UPDATE ""GsmMaterials""
            SET ""Nd"" = NULLIF(btrim(""Gost""), '')
            WHERE ""Nd"" IS NULL AND ""Gost"" IS NOT NULL;

            UPDATE ""GsmMaterials""
            SET ""IntendedUse"" = ""Description""
            WHERE ""IntendedUse"" IS NULL AND ""Description"" IS NOT NULL;");
    }

    private static string PostgresSqlState(DbUpdateException ex)
    {
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        return pg.SqlState;
    }

    // ── Помощники по схеме ─────────────────────────────────────────────────

    private static async Task<List<string>> GetColumnNamesAsync(TestScope s, string table)
    {
        var names = new List<string>();
        await using var cmd = await CreateOpenCommandAsync(s,
            @"SELECT column_name FROM information_schema.columns
              WHERE table_schema = 'public' AND table_name = @table ORDER BY ordinal_position",
            ("@table", table));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<string?> ColumnTypeAsync(TestScope s, string table, string column) =>
        await ScalarStringAsync(s,
            @"SELECT data_type || CASE WHEN character_maximum_length IS NULL OR character_maximum_length = -1
                                       THEN '' ELSE '(' || character_maximum_length || ')' END
              FROM information_schema.columns
              WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            ("@table", table), ("@column", column));

    private static async Task<string?> ColumnNullableAsync(TestScope s, string table, string column) =>
        await ScalarStringAsync(s,
            @"SELECT is_nullable FROM information_schema.columns
              WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            ("@table", table), ("@column", column));

    private static async Task<string?> ColumnDefaultAsync(TestScope s, string table, string column) =>
        await ScalarStringAsync(s,
            @"SELECT column_default FROM information_schema.columns
              WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            ("@table", table), ("@column", column));

    private static async Task<bool> CheckConstraintExistsAsync(TestScope s, string table, string constraint) =>
        await ScalarLongAsync(s,
            @"SELECT count(*) FROM information_schema.table_constraints
              WHERE constraint_type = 'CHECK' AND table_schema = 'public'
                AND table_name = @table AND constraint_name = @constraint",
            ("@table", table), ("@constraint", constraint)) == 1;

    private static async Task<bool> IndexExistsAsync(TestScope s, string table, string index) =>
        await ScalarLongAsync(s,
            @"SELECT count(*) FROM pg_indexes
              WHERE schemaname = 'public' AND tablename = @table AND indexname = @index",
            ("@table", table), ("@index", index)) == 1;

    private static async Task<bool> TriggerExistsAsync(TestScope s, string table, string trigger) =>
        await ScalarLongAsync(s,
            @"SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
              WHERE NOT t.tgisinternal AND c.relname = @table AND t.tgname = @trigger",
            ("@table", table), ("@trigger", trigger)) == 1;

    private static async Task<bool> FunctionExistsAsync(TestScope s, string function) =>
        await ScalarLongAsync(s,
            @"SELECT count(*) FROM pg_proc WHERE proname = @function",
            ("@function", function)) == 1;

    private static async Task<string?> ForeignKeyDeleteRuleAsync(TestScope s, string constraint) =>
        await ScalarStringAsync(s,
            @"SELECT confdeltype::text FROM pg_constraint WHERE contype = 'f' AND conname = @c",
            ("@c", constraint));

    private static DbCommand CreateCommand(TestScope s, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task<string?> ScalarStringAsync(TestScope s, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = await CreateOpenCommandAsync(s, sql, parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task<long> ScalarLongAsync(TestScope s, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = await CreateOpenCommandAsync(s, sql, parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }

    /// <summary>Создаёт команду на открытом соединении: сырой DbCommand не открывает его сам.</summary>
    private static async Task<DbCommand> CreateOpenCommandAsync(
        TestScope s, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = CreateCommand(s, sql, parameters);
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
        return cmd;
    }
}
