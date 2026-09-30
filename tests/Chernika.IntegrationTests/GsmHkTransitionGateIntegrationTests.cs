using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// A2 — переходное ограничение варианта A §4.3: пока legacy-потребители читают
/// прежний <c>Type</c>, новая строка ХК допускает только марку с РОВНО ОДНОЙ
/// подгруппой. Проверки серверные; исторические ссылки при этом не должны
/// блокировать несвязанную правку карточки.
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
        // прежнем Type — новая ссылка на неё запрещена.
        var materialId = await CreateLegacyMaterialAsync("Без классификации " + Suffix());
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.CreateAsync(BuildCard(nodeId, unitId, materialId)));

        Assert.Contains("подгрупп", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task HkCreate_WithMultiSubgroupMaterial_IsRejected()
    {
        var materialId = await CreateMaterialAsync("Две подгруппы " + Suffix(), new List<string> { "Альфа", "Бета" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.CreateAsync(BuildCard(nodeId, unitId, materialId)));

        Assert.Contains("подгрупп", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task SelectionList_ExcludesZeroAndMultipleSubgroupMaterials()
    {
        var singleId = await CreateMaterialAsync("Доступная " + Suffix(), new List<string> { "Подгруппа" });
        var multiId = await CreateMaterialAsync("Недоступная много " + Suffix(), new List<string> { "Альфа", "Бета" });
        var legacyId = await CreateLegacyMaterialAsync("Недоступная без класса " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var list = await s.GsmMaterials.GetActiveForSelectionAsync();
        var ids = list.Select(m => m.Id).ToHashSet();

        Assert.Contains(singleId, ids);
        Assert.DoesNotContain(multiId, ids);
        Assert.DoesNotContain(legacyId, ids);
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

    // ── 4. Создание второй подгруппы у используемой марки запрещено ────────

    [Fact]
    public async Task GsmUpdate_AddingSecondSubgroupToHkUsedMaterial_IsRejected()
    {
        var materialId = await CreateMaterialAsync("Используемая " + Suffix(), new List<string> { "Альфа" });
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        await CreateCardAsync(nodeId, unitId, materialId);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateAsync(materialId, new GsmMaterialWriteRequest
            {
                Name = "Используемая " + Suffix(),
                GroupName = "Группа",
                SubgroupNames = new List<string> { "Альфа", "Бета" },
            }));

        Assert.Contains("подгрупп", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Прежний набор не тронут.
        await using var s2 = _fixture.CreateScope();
        var rows = await s2.Db.GsmMaterialClassifications.AsNoTracking()
            .Where(c => c.GsmMaterialId == materialId).ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task GsmUpdate_AddingSecondSubgroupToUnusedMaterial_IsAllowed()
    {
        // Запрет распространяется только на реально используемые марки: у свободной
        // марки несколько подгрупп допустимы (переходное правило ограничивает
        // новые ссылки в ХК, а не саму классификацию).
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
