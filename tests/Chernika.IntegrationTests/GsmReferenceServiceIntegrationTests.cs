using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// PR-3: сервис ГСМ и первый сводный справочник марок.
/// <para>
/// Проверяются: единая серверная защита публикации для всех путей записи,
/// атомарная запись марки вместе с классификацией, сводный список без дублей
/// и размножения строк, read-only списки связей, прямой FK-guard удаления и
/// раздельность прав View/Edit.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmReferenceServiceIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmReferenceServiceIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SetRefEditor(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── 2. Новая опубликованная марка: классификация обязательна ───────────

    [Fact]
    public async Task Create_WithoutGroup_IsRejected()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
            {
                Name = "Без группы " + Suffix(),
                Nd = "ГОСТ 1",
            }));

        Assert.Contains("группу", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await s.Db.GsmMaterials.AnyAsync(m => m.Name == "Без группы"));
    }

    [Fact]
    public async Task Create_WithGroupButNoSubgroup_IsRejected()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
            {
                Name = "Без подгруппы " + Suffix(),
                GroupName = "Моторные масла",
                SubgroupNames = new List<string>(),
            }));

        Assert.Contains("подгрупп", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WithBlankSubgroup_IsRejected()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
            {
                Name = "Пустая подгруппа " + Suffix(),
                GroupName = "Моторные масла",
                SubgroupNames = new List<string> { "   " },
            }));
    }

    [Fact]
    public async Task Create_StoresOneGroupAndManySubgroups_AndLegacyMirrors()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Много подгрупп " + Suffix(),
            Nd = "ГОСТ 21743-76",
            IntendedUse = "Для турбинных двигателей",
            SuitabilityAir = true,
            NatoIndex = "F-35",
            Note = "Примечание",
            GroupName = "  Моторные масла  ",
            SubgroupNames = new List<string> { "Для дизельных двигателей", "Для дизельных двигателей ", "Для газовых турбин" },
        });

        // Дубли схлопнуты, группа обрезана, подгруппы сохранены.
        Assert.Equal("Моторные масла", view.GroupName);
        Assert.Equal(2, view.SubgroupNames.Count);
        Assert.Contains("Для дизельных двигателей", view.SubgroupNames);
        Assert.Contains("Для газовых турбин", view.SubgroupNames);

        var stored = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == view.Id);
        Assert.Equal("ГОСТ 21743-76", stored.Nd);
        Assert.Equal("ГОСТ 21743-76", stored.Gost);
        Assert.Equal("Для турбинных двигателей", stored.IntendedUse);
        Assert.Equal("Для турбинных двигателей", stored.Description);
        Assert.Equal("F-35", stored.NatoIndex);
        Assert.True(stored.SuitabilityAir);
        Assert.Equal("Примечание", stored.Note);

        // Переходное правило: legacy Type = подгруппа, первая по алфавиту.
        // Группа в Type не пишется — это разные уровни модели.
        Assert.Equal("Для газовых турбин", stored.Type);
        Assert.NotEqual("Моторные масла", stored.Type);
    }

    // ── 3. Классификация: замена набора и запреты ──────────────────────────

    [Fact]
    public async Task Update_ReplacesSubgroups_AndSwitchesGroupAtomically()
    {
        var id = await CreateMaterialAsync("Группа А", new List<string> { "Подгруппа 1", "Подгруппа 2" });

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            var result = await s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Марка смены группы " + Suffix(),
                GroupName = "Группа Б",
                SubgroupNames = new List<string> { "Подгруппа 3" },
            });

            Assert.NotNull(result);
            Assert.Equal("Группа Б", result!.GroupName);
            Assert.Equal(new[] { "Подгруппа 3" }, result.SubgroupNames);
        }

        // В БД осталась ровно одна строка классификации новой группы.
        await using var s2 = _fixture.CreateScope();
        var rows = await s2.Db.GsmMaterialClassifications.AsNoTracking()
            .Where(c => c.GsmMaterialId == id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Группа Б", rows[0].GroupName);
    }

    [Fact]
    public async Task Update_RejectsSecondGroup_AtDatabaseLevel()
    {
        var id = await CreateMaterialAsync("Группа А", new List<string> { "Подгруппа 1" });

        await using var s = _fixture.CreateScope();
        s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        {
            Id = Guid.NewGuid(),
            GsmMaterialId = id,
            GroupName = "Другая группа",
            SubgroupName = "Подгруппа X",
        });

        // DB-триггер остаётся последней защитой независимо от сервиса.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Equal("23514", ((Npgsql.PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Update_RejectsDuplicateSubgroupPair_AtDatabaseLevel()
    {
        var id = await CreateMaterialAsync("Группа А", new List<string> { "Подгруппа 1" });

        await using var s = _fixture.CreateScope();
        s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        {
            Id = Guid.NewGuid(),
            GsmMaterialId = id,
            GroupName = "  группа а  ",
            SubgroupName = "ПОДГРУППА 1",
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Equal("23505", ((Npgsql.PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Update_Service_DropsDuplicateSubgroups_BeforeWriting()
    {
        var id = await CreateMaterialAsync("Группа А", new List<string> { "Подгруппа 1" });

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var result = await s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
        {
            Name = "Дубли " + Suffix(),
            GroupName = "Группа А",
            SubgroupNames = new List<string> { "Подгруппа 9", " подгруппа 9 ", "Подгруппа 8", "  " },
        });

        Assert.NotNull(result);
        Assert.Equal(2, result!.SubgroupNames.Count);
        Assert.Equal(2, await s.Db.GsmMaterialClassifications.CountAsync(c => c.GsmMaterialId == id));
    }

    [Fact]
    public async Task Update_RejectsGroupWithoutSubgroup()
    {
        var id = await CreateMaterialAsync("Группа А", new List<string> { "Подгруппа 1" });

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Без подгруппы " + Suffix(),
                GroupName = "Группа Б",
                SubgroupNames = new List<string>(),
            }));

        Assert.Contains("подгрупп", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4. Атомарность марки и классификации ──────────────────────────────

    [Fact]
    public async Task Create_WhenClassificationInsertFails_RollsBackMaterialToo()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        // Сбой на вставке классификации внутри той же транзакции.
        FailingCommandInterceptor.ArmAt("INSERT INTO \"GsmMaterialClassifications\"", occurrence: 1);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
                {
                    Name = "Откат " + Suffix(),
                    Nd = "ГОСТ 9999-99",
                    GroupName = "Моторные масла",
                    SubgroupNames = new List<string> { "Для смазки" },
                }));
            Assert.True(FailingCommandInterceptor.Fired, "Контролируемый сбой не сработал");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        // В новом scope: ни марки, ни её классификации, ни записи журнала.
        // Проверка адресная: другие тесты класса создают свои марки и свои audit.
        await using var s2 = _fixture.CreateScope();
        Assert.False(await s2.Db.GsmMaterials.IgnoreQueryFilters()
            .AnyAsync(m => m.Nd == "ГОСТ 9999-99"));
        Assert.False(await s2.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "GsmMaterial"
            && a.Action == "Create"
            && a.EntityDisplayName != null
            && a.EntityDisplayName.EndsWith("ГОСТ 9999-99")));
    }

    [Fact]
    public async Task Update_WhenSecondPhaseFails_RollsBackMaterialAndClassifications()
    {
        var id = await CreateMaterialAsync("Группа А", new List<string> { "Подгруппа 1" });

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        // Падает вторая фаза — вставка нового набора подгрупп. Удаление прежнего
        // набора уже выполнено внутри той же транзакции, поэтому откат обязан
        // вернуть и его, и неизменённую марку.
        FailingCommandInterceptor.ArmAt("INSERT INTO \"GsmMaterialClassifications\"", occurrence: 1);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
                {
                    Name = "Смена группы " + Suffix(),
                    GroupName = "Группа Б",
                    SubgroupNames = new List<string> { "Подгруппа 3", "Подгруппа 4" },
                }));
            Assert.True(FailingCommandInterceptor.Fired, "Контролируемый сбой не сработал");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using var s2 = _fixture.CreateScope();
        var rows = await s2.Db.GsmMaterialClassifications.AsNoTracking()
            .Where(c => c.GsmMaterialId == id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Группа А", rows[0].GroupName);
        Assert.Equal("Подгруппа 1", rows[0].SubgroupName);
    }

    // ── 5. Foreign-защита при включении в номенклатуру по ГОСТ ─────────────

    [Fact]
    public async Task Update_InGostNomenclature_IsBlocked_ForForeignAnalog()
    {
        var (primary, related) = await CreateMaterialPairAsync("Аналог", "Зарубежная");
        await InsertRelationAsync(primary, related, GsmRelationType.Foreign);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        var view = await s.GsmMaterials.GetEditViewAsync(related);
        Assert.NotNull(view);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateAsync(related, new GsmMaterialWriteRequest
            {
                Name = view!.Name,
                InGostNomenclature = true,
                GroupName = view.GroupName,
                SubgroupNames = view.SubgroupNames.ToList(),
            }));

        Assert.Contains("зарубежным аналогом", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False((await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == related)).InGostNomenclature);
    }

    [Fact]
    public async Task Update_InGostNomenclature_IsBlocked_AfterRelationSoftDeleted()
    {
        var (primary, related) = await CreateMaterialPairAsync("Аналог", "Зарубежная 2");
        var relationId = await InsertRelationAsync(primary, related, GsmRelationType.Foreign);

        await using (var s = _fixture.CreateScope())
        {
            var row = await s.Db.GsmMaterialRelations.FirstAsync(r => r.Id == relationId);
            row.IsDeleted = true;
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);
        var view = await s2.GsmMaterials.GetEditViewAsync(related);
        var result = await s2.GsmMaterials.UpdateAsync(related, new GsmMaterialWriteRequest
        {
            Name = view!.Name,
            InGostNomenclature = true,
            GroupName = view.GroupName,
            SubgroupNames = view.SubgroupNames.ToList(),
        });

        Assert.NotNull(result);
        Assert.True((await s2.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == related)).InGostNomenclature);
    }

    // ── 6. Сводный список: одна строка на марку, счётчик по маркам ──────────

    [Fact]
    public async Task Paged_OneRowPerMaterial_EvenWithTwoMatchingSubgroups()
    {
        var id = await CreateMaterialAsync("Группа фильтра", new List<string> { "Масло", "Смазка" });
        var marker = "Фильтр " + Suffix();

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            await s.Db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE ""GsmMaterials"" SET ""Name"" = {marker} WHERE ""Id"" = {id}");
        }

        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);

        var bySearch = await s2.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { Search = marker });
        Assert.Single(bySearch.Items);
        Assert.Equal(1, bySearch.Items.Count(x => x.Id == id));

        // Фильтр по двум подгруппам подряд не размножает строку марки.
        var bySub1 = await s2.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { SubgroupName = "Масло" });
        var bySub2 = await s2.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { SubgroupName = "Смазка" });
        Assert.Equal(1, bySub1.Items.Count(x => x.Id == id));
        Assert.Equal(1, bySub2.Items.Count(x => x.Id == id));

        // Обе подгруппы возвращаются в одной строке, а не двумя строками.
        var row = Assert.Single(bySub1.Items, x => x.Id == id);
        Assert.Equal(2, row.SubgroupNames.Count);
    }

    [Fact]
    public async Task Paged_TotalCount_CountsMaterials_NotSubgroupsOrRelations()
    {
        var id = await CreateMaterialAsync("Группа счётчика " + Suffix(),
            new List<string> { "Подгруппа 1", "Подгруппа 2", "Подгруппа 3" });
        var (other, related) = await CreateMaterialPairAsync("Связанная", "Связанная 2");
        await InsertRelationAsync(id, other, GsmRelationType.DuplicateAndReserve);
        await InsertRelationAsync(id, related, GsmRelationType.Reserve);

        var group = "Группа счётчика ";
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var page = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { GroupName = group, PageSize = 200 });

        // Три подгруппы и две связи не превращают одну марку в пять строк.
        Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        var row = page.Items[0];
        Assert.Equal(id, row.Id);
        Assert.Equal(3, row.SubgroupNames.Count);

        // DuplicateAndReserve показывается в обеих колонках, обычный Reserve — только в резервных.
        Assert.Single(row.DuplicateNames);
        Assert.Equal(2, row.ReserveNames.Count);
        Assert.Contains(row.DuplicateNames[0], row.ReserveNames);
    }

    [Fact]
    public async Task Paged_RelationColumns_FollowRelationType_AndSkipDeleted()
    {
        var (primary, dup) = await CreateMaterialPairAsync("Основная", "Дубликат");
        var (_, reserve) = await CreateMaterialPairAsync("Резервная", "Резервная марка");
        var (_, foreign) = await CreateMaterialPairAsync("Зарубежная", "Зарубежная марка");
        var (_, both) = await CreateMaterialPairAsync("Обе", "Обе марка");
        var (_, deletedRel) = await CreateMaterialPairAsync("Удалённая связь", "Скрытая марка");

        await InsertRelationAsync(primary, dup, GsmRelationType.Duplicate);
        await InsertRelationAsync(primary, reserve, GsmRelationType.Reserve);
        await InsertRelationAsync(primary, foreign, GsmRelationType.Foreign);
        await InsertRelationAsync(primary, both, GsmRelationType.DuplicateAndReserve);
        var deletedRelationId = await InsertRelationAsync(primary, deletedRel, GsmRelationType.Duplicate);

        await using (var s = _fixture.CreateScope())
        {
            var relation = await s.Db.GsmMaterialRelations.FirstAsync(r => r.Id == deletedRelationId);
            relation.IsDeleted = true;
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);
        var page = await s2.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { Search = "Основная" });
        var row = Assert.Single(page.Items, x => x.Id == primary);

        // DuplicateAndReserve присутствует в обеих колонках, Foreign — только в своей.
        Assert.Equal(new[] { "Дубликат", "Обе марка" }.OrderBy(x => x), row.DuplicateNames.OrderBy(x => x));
        Assert.Equal(new[] { "Обе марка", "Резервная марка" }.OrderBy(x => x), row.ReserveNames.OrderBy(x => x));
        Assert.Equal(new[] { "Зарубежная марка" }, row.ForeignNames);
        Assert.DoesNotContain("Скрытая марка", row.DuplicateNames);
    }

    [Fact]
    public async Task Paged_ShowsRelationNames_NotGuids()
    {
        var (primary, other) = await CreateMaterialPairAsync("Именованная", "Аналог с именем");
        await InsertRelationAsync(primary, other, GsmRelationType.Duplicate);

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        var page = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { Search = "Именованная" });
        var row = Assert.Single(page.Items, x => x.Id == primary);

        Assert.Equal(new[] { "Аналог с именем" }, row.DuplicateNames);
        Assert.DoesNotContain(row.DuplicateNames, n => Guid.TryParse(n, out _));
    }

    [Fact]
    public async Task Paged_OnlyUnclassified_IncludesDraftsAndExcludesClassified()
    {
        var classified = await CreateMaterialAsync("Классифицированная", new List<string> { "Подгруппа" });
        var legacy = await CreateLegacyMaterialAsync("Без классификации " + Suffix());

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var page = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery
        {
            OnlyUnclassified = true,
            PageSize = 200,
        });

        Assert.Contains(page.Items, x => x.Id == legacy);
        Assert.DoesNotContain(page.Items, x => x.Id == classified);
        Assert.All(page.Items, x => Assert.False(x.HasClassification));
    }

    [Fact]
    public async Task Paged_SuitabilityAndGostAndNatoFilters_WorkServerSide()
    {
        var air = await CreateMaterialAsync("Группа А", new List<string> { "Авиа" });
        var ground = await CreateMaterialAsync("Группа Б", new List<string> { "Наземная" });

        await using (var s = _fixture.CreateScope())
        {
            SetRefEditor(s);
            await s.GsmMaterials.UpdateAsync(air, new GsmMaterialWriteRequest
            {
                Name = "Авиационная " + Suffix(),
                SuitabilityAir = true,
                NatoIndex = "F-16",
                InGostNomenclature = true,
                GroupName = "Группа А",
                SubgroupNames = new List<string> { "Авиа" },
            });
            await s.GsmMaterials.UpdateAsync(ground, new GsmMaterialWriteRequest
            {
                Name = "Наземная " + Suffix(),
                SuitabilityGround = true,
                GroupName = "Группа Б",
                SubgroupNames = new List<string> { "Наземная" },
            });
        }

        await using var s2 = _fixture.CreateScope();
        SetRefEditor(s2);

        var byAir = await s2.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { SuitabilityAny = true, Search = "Авиационная" });
        Assert.Single(byAir.Items, x => x.Id == air);

        var byGost = await s2.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { InGostNomenclature = true, PageSize = 200 });
        Assert.Contains(byGost.Items, x => x.Id == air);
        Assert.DoesNotContain(byGost.Items, x => x.Id == ground);

        var byNato = await s2.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { NatoIndex = "F-1", PageSize = 200 });
        Assert.Contains(byNato.Items, x => x.Id == air);
    }

    // ── 7. Прямой FK-guard удаления ────────────────────────────────────────

    [Fact]
    public async Task Delete_IsBlocked_ForEveryCardStatus_AndKeepsRow()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        foreach (var status in new[]
                 {
                     HKCardStatus.Draft, HKCardStatus.Approved,
                     HKCardStatus.Archived, HKCardStatus.Deleted,
                 })
        {
            var materialId = await CreateMaterialAsync("Группа " + Suffix(), new List<string> { "Подгруппа" });
            var rowId = await CreateHkMaterialRowAsync(materialId, status);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.GsmMaterials.DeleteAsync(materialId));
            Assert.Contains("ХК", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Марка жива, строка ХК на месте.
            Assert.True(await s.Db.GsmMaterials.IgnoreQueryFilters()
                .AnyAsync(m => m.Id == materialId && !m.IsDeleted));
            Assert.True(await s.Db.HKCardItemMaterials.AnyAsync(r => r.Id == rowId));
        }
    }

    [Fact]
    public async Task Delete_AndRestore_Work_ForUnreferencedMaterial()
    {
        var materialId = await CreateMaterialAsync("Группа " + Suffix(), new List<string> { "Подгруппа" });

        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        Assert.True(await s.GsmMaterials.DeleteAsync(materialId));
        Assert.False(await s.Db.GsmMaterials.AnyAsync(m => m.Id == materialId));
        Assert.True(await s.Db.GsmMaterials.IgnoreQueryFilters().AnyAsync(m => m.Id == materialId && m.IsDeleted));

        Assert.True(await s.GsmMaterials.RestoreAsync(materialId));
        Assert.True(await s.Db.GsmMaterials.AnyAsync(m => m.Id == materialId && !m.IsDeleted));
    }

    // ── 8. Права View и Edit раздельно ─────────────────────────────────────

    [Fact]
    public async Task ReferenceView_AllowsRead_ButNotWrite()
    {
        var id = await CreateMaterialAsync("Группа прав " + Suffix(), new List<string> { "Подгруппа" });

        await using var s = _fixture.CreateScope();

        // Собственный пользователь только для чтения: общие тесты выдают
        // фикстурным пользователям персональные override, и опираться на них
        // нельзя — иначе результат зависел бы от порядка выполнения.
        var viewerId = await CreateViewerUserAsync(s);
        s.User.CurrentUserId = Guid.Parse(viewerId);

        // Чтение доступно.
        Assert.NotNull(await s.GsmMaterials.GetEditViewAsync(id));
        Assert.NotNull(await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery()));

        // Запись — нет, и это серверная проверка, а не скрытие кнопок.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
            {
                Name = "Не должна создаться " + Suffix(),
                GroupName = "Группа",
                SubgroupNames = new List<string> { "Подгруппа" },
            }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.UpdateAsync(id, new GsmMaterialWriteRequest
            {
                Name = "Не должна обновиться",
                GroupName = "Группа",
                SubgroupNames = new List<string> { "Подгруппа" },
            }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.GsmMaterials.DeleteAsync(id));
    }

    [Fact]
    public async Task Unauthenticated_IsRejected()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
            {
                Name = "Аноним " + Suffix(),
                GroupName = "Группа",
                SubgroupNames = new List<string> { "Подгруппа" },
            }));
    }

    // ── 9. Черновик предложения нельзя опубликовать без классификации ─────

    [Fact]
    public async Task AcceptProposal_IsBlocked_WhenStubUnclassified_AndStatusStaysPending()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var (proposalId, stubId) = await CreatePendingGsmProposalAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.AcceptProposalAsync(proposalId));
        Assert.Contains("не классифицирована", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var s2 = _fixture.CreateScope();
        var proposal = await s2.Db.ReferenceProposals.FindAsync(proposalId);
        Assert.Equal(ProposalStatus.Pending, proposal!.Status);
        var stub = await s2.Db.GsmMaterials.IgnoreQueryFilters().FirstAsync(m => m.Id == stubId);
        Assert.True(stub.IsDraft);
    }

    [Fact]
    public async Task AcceptProposal_Succeeds_AfterStubIsClassified()
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);

        var (proposalId, stubId) = await CreatePendingGsmProposalAsync();

        await s.GsmMaterials.UpdateAsync(stubId, new GsmMaterialWriteRequest
        {
            Name = "Черновик классифицирован " + Suffix(),
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Для дизельных двигателей" },
        });

        await s.HK.AcceptProposalAsync(proposalId);

        await using var s2 = _fixture.CreateScope();
        Assert.Equal(ProposalStatus.Accepted, (await s2.Db.ReferenceProposals.FindAsync(proposalId))!.Status);
        Assert.False((await s2.Db.GsmMaterials.IgnoreQueryFilters().FirstAsync(m => m.Id == stubId)).IsDraft);
    }

    // ── Фикстуры данных ────────────────────────────────────────────────────

    /// <summary>Пользователь с ролью «Гость» и явно запрещённым Reference.Edit,
    /// созданный этим тестом: состояние прав не зависит от других тестов.</summary>
    private async Task<string> CreateViewerUserAsync(TestScope s)
    {
        var user = new Chernika.Domain.Entities.ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "gsm_viewer_" + Suffix(),
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
            PermissionCode = Chernika.Domain.PermissionCodes.ReferenceEdit,
            IsGranted = false,
            Reason = "Test",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
        return user.Id;
    }

    private async Task<Guid> CreateMaterialAsync(string group, List<string> subgroups, string? name = null)
    {
        await using var s = _fixture.CreateScope();
        SetRefEditor(s);
        var created = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name ?? "Марка " + Suffix(),
            Nd = "ГОСТ " + Suffix(),
            GroupName = group,
            SubgroupNames = subgroups,
        });
        return created.Id;
    }

    /// <summary>Legacy-марка: создаётся с классификацией, затем классификация снимается,
    /// чтобы получить состояние «опубликована, но без группы».</summary>
    private async Task<Guid> CreateLegacyMaterialAsync(string name)
    {
        var id = await CreateMaterialAsync("Группа " + Suffix(), new List<string> { "Подгруппа" }, name);
        await using var s = _fixture.CreateScope();
        var rows = await s.Db.GsmMaterialClassifications.Where(c => c.GsmMaterialId == id).ToListAsync();
        s.Db.GsmMaterialClassifications.RemoveRange(rows);
        await s.Db.SaveChangesAsync();
        return id;
    }

    private async Task<(Guid Primary, Guid Related)> CreateMaterialPairAsync(string primaryName, string relatedName)
    {
        var primary = await CreateMaterialAsync("Группа " + Suffix(), new List<string> { "Подгруппа" }, primaryName);
        var related = await CreateMaterialAsync("Группа " + Suffix(), new List<string> { "Подгруппа" }, relatedName);
        return (primary, related);
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

    private async Task<Guid> CreateHkMaterialRowAsync(Guid materialId, HKCardStatus status)
    {
        await using var s = _fixture.CreateScope();

        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();

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
        await s.Db.SaveChangesAsync();

        return row.Id;
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
            Name = stub.Name,
            Status = ProposalStatus.Pending,
            CreatedStubGsmMaterialId = stub.Id,
            CreatedByUserId = _fixture.NormAdminA.Id,
            CreatedAt = DateTime.UtcNow,
        };
        s.Db.ReferenceProposals.Add(proposal);
        await s.Db.SaveChangesAsync();

        return (proposal.Id, stub.Id);
    }
}
