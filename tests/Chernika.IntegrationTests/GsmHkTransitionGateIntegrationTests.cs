using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Граница классификации для новых строк ХК.
/// <para>
/// После PR-5 требование сформулировано как «хотя бы одна подгруппа»: несколько
/// подгрупп больше не блокируют новое назначение, потому что действующие
/// потребители переключены на <c>Nd</c> и классификацию, а прежний переходный
/// запрет «ровно одна подгруппа» (вариант A §4.3) снят.
/// </para>
/// <para>
/// Марка без классификации по-прежнему недопустима для новой строки: группа из
/// прежнего <c>Type</c> не выдумывается. Проверки серверные; исторические ссылки
/// не блокируют несвязанную правку карточки и не перепроверяются.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmHkTransitionGateIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmHkTransitionGateIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── 1. Создание ХК: требуется ровно одна подгруппа ─────────────────────

    [Fact]
    public async Task HkCreate_WithUnclassifiedMaterial_IsRejected()
    {
        // Неклассифицированная марка (0 подгрупп) не имеет представления в
        // Nd/классификации — новая ссылка на неё запрещена.
        var materialId = await CreateLegacyMaterialAsync("Без классификации " + Suffix());
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.CreateAsync(BuildCard(nodeId, unitId, materialId)));

        Assert.Contains("не классифицирована", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HkCreate_WithSingleSubgroupMaterial_IsAccepted()
    {
        var materialId = await CreateMaterialAsync("Одна подгруппа " + Suffix(), new List<string> { "Подгруппа" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var created = await s.HK.CreateAsync(BuildCard(nodeId, unitId, materialId));
        Assert.NotNull(created);
        Assert.True(await s.Db.HKCardItemMaterials.AnyAsync(r => r.GsmMaterialId == materialId));
    }

    [Fact]
    public async Task HkCreate_WithMultiSubgroupMaterial_IsAccepted()
    {
        // PR-5: переходный запрет «ровно одна подгруппа» снят. Классифицированная
        // марка с несколькими подгруппами — обычное назначение в новую строку ХК.
        var materialId = await CreateMaterialAsync("Две подгруппы " + Suffix(), new List<string> { "Альфа", "Бета" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var created = await s.HK.CreateAsync(BuildCard(nodeId, unitId, materialId));

        Assert.NotNull(created);
        Assert.True(await s.Db.HKCardItemMaterials.AnyAsync(r => r.GsmMaterialId == materialId));
    }

    // ── 2. Обновление: новое назначение проверяется, история — нет ─────────

    [Fact]
    public async Task HkUpdate_AddingUnclassifiedMaterial_IsRejectedBeforeMutations()
    {
        var keptId = await CreateMaterialAsync("Остаётся " + Suffix(), new List<string> { "Подгруппа" });
        var legacyId = await CreateLegacyMaterialAsync("Новая без классификации " + Suffix());
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, keptId);

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            var item = card!.Items.Single();
            item.Materials.Add(new HKCardItemMaterial
            {
                Id = Guid.NewGuid(),
                HKCardItemId = item.Id,
                GsmMaterialId = legacyId,
                Category = GsmCategory.Primary,
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(card));
        }

        // Отказ до мутаций: новая ссылка не появилась, прежняя не пострадала.
        await using (var s2 = _fixture.CreateScope())
        {
            var rows = await s2.Db.HKCardItemMaterials.AsNoTracking()
                .Where(m => m.GsmMaterialId == keptId || m.GsmMaterialId == legacyId)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal(keptId, row.GsmMaterialId);
        }
    }

    [Fact]
    public async Task HkUpdate_TextOnlyEditOfHistoricalLink_SurvivesLaterReclassification()
    {
        // Марка была в ХК с одной подгруппой; позже её классифицировали иначе
        // (это отдельная операция над маркой). Правка текста карточки не должна
        // быть заблокирована из-за исторической ссылки.
        var materialId = await CreateMaterialAsync("Историческая " + Suffix(), new List<string> { "Альфа" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, materialId);

        await AddSecondSubgroupAsync(materialId, "Бета");

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            card!.Notes = "Правка текста при изменённой классификации";

            var updated = await s.HK.UpdateAsync(card);
            Assert.NotNull(updated);
        }

        await using (var s2 = _fixture.CreateScope())
        {
            AsNormAdmin(s2);
            var saved = await s2.HK.GetByIdAsync(cardId);
            Assert.Equal("Правка текста при изменённой классификации", saved!.Notes);
        }
    }

    [Fact]
    public async Task HkUpdate_AddingHistoricalUnclassifiedLink_IsRejected()
    {
        // Та же марка без классификации, но добавляемая как НОВАЯ ссылка —
        // запрещена независимо от того, что где-то она уже встречалась.
        var legacyId = await CreateLegacyMaterialAsync("Новая ссылка " + Suffix());
        var keptId = await CreateMaterialAsync("Прежняя " + Suffix(), new List<string> { "Подгруппа" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, keptId);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var card = await s.HK.GetByIdAsync(cardId);
        var item = card!.Items.Single();
        item.Materials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = item.Id,
            GsmMaterialId = legacyId,
            Category = GsmCategory.Primary,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(card));
    }

    // ── 3. Список выбора не предлагает недопустимые марки ──────────────────

    [Fact]
    public async Task SelectionList_ExcludesUnclassified_AndKeepsAnySubgroupCount()
    {
        var singleId = await CreateMaterialAsync("Доступная " + Suffix(), new List<string> { "Подгруппа" });
        var multiId = await CreateMaterialAsync("Доступная много " + Suffix(), new List<string> { "Альфа", "Бета" });
        var legacyId = await CreateLegacyMaterialAsync("Недоступная без класса " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var list = await s.GsmMaterials.GetActiveForSelectionAsync();
        var ids = list.Select(m => m.Id).ToHashSet();

        Assert.Contains(singleId, ids);
        // PR-5: несколько подгрупп больше не мешают выбору.
        Assert.Contains(multiId, ids);
        Assert.DoesNotContain(legacyId, ids);

        // Классификация подгружена: выпадающий список ХК группирует по ней.
        var multi = list.First(m => m.Id == multiId);
        Assert.Equal(2, multi.Classifications.Count);
    }

    [Fact]
    public async Task SelectionList_StillContainsMaterialWhenSearchNarrowsToExcluded()
    {
        // Список не должен ломать отображение уже сохранённых марок: поиск по
        // прежним полям продолжает работать для доступных марок.
        var singleId = await CreateMaterialAsync("Поисковая " + Suffix(), new List<string> { "Подгруппа" });

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var list = await s.GsmMaterials.GetActiveForSelectionAsync("Поисковая");
        Assert.Contains(singleId, list.Select(m => m.Id));
    }

    // ── 4. Классификация используемой в ХК марки больше не ограничена ──────

    [Fact]
    public async Task GsmUpdate_AddingSecondSubgroupToHkUsedMaterial_IsAllowed()
    {
        // PR-5: переходное ограничение снято. Несколько подгрупп у марки, уже
        // используемой в ХК, больше не мешают — действующие потребители читают
        // Nd и классификацию, а не прежний Type.
        var materialId = await CreateMaterialAsync("Используемая " + Suffix(), new List<string> { "Альфа" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        await CreateCardAsync(nodeId, unitId, materialId);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var view = await s.GsmMaterials.UpdateAsync(materialId, new GsmMaterialWriteRequest
        {
            Name = "Используемая " + Suffix(),
            GroupName = "Группа",
            SubgroupNames = new List<string> { "Альфа", "Бета" },
        });

        Assert.NotNull(view);
        Assert.Equal(2, view!.SubgroupNames.Count);

        // Строка ХК при этом не меняется: Id материала и категория прежние.
        await using var s2 = _fixture.CreateScope();
        var rows = await s2.Db.HKCardItemMaterials.AsNoTracking()
            .Where(c => c.GsmMaterialId == materialId).ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task GsmUpdate_AddingSecondSubgroupToUnusedMaterial_IsAllowed()
    {
        var materialId = await CreateMaterialAsync("Свободная " + Suffix(), new List<string> { "Альфа" });

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var view = await s.GsmMaterials.UpdateAsync(materialId, new GsmMaterialWriteRequest
        {
            Name = "Свободная " + Suffix(),
            GroupName = "Группа",
            SubgroupNames = new List<string> { "Альфа", "Бета" },
        });

        Assert.NotNull(view);
        Assert.Equal(2, view!.SubgroupNames.Count);
    }

    [Fact]
    public async Task GsmUpdate_RenamingHkUsedSingleSubgroupMaterial_IsAllowed()
    {
        // Название марки, НД и назначение правилу не подчинены: правка обычных
        // полей используемой марки должна проходить.
        var materialId = await CreateMaterialAsync("Правка " + Suffix(), new List<string> { "Альфа" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        await CreateCardAsync(nodeId, unitId, materialId);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var view = await s.GsmMaterials.UpdateAsync(materialId, new GsmMaterialWriteRequest
        {
            Name = "Правка переименована " + Suffix(),
            Nd = "ГОСТ 12345-2020",
            IntendedUse = "Уточнённое назначение",
            GroupName = "Группа",
            SubgroupNames = new List<string> { "Альфа" },
        });

        Assert.NotNull(view);
        Assert.Single(view!.SubgroupNames);
    }

    [Fact]
    public async Task NameLookup_ReturnsNamesForLegacyMaterialsExcludedFromSelection()
    {
        // Марка, уже сохранённая в ХК, может быть исключена из выбора новым
        // фильтром, но в существующей строке ХК обязана оставаться читаемой:
        // справочный просмотр не зависит от переходного ограничения.
        var legacyId = await CreateLegacyMaterialAsync("Историческая " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var names = await s.GsmMaterials.GetNamesAsync(new[] { legacyId, Guid.NewGuid() });

        Assert.True(names.ContainsKey(legacyId));
        Assert.False(names.ContainsKey(Guid.NewGuid()));
        Assert.StartsWith("Историческая ", names[legacyId], StringComparison.Ordinal);
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────

    private async Task<Guid> CreateMaterialAsync(string name, List<string> subgroups)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name,
            GroupName = "Группа " + Suffix(),
            SubgroupNames = subgroups,
        });
        return view.Id;
    }

    /// <summary>Марка без классификации: состояние до перехода на новую модель.</summary>
    private async Task<Guid> CreateLegacyMaterialAsync(string name)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name,
            GroupName = "Группа " + Suffix(),
            SubgroupNames = new List<string> { "Подгруппа " + Suffix() },
        });

        var rows = await s.Db.GsmMaterialClassifications.Where(c => c.GsmMaterialId == view.Id).ToListAsync();
        s.Db.GsmMaterialClassifications.RemoveRange(rows);
        await s.Db.SaveChangesAsync();
        return view.Id;
    }

    private async Task AddSecondSubgroupAsync(Guid materialId, string subgroup)
    {
        await using var s = _fixture.CreateScope();
        var group = await s.Db.GsmMaterialClassifications
            .Where(c => c.GsmMaterialId == materialId)
            .Select(c => c.GroupName)
            .FirstAsync();

        s.Db.GsmMaterialClassifications.Add(new GsmMaterialClassification
        {
            Id = Guid.NewGuid(),
            GsmMaterialId = materialId,
            GroupName = group,
            SubgroupName = subgroup,
        });
        await s.Db.SaveChangesAsync();
    }

    private async Task<(Guid NodeId, Guid UnitId)> CreateNodeAndUnitAsync()
    {
        await using var s = _fixture.CreateScope();
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix() };
        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix() };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        return (node.Id, unit.Id);
    }

    private async Task<Guid> CreateCardAsync(Guid nodeId, Guid unitId, params Guid[] materialIds)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var created = await s.HK.CreateAsync(BuildCard(nodeId, unitId, materialIds));
        return created.Id;
    }

    private static HKCard BuildCard(Guid nodeId, Guid unitId, params Guid[] materialIds) => new()
    {
        ObjectLevel = HKObjectLevel.Node,
        NodeId = nodeId,
        Purpose = "Проверка переходного gate",
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
