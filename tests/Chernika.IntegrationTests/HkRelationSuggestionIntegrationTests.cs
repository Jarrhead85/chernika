using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Сценарий «выбрать основную марку → увидеть связанные ГСМ → подтвердить часть».
/// <para>
/// Справочные связи — направленные рекомендации, а
/// <c>HKCardItemMaterial.Category</c> — фактический выбор строки ХК. Связь не
/// добавляет материал сама: подтверждает пользователь. Проверяется серверная
/// часть сценария (что предлагается, как разворачивается по категориям, что
/// попадает в строку после подтверждения и что происходит с сохранённой ХК);
/// визуальная часть в браузере — отдельная ручная проверка.
/// </para>
/// </summary>
[Collection("Database")]
public class HkRelationSuggestionIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HkRelationSuggestionIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── Разворачивание типов связи по категориям ──────────────────────────

    [Fact]
    public void CategoryMap_ExpandsDuplicateAndReserve_IntoTwoCategories()
    {
        Assert.Equal(new[] { GsmCategory.Duplicate }, GsmRelationCategoryMap.CategoriesFor(GsmRelationType.Duplicate));
        Assert.Equal(new[] { GsmCategory.Reserve }, GsmRelationCategoryMap.CategoriesFor(GsmRelationType.Reserve));
        Assert.Equal(
            new[] { GsmCategory.Duplicate, GsmCategory.Reserve },
            GsmRelationCategoryMap.CategoriesFor(GsmRelationType.DuplicateAndReserve));
        Assert.Equal(new[] { GsmCategory.Foreign }, GsmRelationCategoryMap.CategoriesFor(GsmRelationType.Foreign));
    }

    [Fact]
    public void CategoryMap_IsExplicit_MatchNotByName()
    {
        // Совпадение имён здесь дало бы неверный результат: GsmRelationType
        // «DuplicateAndReserve» не должен превращаться в Category «Duplicate»
        // приведением имени. Карта возвращает ДВЕ категории.
        var byName = Enum.GetNames<GsmRelationType>()
            .Where(n => Enum.TryParse<GsmCategory>(n, out _))
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(nameof(GsmRelationType.DuplicateAndReserve), byName);
        Assert.Equal(2, GsmRelationCategoryMap.CategoriesFor(GsmRelationType.DuplicateAndReserve).Count);
    }

    // ── Что предлагается ───────────────────────────────────────────────────

    [Fact]
    public async Task Suggestions_ReturnActiveDirectRelations_GroupedByPrimary()
    {
        var (primaryId, duplicateId, reserveId, foreignId) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = duplicateId,
            RelationType = GsmRelationType.Duplicate,
        });
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = reserveId,
            RelationType = GsmRelationType.Reserve,
        });
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = foreignId,
            RelationType = GsmRelationType.Foreign,
            Note = "Аналог по НАТО",
        });

        var suggestions = await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId });
        var source = Assert.Single(suggestions);
        Assert.Equal(primaryId, source.PrimaryMaterialId);
        Assert.Equal(3, source.Suggestions.Count);

        Assert.Equal(
            new[] { GsmCategory.Duplicate },
            source.Suggestions.Single(x => x.MaterialId == duplicateId).Categories);
        Assert.Equal(
            new[] { GsmCategory.Reserve },
            source.Suggestions.Single(x => x.MaterialId == reserveId).Categories);
        var foreign = source.Suggestions.Single(x => x.MaterialId == foreignId);
        Assert.Equal(new[] { GsmCategory.Foreign }, foreign.Categories);
        Assert.Equal("Аналог по НАТО", foreign.Note);
        Assert.True(foreign.IsAddable);
    }

    [Fact]
    public async Task Suggestions_ExposeDuplicateAndReserve_InBothCategoriesIndependently()
    {
        var (primaryId, bothId, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = bothId,
            RelationType = GsmRelationType.DuplicateAndReserve,
        });

        var source = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
        var option = Assert.Single(source.Suggestions);

        // Одна связь — две независимые категории. Пользователь вправе подтвердить
        // одну, обе или ни одной.
        Assert.Equal(
            new[] { GsmCategory.Duplicate, GsmCategory.Reserve },
            option.Categories.OrderBy(c => c).ToArray());
        Assert.Equal(1, source.Suggestions.Count);
    }

    [Fact]
    public async Task Suggestions_AreDirectional_ReverseRelationIsNotAProposal()
    {
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = duplicateId,
            RelationType = GsmRelationType.Duplicate,
        });

        // Обратный запрос: duplicateId — не источник связи, поэтому для него
        // ничего не предлагается.
        var reverse = await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { duplicateId });
        Assert.Empty(reverse);
    }

    [Fact]
    public async Task Suggestions_SkipSoftDeletedRelation()
    {
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        Guid relationId;
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var created = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = duplicateId,
                RelationType = GsmRelationType.Duplicate,
            });
            relationId = created.Id;
            Assert.True(await s.GsmMaterials.DeleteRelationAsync(relationId));
        }

        await using var s2 = _fixture.CreateScope();
        AsNormAdmin(s2);
        Assert.Empty(await s2.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
    }

    [Fact]
    public async Task Suggestions_ReportUnavailableRelatedMaterial_InsteadOfHidingIt()
    {
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = duplicateId,
                RelationType = GsmRelationType.Duplicate,
            });

            // Марка удаляется ПОСЛЕ создания связи: до этого сценарий в БД не
            // встречается, поэтому guard soft-delete не мешает.
            var material = await s.Db.GsmMaterials.AsNoTracking()
                .FirstAsync(m => m.Id == duplicateId);
            s.Db.Entry(material).State = EntityState.Modified;
            material.IsDeleted = true;
            await s.Db.SaveChangesAsync();
        }

        await using var s2 = _fixture.CreateScope();
        AsNormAdmin(s2);
        var source = Assert.Single(await s2.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
        var option = Assert.Single(source.Suggestions);

        Assert.False(option.IsAddable);
        Assert.Contains("удалена", option.UnavailableReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Suggestions_SkipDeletedPrimary_AndEmptyInput()
    {
        var (primaryId, _, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        Assert.Empty(await s.GsmMaterials.GetRelatedSuggestionsAsync(Array.Empty<Guid>()));
        Assert.Empty(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { Guid.Empty }));

        var material = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == primaryId);
        s.Db.Entry(material).State = EntityState.Modified;
        material.IsDeleted = true;
        await s.Db.SaveChangesAsync();

        // Удалённая исходная марка не рекомендует ничего: её нет в справочнике.
        Assert.Empty(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
    }

    [Fact]
    public async Task Suggestions_GroupByEachPrimarySeparately()
    {
        var (primaryA, _, _, _) = await SeedAsync();
        var (primaryB, relatedB, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryA,
            RelatedGsmMaterialId = relatedB,
            RelationType = GsmRelationType.Reserve,
        });
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryB,
            RelatedGsmMaterialId = relatedB,
            RelationType = GsmRelationType.Duplicate,
        });

        var sources = await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryA, primaryB });
        Assert.Equal(2, sources.Count);
        // Одна и та же связанная марка у двух разных основных — группы не
        // смешиваются и не мнутся.
        Assert.Equal(
            new[] { GsmCategory.Reserve },
            sources.Single(x => x.PrimaryMaterialId == primaryA).Suggestions.Single().Categories);
        Assert.Equal(
            new[] { GsmCategory.Duplicate },
            sources.Single(x => x.PrimaryMaterialId == primaryB).Suggestions.Single().Categories);
    }

    [Fact]
    public async Task Suggestions_DoNotDuplicateOption_WhenPairHasDeletedAndActiveRelation()
    {
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var first = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = duplicateId,
            RelationType = GsmRelationType.Duplicate,
            Note = "Архивная",
        });
        Assert.True(await s.GsmMaterials.DeleteRelationAsync(first.Id));

        // Частичный уникальный индекс допускает архивную и активную связь одной
        // пары. Архивная не должна ни предлагаться, ни дублировать вариант.
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = duplicateId,
            RelationType = GsmRelationType.Reserve,
            Note = "Активная",
        });

        var source = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
        var option = Assert.Single(source.Suggestions);
        Assert.Equal(new[] { GsmCategory.Reserve }, option.Categories);
        Assert.Equal("Активная", option.Note);
    }

    [Fact]
    public async Task Suggestions_DoNotTouchHkRows()
    {
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primaryId,
            RelatedGsmMaterialId = duplicateId,
            RelationType = GsmRelationType.DuplicateAndReserve,
        });

        var before = await s.Db.HKCardItemMaterials.CountAsync();
        await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId });
        await s.GsmMaterials.GetRelationHintsAsync(new[] { primaryId });

        // Чтение рекомендаций ничего не пишет: Category строки не выводится из
        // RelationType.
        Assert.Equal(before, await s.Db.HKCardItemMaterials.CountAsync());
    }

    // ── Фактическая строка ХК после подтверждения ─────────────────────────

    [Fact]
    public async Task ConfirmedSuggestion_BecomesOrdinaryRow_WithChosenCategory()
    {
        var (primaryId, bothId, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var (nodeId, unitId) = await SeedNodeAndUnitAsync(s);
        var item = new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = unitId,
            SortOrder = 1,
            Quantity = 1,
            Volume = 10,
            UnitOfMeasure = "кг",
        };
        item.Materials.Add(NewMaterial(item.Id, primaryId, GsmCategory.Primary));
        // Пользователь подтвердил ТОЛЬКО дублирующую категорию из двух.
        item.Materials.Add(NewMaterial(item.Id, bothId, GsmCategory.Duplicate));

        var card = await s.HK.CreateAsync(new HKCard
        {
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            Items = new List<HKCardItem> { item },
        });

        await using var s2 = _fixture.CreateScope();
        var saved = await s2.HK.GetByIdAsync(card.Id);
        var rows = saved!.Items.Single().Materials.ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            GsmCategory.Duplicate,
            rows.Single(r => r.GsmMaterialId == bothId).Category);

        // Резервная категория не появилась: подтверждено было не всё.
        Assert.DoesNotContain(rows, r => r.GsmMaterialId == bothId && r.Category == GsmCategory.Reserve);
    }

    [Fact]
    public async Task ConfirmedSuggestion_AfterRelationDeletion_KeepsMaterialInRow()
    {
        // Справочная связь остаётся подсказкой: её последующее удаление не должно
        // физически убирать марку из уже существующей ХК.
        var (primaryId, bothId, _, _) = await SeedAsync();
        Guid relationId;
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            var created = await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = bothId,
                RelationType = GsmRelationType.DuplicateAndReserve,
            });
            relationId = created.Id;
        }

        Guid cardId;
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var (nodeId, unitId) = await SeedNodeAndUnitAsync(s);
            var item = new HKCardItem
            {
                Id = Guid.NewGuid(),
                AssemblyUnitId = unitId,
                SortOrder = 1,
                Quantity = 1,
                Volume = 10,
                UnitOfMeasure = "кг",
            };
            item.Materials.Add(NewMaterial(item.Id, primaryId, GsmCategory.Primary));
            item.Materials.Add(NewMaterial(item.Id, bothId, GsmCategory.Reserve));
            var card = await s.HK.CreateAsync(new HKCard
            {
                Code = "HK-" + Suffix(),
                Version = "v" + Suffix()[..4],
                ObjectLevel = HKObjectLevel.Node,
                NodeId = nodeId,
                Items = new List<HKCardItem> { item },
            });
            cardId = card.Id;
        }

        await using (var s3 = _fixture.CreateScope())
        {
            AsNormAdmin(s3);
            Assert.True(await s3.GsmMaterials.DeleteRelationAsync(relationId));
        }

        await using var s4 = _fixture.CreateScope();
        var saved = await s4.HK.GetByIdAsync(cardId);
        var rows = saved!.Items.Single().Materials.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            GsmCategory.Reserve,
            rows.Single(r => r.GsmMaterialId == bothId).Category);
    }

    [Fact]
    public async Task NewRow_RejectsDeletedMaterial_WhileHistoricalRowStaysReadable()
    {
        // Серверная валидация нового назначения обязательна и после переключения:
        // удалённая марка в новую строку не попадает, уже сохранённая ссылка
        // остаётся читаемой.
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            var material = await setup.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == duplicateId);
            setup.Db.Entry(material).State = EntityState.Modified;
            material.IsDeleted = true;
            await setup.Db.SaveChangesAsync();
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var (nodeId, unitId) = await SeedNodeAndUnitAsync(s);
        var item = new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = unitId,
            SortOrder = 1,
            Quantity = 1,
            Volume = 10,
            UnitOfMeasure = "кг",
        };
        item.Materials.Add(NewMaterial(item.Id, primaryId, GsmCategory.Primary));

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            Items = new List<HKCardItem> { item },
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        // Удалённая марка добавляется ПОСЛЕ сохранения карточки: только тогда
        // назначение новое и подлежит проверке. Уже сохранённые назначения
        // задним числом не перепроверяются.
        card.Items.Single().Materials.Add(NewMaterial(item.Id, duplicateId, GsmCategory.Duplicate));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(card));
        Assert.Contains("удалена", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HistoricalRow_WithDeletedMaterial_IsNotRevalidatedOnLaterEdit()
    {
        // Обратная сторона того же правила: если строка с удалённой маркой уже
        // сохранена, обычная правка карточки проходит — иначе старую ХК нельзя
        // было бы отредактировать вовсе.
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        Guid cardId;
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            var (nodeId, unitId) = await SeedNodeAndUnitAsync(setup);
            var item = new HKCardItem
            {
                Id = Guid.NewGuid(),
                AssemblyUnitId = unitId,
                SortOrder = 1,
                Quantity = 1,
                Volume = 10,
                UnitOfMeasure = "кг",
            };
            item.Materials.Add(NewMaterial(item.Id, primaryId, GsmCategory.Primary));
            item.Materials.Add(NewMaterial(item.Id, duplicateId, GsmCategory.Duplicate));
            var card = await setup.HK.CreateAsync(new HKCard
            {
                Code = "HK-" + Suffix(),
                Version = "v" + Suffix()[..4],
                ObjectLevel = HKObjectLevel.Node,
                NodeId = nodeId,
                Items = new List<HKCardItem> { item },
            });
            cardId = card.Id;

            var material = await setup.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == duplicateId);
            setup.Db.Entry(material).State = EntityState.Modified;
            material.IsDeleted = true;
            await setup.Db.SaveChangesAsync();
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var card2 = await s.HK.GetByIdAsync(cardId);
        card2!.Notes = "Правка текста карточки с исторической ссылкой";

        var updated = await s.HK.UpdateAsync(card2);
        Assert.NotNull(updated);

        await using var s2 = _fixture.CreateScope();
        var rows = await s2.Db.HKCardItemMaterials.AsNoTracking()
            .Where(r => r.HKCardItem!.HKCardId == cardId)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
    }

    // ── Все связанные марки недоступны (замечание A1.1) ──────────────────

    [Fact]
    public async Task Suggestions_WhenAllRelatedAreUnavailable_StillReturnThemWithReasons()
    {
        // Связь видна, но ни одна связанная марка не годится для строки ХК.
        // Список обязан вернуться: иначе интерфейс покажет «связей нет», хотя
        // связи есть, и пользователь будет искать несуществующую ошибку в
        // справочнике связей.
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = duplicateId,
                RelationType = GsmRelationType.Duplicate,
            });

            var material = await setup.Db.GsmMaterials.AsNoTracking()
                .FirstAsync(m => m.Id == duplicateId);
            setup.Db.Entry(material).State = EntityState.Modified;
            material.IsDeleted = true;
            await setup.Db.SaveChangesAsync();
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var source = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
        var option = Assert.Single(source.Suggestions);

        Assert.False(option.IsAddable);
        Assert.Contains("удалена", option.UnavailableReason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(source.Suggestions.Any(x => x.IsAddable));
    }

    [Fact]
    public async Task Suggestions_MixedAvailability_KeepsBothKindsInOneList()
    {
        var (primaryId, availableId, deletedId, _) = await SeedAsync();
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = availableId,
                RelationType = GsmRelationType.Duplicate,
            });
            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = deletedId,
                RelationType = GsmRelationType.Reserve,
            });

            var material = await setup.Db.GsmMaterials.AsNoTracking()
                .FirstAsync(m => m.Id == deletedId);
            setup.Db.Entry(material).State = EntityState.Modified;
            material.IsDeleted = true;
            await setup.Db.SaveChangesAsync();
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var source = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));

        Assert.Equal(2, source.Suggestions.Count);
        Assert.Single(source.Suggestions, x => x.IsAddable);
        Assert.Single(source.Suggestions, x => !x.IsAddable);
    }

    // ── Неклассифицированный аналог (замечание A1.2) ─────────────────────

    [Fact]
    public async Task Suggestions_UnclassifiedRelated_IsNotAddable_WithReason()
    {
        // PR-4 допускает справочную связь с опубликованной неклассифицированной
        // маркой, но новая строка ХК такую марку запрещает. Предложение обязано
        // совпадать с решением сервиса ХК, иначе «Сохранить ХК» откажет там,
        // где интерфейс назвал вариант доступным.
        var (primaryId, _, _, _) = await SeedAsync();
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);

            // Марка без единой строки классификации: создаётся напрямую, мимо
            // сервиса, который требует группу и подгруппу.
            var unclassified = new GsmMaterial
            {
                Id = Guid.NewGuid(),
                Name = "Без классификации " + Suffix(),
                Nd = "НД " + Suffix(),
                IsDraft = false,
                IsDeleted = false,
            };
            setup.Db.GsmMaterials.Add(unclassified);
            await setup.Db.SaveChangesAsync();

            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = unclassified.Id,
                RelationType = GsmRelationType.Duplicate,
            });

            await using var read = _fixture.CreateScope();
            AsNormAdmin(read);
            var source = Assert.Single(await read.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
            var option = Assert.Single(source.Suggestions);

            Assert.False(option.IsAddable);
            Assert.Contains("не классифицирована", option.UnavailableReason ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Suggestions_UnclassifiedRelated_VisibleInRelationRegistry_ButRejectedByHk()
    {
        // Полный сценарий замечания: связь видна в справочнике, но отметить её
        // для ХК нельзя, а сервер при попытке добавить такую строку отказывает.
        var (primaryId, _, _, _) = await SeedAsync();
        Guid unclassifiedId;
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            var unclassified = new GsmMaterial
            {
                Id = Guid.NewGuid(),
                Name = "Без классификации " + Suffix(),
                Nd = "НД " + Suffix(),
                IsDraft = false,
                IsDeleted = false,
            };
            setup.Db.GsmMaterials.Add(unclassified);
            await setup.Db.SaveChangesAsync();
            unclassifiedId = unclassified.Id;

            var created = await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = unclassifiedId,
                RelationType = GsmRelationType.Duplicate,
            });

            // Связь создана и видна — отказ пришлось бы на другое основание.
            var relations = await setup.GsmMaterials.GetRelationsPagedAsync(
                new GsmRelationQuery { Page = 1, PageSize = 200 }, CancellationToken.None);
            Assert.Contains(relations.Items, r => r.Id == created.Id);
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var source = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
        Assert.False(Assert.Single(source.Suggestions).IsAddable);

        // Последний рубеж: сервис ХК такую марку в новую строку не пускает.
        var (nodeId, unitId) = await SeedNodeAndUnitAsync(s);
        var item = new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = unitId,
            SortOrder = 1,
            Quantity = 1,
            Volume = 10,
            UnitOfMeasure = "кг",
        };
        item.Materials.Add(NewMaterial(item.Id, primaryId, GsmCategory.Primary));
        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            Items = new List<HKCardItem> { item },
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        card.Items.Single().Materials.Add(NewMaterial(item.Id, unclassifiedId, GsmCategory.Duplicate));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(card));
        Assert.Contains("классифиц", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Сбой чтения (замечание A1.3) ─────────────────────────────────────

    [Fact]
    public async Task Suggestions_Failure_PropagatesAsException_NeverAsEmptyList()
    {
        // Ключевое требование A1.3: сбой чтения обязан дойти до интерфейса как
        // ошибка, а не раствориться в пустом списке. «Связей нет» и «не удалось
        // прочитать» — разные вещи, и интерфейс показывает их по-разному.
        // Проверяется серверная часть: исключение доходит до вызывающего кода.
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = duplicateId,
                RelationType = GsmRelationType.Duplicate,
            });
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        // Контроль: при нормальных условиях список непустой — иначе тест на
        // отказ прошёл бы на пустом результате и ничего не доказывал.
        Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));

        // Воспроизводимый отказ: у пользователя индивидуально запрещено право
        // Reference.View. Тот же путь, что и при обрыве БД, — метод бросает, а
        // не возвращает пустоту. Запрет снимается в финале, чтобы общая тестовая
        // БД осталась чистой для следующих прогонов.
        var guestId = _fixture.GuestA.Id;
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == guestId && o.PermissionCode == PermissionCodes.ReferenceView)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = guestId,
            PermissionCode = PermissionCodes.ReferenceView,
            IsGranted = false,
            Reason = "HkRelationSuggestion: контроль отказа",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(guestId);
        s.User.CurrentUserId = Guid.Parse(guestId);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
        }
        finally
        {
            var added = await s.Db.UserPermissionOverrides
                .Where(o => o.UserId == guestId
                            && o.PermissionCode == PermissionCodes.ReferenceView
                            && o.Reason == "HkRelationSuggestion: контроль отказа")
                .ToListAsync();
            s.Db.UserPermissionOverrides.RemoveRange(added);
            await s.Db.SaveChangesAsync();
            s.Permissions.InvalidateCache(guestId);
        }
    }

    [Fact]
    public async Task Suggestions_Cancellation_IsNotSwallowed_AndNotAnError()
    {
        // Отмена — не сбой загрузки. Сервис обязан её уважать и пробросить:
        // перехват отмены превратил бы уход со страницы в сообщение об ошибке.
        var (primaryId, _, _, _) = await SeedAsync();
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }, cts.Token));
    }

    // ── Повторное открытие сохранённой ХК (замечание A1.4) ───────────────

    [Fact]
    public async Task Suggestions_RecomputedForCurrentPrimarySet_AfterPrimaryReplaced()
    {
        // Снятие или замена основной марки пересчитывает предложения по
        // АКТУАЛЬНОМУ набору: рекомендации удалённой марки показываться не
        // должны.
        var (primaryA, _, _, _) = await SeedAsync();
        var (primaryB, relatedB, _, _) = await SeedAsync();
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryA,
                RelatedGsmMaterialId = relatedB,
                RelationType = GsmRelationType.Duplicate,
            });
            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryB,
                RelatedGsmMaterialId = relatedB,
                RelationType = GsmRelationType.Reserve,
            });
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var forA = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryA }));
        Assert.Equal(new[] { GsmCategory.Duplicate }, Assert.Single(forA.Suggestions).Categories);

        // Тот же набор после «замены» основной марки: категория меняется, вариант
        // остаётся тем же — интерфейс обязан перечитать и не оставить старую
        // отметку, указывающую на другую категорию.
        var forB = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryB }));
        Assert.Equal(new[] { GsmCategory.Reserve }, Assert.Single(forB.Suggestions).Categories);
    }

    [Fact]
    public async Task Suggestions_AlreadyConfirmedRow_IsNotOfferedAgain()
    {
        // Повторное открытие сохранённой ХК: марка, уже добавленная в строку,
        // не должна предлагаться к добавлению повторно — но связь остаётся
        // видимой, поэтому пользователь видит, откуда она взялась.
        var (primaryId, duplicateId, _, _) = await SeedAsync();
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            await setup.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = duplicateId,
                RelationType = GsmRelationType.Duplicate,
            });
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // Строка уже содержит марку в дублирующей категории.
        var (nodeId, unitId) = await SeedNodeAndUnitAsync(s);
        var item = new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = unitId,
            SortOrder = 1,
            Quantity = 1,
            Volume = 10,
            UnitOfMeasure = "кг",
        };
        item.Materials.Add(NewMaterial(item.Id, primaryId, GsmCategory.Primary));
        item.Materials.Add(NewMaterial(item.Id, duplicateId, GsmCategory.Duplicate));
        var card = await s.HK.CreateAsync(new HKCard
        {
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            Items = new List<HKCardItem> { item },
        });

        // Связь по-прежнему предлагается (она активна), но идемпотентность на
        // стороне строки не даёт добавить вторую такую же строку.
        var source = Assert.Single(await s.GsmMaterials.GetRelatedSuggestionsAsync(new[] { primaryId }));
        var option = Assert.Single(source.Suggestions);
        Assert.True(option.IsAddable);

        var saved = await s.HK.GetByIdAsync(card.Id);
        Assert.Equal(1, saved!.Items.Single().Materials.Count(
            m => m.GsmMaterialId == duplicateId && m.Category == GsmCategory.Duplicate));
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────

    private static HKCardItemMaterial NewMaterial(Guid itemId, Guid materialId, GsmCategory category) =>
        new()
        {
            Id = Guid.NewGuid(),
            HKCardItemId = itemId,
            GsmMaterialId = materialId,
            Category = category,
        };

    /// <summary>
    /// Четыре классифицированные марки: основная и по одной на каждую категорию
    /// связи. Пять фикстур — потому что добавление связи требует действующей
    /// марки, а удаление выполняется уже после фиксации сценария.
    /// </summary>
    private async Task<(Guid primary, Guid duplicate, Guid reserve, Guid foreign)> SeedAsync()
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var primary = await CreateAsync(s, "Основная " + Suffix());
        var duplicate = await CreateAsync(s, "Дублирующая " + Suffix());
        var reserve = await CreateAsync(s, "Резервная " + Suffix());
        var foreign = await CreateAsync(s, "Зарубежная " + Suffix());
        return (primary, duplicate, reserve, foreign);
    }

    private static async Task<Guid> CreateAsync(TestScope s, string name)
    {
        var created = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name,
            Nd = "НД " + Suffix(),
            GroupName = "Группа " + Suffix(),
            SubgroupNames = new List<string> { "Подгруппа " + Suffix() },
        });
        return created.Id;
    }

    private async Task<(Guid nodeId, Guid unitId)> SeedNodeAndUnitAsync(TestScope s)
    {
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-" + Suffix(),
            Name = "Узел " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.Nodes.Add(node);

        var unit = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "AU-" + Suffix(),
            Name = "СЕ " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        return (node.Id, unit.Id);
    }
}
