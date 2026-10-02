using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Корректировка PR-4: трёхсостоянийный фильтр статуса, выбор марок по
/// идентификатору и устойчивая сортировка.
/// </summary>
[Collection("Database")]
public class GsmRelationFilterAndSelectionIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmRelationFilterAndSelectionIntegrationTests(TestDatabaseFixture fixture) =>
        _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── 1. Три состояния фильтра статуса ──────────────────────────────────

    [Fact]
    public async Task StatusFilter_ActiveDeletedAndAll_MatchContract()
    {
        var a = await CreateMaterialAsync("Фильтр A " + Suffix());
        var b = await CreateMaterialAsync("Фильтр B " + Suffix());

        Guid keptId;
        Guid removedId;
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);

            // Две связи с одинаковым набором признаков, чтобы пересечение по
            // типу и направлению гарантированно содержало обе.
            keptId = (await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Duplicate,
            })).Id;

            removedId = (await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = b,
                RelatedGsmMaterialId = a,
                RelationType = GsmRelationType.Duplicate,
            })).Id;

            Assert.True(await s.GsmMaterials.DeleteRelationAsync(removedId));
        }

        // Выборки ограничены основной маркой этой пары и увеличенной страницей:
        // общая тестовая БД накапливает связи других тестов, и без ограничения
        // проверяемые строки просто не попали бы на первую страницу.

        // false → только активные: удалённая связь исключена.
        var active = await QueryAsync(new GsmRelationQuery
        { ShowDeleted = false, PrimaryGsmMaterialId = a, PageSize = 100 });
        Assert.DoesNotContain(active.Items, r => r.Id == removedId);
        Assert.Contains(active.Items, r => r.Id == keptId);

        // true → только удалённые: активная связь исключена. Удалённая связь
        // имеет primary = b, поэтому выборка идёт по ней.
        var deleted = await QueryAsync(new GsmRelationQuery
        { ShowDeleted = true, PrimaryGsmMaterialId = b, PageSize = 100 });
        Assert.DoesNotContain(deleted.Items, r => r.Id == keptId);
        Assert.Contains(deleted.Items, r => r.Id == removedId);

        // null → все обе: собираем по обеим маркам.
        var allActiveSide = await QueryAsync(new GsmRelationQuery
        { ShowDeleted = null, PrimaryGsmMaterialId = a, PageSize = 100 });
        Assert.Contains(allActiveSide.Items, r => r.Id == keptId);

        var allDeletedSide = await QueryAsync(new GsmRelationQuery
        { ShowDeleted = null, PrimaryGsmMaterialId = b, PageSize = 100 });
        Assert.Contains(allDeletedSide.Items, r => r.Id == removedId);

        // Счётчики согласованы с выдачей, а активные и удалённые в сумме дают
        // полный набор: пересечений и потерь между режимами нет.
        Assert.Equal(active.Items.Count, active.TotalCount);
        Assert.Equal(deleted.Items.Count, deleted.TotalCount);
        Assert.Equal(allActiveSide.Items.Count, allActiveSide.TotalCount);
        Assert.Equal(allDeletedSide.Items.Count, allDeletedSide.TotalCount);
        Assert.Equal(
            active.TotalCount + deleted.TotalCount,
            allActiveSide.TotalCount + allDeletedSide.TotalCount);
    }

    [Fact]
    public async Task StatusFilter_CombinedWithTypeAndPrimary_HonoursAllConditions()
    {
        var a = await CreateMaterialAsync("Комбо A " + Suffix());
        var b = await CreateMaterialAsync("Комбо B " + Suffix());
        var c = await CreateMaterialAsync("Комбо C " + Suffix());

        Guid dupId;
        Guid resId;
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            dupId = (await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Duplicate,
            })).Id;
            resId = (await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = c,
                RelationType = GsmRelationType.Reserve,
            })).Id;
            Assert.True(await s.GsmMaterials.DeleteRelationAsync(resId));
        }

        // Удалённая связьReserve скрыта в режиме «активные».
        var activeOnly = await QueryAsync(new GsmRelationQuery
        {
            ShowDeleted = false,
            PrimaryGsmMaterialId = a,
            PageSize = 100,
        });
        Assert.Contains(activeOnly.Items, r => r.Id == dupId);
        Assert.DoesNotContain(activeOnly.Items, r => r.Id == resId);

        // Тип + режим «удалённые» пересекаются по обоим условиям.
        var deletedReserve = await QueryAsync(new GsmRelationQuery
        {
            ShowDeleted = true,
            RelationType = GsmRelationType.Reserve,
            PrimaryGsmMaterialId = a,
            PageSize = 100,
        });
        Assert.Contains(deletedReserve.Items, r => r.Id == resId);
        Assert.DoesNotContain(deletedReserve.Items, r => r.Id == dupId);
        Assert.Equal(deletedReserve.Items.Count, deletedReserve.TotalCount);
    }

    [Fact]
    public async Task StatusFilter_PaginationCoversEveryStatusWithoutOverlap()
    {
        var a = await CreateMaterialAsync("Страницы A " + Suffix());
        var b = await CreateMaterialAsync("Страницы B " + Suffix());

        // Три РАЗНЫЕ направленные пары: одна и та же пара не может иметь трёх
        // активных связей из-за частичного UNIQUE, поэтому для проверки пагинации
        // по статусу пары должны различаться.
        var c = await CreateMaterialAsync("Страницы C " + Suffix());
        var d = await CreateMaterialAsync("Страницы D " + Suffix());
        var e = await CreateMaterialAsync("Страницы E " + Suffix());

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var pairs = new[] { (a, b), (a, c), (a, d) };
            for (var i = 0; i < pairs.Length; i++)
            {
                var rel = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
                {
                    PrimaryGsmMaterialId = pairs[i].Item1,
                    RelatedGsmMaterialId = pairs[i].Item2,
                    RelationType = (i % 2 == 0) ? GsmRelationType.Duplicate : GsmRelationType.Reserve,
                });
                if (i == 1) Assert.True(await s.GsmMaterials.DeleteRelationAsync(rel.Id));
            }
            _ = e;
        }

        // Из трёх созданных связей одна удалена, поэтому ожидаемое число строк
        // различается по режимам: активные — 2, удалённые — 1, все — 3.
        // Кортежи, а не Dictionary: ключом режима «все» является null.
        (bool? Mode, int Expected)[] expected =
        {
            (false, 2),
            (true, 1),
            (null, 3),
        };

        foreach (var (mode, expectedCount) in expected)
        {
            var seen = new List<Guid>();
            var page = 1;
            int total;
            do
            {
                var result = await QueryAsync(new GsmRelationQuery
                {
                    ShowDeleted = mode,
                    PrimaryGsmMaterialId = a,
                    Page = page,
                    PageSize = 2,
                });
                // Пагинация проверяется именно по этой основной марке: чужой
                // счётчик общей БД сделал бы проверку зависимой от порядка тестов.
                total = result.TotalCount;
                seen.AddRange(result.Items.Select(r => r.Id));
                page++;
            }
            while (seen.Count < total && page <= 20);

            // Все связи этого набора учтены ровно один раз, дублей страниц нет.
            Assert.Equal(expectedCount, seen.Count);
            Assert.Equal(expectedCount, total);
            Assert.Equal(seen.Count, seen.Distinct().Count());
        }
    }

    [Fact]
    public async Task Sorting_IsStableAcrossRepeatedQueries_WhenNamesAndTypesAreEqual()
    {
        var suffix = Suffix();
        var a = await CreateMaterialAsync("Стабильная " + suffix);
        var b = await CreateMaterialAsync("Стабильная " + suffix);
        var c = await CreateMaterialAsync("Стабильная " + suffix);

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            foreach (var (p, r) in new[] { (a, b), (b, c), (c, a) })
            {
                await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
                {
                    PrimaryGsmMaterialId = p,
                    RelatedGsmMaterialId = r,
                    RelationType = GsmRelationType.Duplicate,
                });
            }
        }

        // Имена и типы совпадают во всех трёх строках: без ThenBy(Id) порядок
        // мог бы отличаться между прогонами и страницы «прыгали» бы.
        foreach (var sortBy in new[] { "Primary", "Related", "RelationType" })
        {
            var first = await QueryAsync(new GsmRelationQuery
            {
                PrimaryGsmMaterialId = a,
                SortBy = sortBy,
                PageSize = 50,
                ShowDeleted = null,
            });

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var again = await QueryAsync(new GsmRelationQuery
                {
                    PrimaryGsmMaterialId = a,
                    SortBy = sortBy,
                    PageSize = 50,
                    ShowDeleted = null,
                });

                Assert.Equal(
                    first.Items.Select(x => x.Id).ToList(),
                    again.Items.Select(x => x.Id).ToList());
            }
        }
    }

    // ── 2. Выбор марок по идентификатору ──────────────────────────────────

    [Fact]
    public async Task MaterialOptions_DuplicateNames_ProduceDistinguishableLabels()
    {
        // Уникальности Name нет: две одноимённые марки с разным НД обязаны быть
        // различимы, иначе выбор «первой молча» создал бы связь не с той маркой.
        var suffix = Suffix();
        var name = "Одноимённая " + suffix;
        var first = await CreateMaterialAsync(name, nd: "ГОСТ 11111-2019");
        var second = await CreateMaterialAsync(name, nd: "ГОСТ 22222-2020");

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var options = await s.GsmMaterials.GetRelationMaterialOptionsAsync(name);
        Assert.Equal(2, options.Count);

        var labels = options.Select(o => o.DisplayLabel).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(options, o => o.DisplayLabel.Contains("11111", StringComparison.Ordinal));
        Assert.Contains(options, o => o.DisplayLabel.Contains("22222", StringComparison.Ordinal));

        // Идентификаторы различны, и каждый ведёт к своей марке.
        Assert.Contains(options, o => o.Id == first);
        Assert.Contains(options, o => o.Id == second);
    }

    [Fact]
    public async Task MaterialOptions_IdenticalVisibleData_StillDistinguishableByVariant()
    {
        // Совпадает всё отображаемое: имя, НД, группа и подгруппа. Различить их
        // иначе нечем, поэтому подпись дополняется порядковым номером — выбор не
        // становится молчаливым.
        var suffix = Suffix();
        var group = "Группа " + Suffix();
        var subgroup = "Подгруппа " + Suffix();
        var name = "Полный дубль " + suffix;
        var nd = "ГОСТ 33333-2021";

        var first = await CreateMaterialAsync(name, nd, group, subgroup);
        var second = await CreateMaterialAsync(name, nd, group, subgroup);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var options = await s.GsmMaterials.GetRelationMaterialOptionsAsync(name);
        Assert.Equal(2, options.Count);

        // Все отображаемые поля совпадают — значит подписи обязаны различаться.
        Assert.All(options, o => Assert.Equal(name, o.Name));
        Assert.All(options, o => Assert.Equal(nd, o.Nd));
        Assert.All(options, o => Assert.Equal(group, o.GroupName));

        var labels = options.Select(o => o.DisplayLabel).ToList();
        Assert.Equal(2, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(options, o => o.DisplayLabel.Contains("вариант", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options, o => o.Id == first);
        Assert.Contains(options, o => o.Id == second);
    }

    [Fact]
    public async Task MaterialOptions_SearchReachesRecordsBeyondFirstPage()
    {
        // 200 — предел одной выдачи. Марка, не попавшая в первые 200 по сортировке
        // имени, всё равно должна находиться поиском по своему названию.
        var suffix = Suffix();
        var marker = "Якорь " + suffix;

        // Наполнение справочника выполняется прямыми вставками одной пачкой:
        // 205 вызовов CreateAsync были бы медленными, а суть проверки — не в
        // бизнес-логике создания марок, а в границе выдачи и серверном поиске.
        await using (var setup = _fixture.CreateScope())
        {
            var filler = Enumerable.Range(0, 205).Select(i => new GsmMaterial
            {
                Id = Guid.NewGuid(),
                Name = $"Ааа{i:D3} {suffix}",
                IsDeleted = false,
                IsDraft = false,
            }).ToList();
            setup.Db.GsmMaterials.AddRange(filler);
            await setup.Db.SaveChangesAsync();
        }

        var target = await CreateMaterialAsync(marker);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // Без поиска якорь не обязан попасть в выдачу — ограничение 200 реально.
        var all = await s.GsmMaterials.GetRelationMaterialOptionsAsync();
        Assert.True(all.Count <= 200);
        Assert.DoesNotContain(all, o => o.Id == target);

        // Но поиском по названию марка находится.
        var found = await s.GsmMaterials.GetRelationMaterialOptionsAsync(marker);
        Assert.Contains(found, o => o.Id == target);
    }

    [Fact]
    public async Task MaterialOptions_IncludeIds_ReturnsSelectedMaterialNotInSearchPage()
    {
        // Правка связи не должна терять выбранную марку, если она не попала в
        // текущую выдачу поиска.
        var suffix = Suffix();
        var marker = "Избранная " + suffix;
        var target = await CreateMaterialAsync(marker);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var withInclude = await s.GsmMaterials.GetRelationMaterialOptionsAsync(
            "ничего не совпадёт", default, new[] { target });

        Assert.Contains(withInclude, o => o.Id == target);
    }

    [Fact]
    public async Task MaterialOptions_NeverOfferDeletedOrDraft()
    {
        var deleted = await CreateMaterialAsync("Удалённая выбор " + Suffix());
        var draft = await CreateDraftMaterialAsync();

        await using (var setup = _fixture.CreateScope())
        {
            setup.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
            Assert.True(await setup.GsmMaterials.DeleteAsync(deleted));
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var options = await s.GsmMaterials.GetRelationMaterialOptionsAsync();
        var ids = options.Select(o => o.Id).ToHashSet();

        Assert.DoesNotContain(deleted, ids);
        Assert.DoesNotContain(draft, ids);
        Assert.All(options, o => Assert.False(o.IsDeleted));
        Assert.All(options, o => Assert.False(o.IsDraft));
    }

    // ── 3. Успешность правки ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateRelation_UnknownId_ReturnsNull_SoUiCannotReportSuccess()
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var result = await s.GsmMaterials.UpdateRelationAsync(Guid.NewGuid(), new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = Guid.NewGuid(),
            RelatedGsmMaterialId = Guid.NewGuid(),
            RelationType = GsmRelationType.Duplicate,
        });

        // null обязан отличаться от успеха: UI по нему показывает «связь не найдена».
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateRelation_DeletedRelation_MessageOffersNewRelation_NotRestore()
    {
        var a = await CreateMaterialAsync("Нет restore " + Suffix());
        var b = await CreateMaterialAsync("Нет restore " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });
        Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateRelationAsync(relation.Id, new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Reserve,
            }));

        // restore не реализован, поэтому инструкция «восстановите» была ложной.
        Assert.DoesNotContain("восстанов", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("создайте новую", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRelation_IsIdempotent_SoRepeatedConfirmIsNotAnError()
    {
        var a = await CreateMaterialAsync("Идемпотентность " + Suffix());
        var b = await CreateMaterialAsync("Идемпотентность " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));
        Assert.False(await s.GsmMaterials.DeleteRelationAsync(relation.Id));
    }

    // ── Права: команды записи недоступны без Reference.Edit ────────────────

    [Fact]
    public async Task Operator_WithoutReferenceEdit_CanReadOptions_ButCannotWrite()
    {
        var a = await CreateMaterialAsync("Права коррекции " + Suffix());
        var b = await CreateMaterialAsync("Права коррекции " + Suffix());

        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        // Запись запрещена сервером независимо от того, скрыты ли кнопки в UI.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Duplicate,
            }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.DeleteRelationAsync(Guid.NewGuid()));

        var existing = await CreateMaterialAsync("Права чтения " + Suffix());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.UpdateRelationAsync(Guid.NewGuid(), new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = existing,
                RelatedGsmMaterialId = existing,
                RelationType = GsmRelationType.Duplicate,
            }));

        // Чтение справочника выбора доступно: для просмотра справочников нужно
        // право View, а не Edit.
        var options = await s.GsmMaterials.GetRelationMaterialOptionsAsync();
        Assert.NotNull(options);
        Assert.NotNull(await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery()));
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────

    private async Task<GsmRelationListResult> QueryAsync(GsmRelationQuery query)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var result = await s.GsmMaterials.GetRelationsPagedAsync(query);
        return new GsmRelationListResult(result.Items.ToList(), result.TotalCount);
    }

    private sealed record GsmRelationListResult(List<GsmRelationSummary> Items, int TotalCount);

    private async Task<Guid> CreateMaterialAsync(
        string name, string? nd = null, string? group = null, string? subgroup = null)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name,
            Nd = nd,
            GroupName = group ?? ("Группа " + Suffix()),
            SubgroupNames = new List<string> { subgroup ?? ("Подгруппа " + Suffix()) },
        });
        return view.Id;
    }

    private async Task<Guid> CreateDraftMaterialAsync()
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var id = Guid.NewGuid();
        s.Db.GsmMaterials.Add(new GsmMaterial
        {
            Id = id,
            Name = "Черновик " + Suffix(),
            IsDeleted = false,
            IsDraft = true,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }
}
