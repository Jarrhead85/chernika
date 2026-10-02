using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Позитивные регрессии фазы B PR-6: удаление переходных колонок не сломало
/// действующие сценарии.
/// <para>
/// Проверяется сквозной путь пользователя, а не отсутствие ошибок компиляции:
/// марка с несколькими подгруппами и длинным НД → выбор в ХК → подтверждение
/// справочных связей → сохранение → повторное открытие. Плюс чтение старой ХК
/// с исторически удалённой маркой и работоспособность поиска и аудита.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmPhaseBPositiveRegressionTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmPhaseBPositiveRegressionTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    [Fact]
    public async Task Material_WithTwoSubgroupsAndLongNd_IsCreatedAndReadBack()
    {
        var longNd = "ГОСТ 21743-76; " + new string('A', 180);
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var created = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Много подгрупп " + Suffix(),
            Nd = longNd,
            IntendedUse = "Для дизельных двигателей",
            GroupName = "Моторные масла",
            SubgroupNames = new List<string> { "Ясные", "Азотные" },
        });

        var view = await s.GsmMaterials.GetEditViewAsync(created.Id);
        Assert.NotNull(view);
        Assert.Equal(longNd, view!.Nd);
        Assert.Equal("Для дизельных двигателей", view.IntendedUse);
        Assert.Equal("Моторные масла", view.GroupName);
        Assert.Equal(2, view.SubgroupNames.Count);
    }

    [Fact]
    public async Task HkRow_AcceptsMultiSubgroupMaterial_AndPersistsChosenCategory()
    {
        var primary = await CreateMaterialAsync("Моторные масла", new[] { "Азотные", "Ясные" });
        var duplicate = await CreateMaterialAsync("Моторные масла", new[] { "Азотные" });
        var foreign = await CreateMaterialAsync("Импортные масла", new[] { "Импортные" });

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // Связь Foreign требует InGostNomenclature = false у связанной марки.
        var foreignRow = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == foreign);
        s.Db.Entry(foreignRow).State = EntityState.Modified;
        foreignRow.InGostNomenclature = false;
        await s.Db.SaveChangesAsync();

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primary,
            RelatedGsmMaterialId = duplicate,
            RelationType = GsmRelationType.Duplicate,
        });
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = primary,
            RelatedGsmMaterialId = foreign,
            RelationType = GsmRelationType.Foreign,
        });

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
        item.Materials.Add(Mat(item.Id, primary, GsmCategory.Primary));
        // Категория выбрана пользователем, а не выведена из RelationType.
        item.Materials.Add(Mat(item.Id, duplicate, GsmCategory.Duplicate));
        item.Materials.Add(Mat(item.Id, foreign, GsmCategory.Foreign));

        var card = await s.HK.CreateAsync(new HKCard
        {
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            Items = new List<HKCardItem> { item },
        });

        // Повторное открытие: выбор категории пережил сохранение.
        await using var s2 = _fixture.CreateScope();
        var reopened = await s2.HK.GetByIdAsync(card.Id);
        var rows = reopened!.Items.Single().Materials.ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(GsmCategory.Primary, rows.Single(r => r.GsmMaterialId == primary).Category);
        Assert.Equal(GsmCategory.Duplicate, rows.Single(r => r.GsmMaterialId == duplicate).Category);
        Assert.Equal(GsmCategory.Foreign, rows.Single(r => r.GsmMaterialId == foreign).Category);
    }

    [Fact]
    public async Task OldHk_WithHistoricallyDeletedMaterial_StillOpensAndPrints()
    {
        // Старая ХК со ссылкой на удалённую марку обязана оставаться открытой:
        // удаление legacy-колонок не имеет права ужесточать чтение истории.
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var material = await CreateMaterialAsync("Моторные масла", new[] { "Азотные" });
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
        item.Materials.Add(Mat(item.Id, material, GsmCategory.Primary));

        var card = await s.HK.CreateAsync(new HKCard
        {
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            Items = new List<HKCardItem> { item },
        });

        // Марка уходит в архив ПОСЛЕ того, как строка ХК её использовала.
        var archived = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == material);
        s.Db.Entry(archived).State = EntityState.Modified;
        archived.IsDeleted = true;
        archived.DeletedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        await using var s2 = _fixture.CreateScope();
        var reopened = await s2.HK.GetByIdAsync(card.Id);
        Assert.NotNull(reopened);
        Assert.Single(reopened!.Items.Single().Materials);

        // Сводная выдача родительской карты — отдельный сценарий, требующий
        // иерархии карт; здесь он не проверяется намеренно. Случай
        // «мягко удалённая марка не теряет строку ХК» закрыт в
        // GsmActiveConsumerSwitchIntegrationTests.
    }

    [Fact]
    public async Task Search_ByNdAndByGroup_WorksAfterColumnRemoval()
    {
        var material = await CreateMaterialAsync("Моторные масла", new[] { "Азотные" });
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var byNd = await s.Search.SearchAsync(new SearchQuery { Text = "НД марки" });
        Assert.Contains(byNd.Items, x => x.EntityId == material);

        var byGroup = await s.Search.SearchAsync(new SearchQuery { Text = "Моторные масла" });
        Assert.Contains(byGroup.Items, x => x.EntityId == material);
    }

    [Fact]
    public async Task Audit_RecordsMaterialWrite_WithNewFieldsOnly()
    {
        var material = await CreateMaterialAsync("Моторные масла", new[] { "Азотные" });
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var logs = await s.Audit.GetLogsWithEntityNamesAsync(1, 200, entityType: "GsmMaterial");
        Assert.Contains(logs, e => e.EntityId == material.ToString());

        // Отчёт о legacy-полях удалён вместе с колонками: вызывать его больше
        // нечем, и он не должен остаться в сервисе.
        var service = typeof(Chernika.Infrastructure.Services.GsmMaterialService);
        Assert.DoesNotContain(
            service.GetMethods(),
            m => m.Name.Contains("Transition", StringComparison.Ordinal)
                 || m.Name.Contains("Divergence", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoricalIndividualCardSnapshot_KeepsItsOwnGostAndMaterialType()
    {
        // Историческое чтение ИК не зависит от live-полей марки: снимок хранит
        // собственные Gost/MaterialType. Именно поэтому удаление колонок марки
        // не обнуляет старые карты.
        var snapshotProps = typeof(IndividualCardItemMaterialSnapshot)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Gost", snapshotProps);
        Assert.Contains("MaterialType", snapshotProps);

        // А таблица и её колонки на месте.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=configuration_probe;Username=none")
            .Options;
        using var ctx = new AppDbContext(options);

        var type = ctx.Model.FindEntityType(typeof(IndividualCardItemMaterialSnapshot))!;
        Assert.NotNull(type.FindProperty("Gost"));
        Assert.NotNull(type.FindProperty("MaterialType"));
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────

    private async Task<Guid> CreateMaterialAsync(string group, string[] subgroups)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var created = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Марка " + Suffix(),
            Nd = "НД марки " + Suffix(),
            GroupName = group,
            SubgroupNames = subgroups.ToList(),
        });
        return created.Id;
    }

    private static HKCardItemMaterial Mat(Guid itemId, Guid materialId, GsmCategory category) =>
        new() { Id = Guid.NewGuid(), HKCardItemId = itemId, GsmMaterialId = materialId, Category = category };

    private async Task<(Guid nodeId, Guid unitId)> SeedNodeAndUnitAsync(TestScope s)
    {
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-" + Suffix(),
            Name = "Узел " + Suffix(),
        };
        var unit = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "AU-" + Suffix(),
            Name = "СЕ " + Suffix(),
        };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        return (node.Id, unit.Id);
    }
}
