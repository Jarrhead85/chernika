using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Чтение полного рабочего графа редактором и устойчивость снимков при отказе.
/// <para>
/// Чтение проверяется в НОВОМ scope: старый ChangeTracker способен скрыть
/// отсутствие Include, потому что сущности уже в памяти.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardStructureGraphIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardStructureGraphIntegrationTests(TestDatabaseFixture fixture) =>
        _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private TestScope As(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    private async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();
    }

    private async Task<Guid> UnitAsync()
    {
        await using var s = _fixture.CreateScope();
        var unit = new Chernika.Domain.Entities.AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "АУ-" + Suffix(),
            Name = "СЕ тест"
        };
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        return unit.Id;
    }

    private async Task<Guid> GsmAsync()
    {
        await using var s = _fixture.CreateScope();
        var gsm = new Chernika.Domain.Entities.GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "М-" + Suffix(),
            Nd = "ТУ " + Suffix()
        };
        s.Db.GsmMaterials.Add(gsm);
        s.Db.GsmMaterialClassifications.Add(
            new Chernika.Domain.Entities.GsmMaterialClassification
            {
                Id = Guid.NewGuid(),
                GsmMaterialId = gsm.Id,
                GroupName = "Группа " + Suffix(),
                SubgroupName = "Подгруппа " + Suffix()
            });
        await s.Db.SaveChangesAsync();
        return gsm.Id;
    }

    /// <summary>
    /// Проекция содержит всё, без чего редактор не может собрать команду:
    /// разделы, позиции, родительские связи, строки, материалы со стабильными
    /// Id, специальный выбор и версию загрузки.
    /// </summary>
    [Fact]
    public async Task GetStructureGraphAsync_LoadsFullGraphInNewScope()
    {
        var oils = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var unitId = await UnitAsync();
        var gsmId = await GsmAsync();
        var materialId = Guid.NewGuid();

        Guid cardId;
        await using (var s = As(_fixture.NormAdminA.Id))
        {
            cardId = await HKApprovalScenario.CreateDraftCardAsync(
                s, _fixture.NormAdminA.Id, withRow: false);

            await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId,
                new[] { new HKCardSectionWrite(oils, HKCardSectionType.Oils, 0) },
                new[]
                {
                    new HKCardStructureNodeWrite(
                        aggregateId, null, HKObjectLevel.Aggregate, "Агрегат", 2, 0, oils),
                    new HKCardStructureNodeWrite(
                        nodeId, aggregateId, HKObjectLevel.Node, "Узел", 3, 0, null)
                },
                new[]
                {
                    new HKCardStructureItemWrite(
                        itemId, nodeId, unitId, 4, 500m, "г", "раз в год", "примечание", 0,
                        null,
                        new[] { new HKCardMaterialWrite(materialId, gsmId, GsmCategory.Reserve) })
                },
                null,
                await VersionAsync(s, cardId)));
        }

        // Новый scope: никаких ранее загруженных сущностей в трекере.
        await using var read = As(_fixture.NormAdminA.Id);
        read.Db.ChangeTracker.Clear();
        var graph = await read.HK.GetStructureGraphAsync(cardId);

        Assert.NotNull(graph);
        Assert.True(graph!.IsAutonomous);

        var section = Assert.Single(graph.Sections);
        Assert.Equal(oils, section.Id);
        Assert.Equal(HKCardSectionType.Oils, section.SectionType);

        Assert.Equal(2, graph.Nodes.Count);
        var aggregate = graph.Nodes.Single(n => n.Id == aggregateId);
        Assert.Equal(oils, aggregate.SectionId);
        Assert.Equal(2, aggregate.Quantity);

        var node = graph.Nodes.Single(n => n.Id == nodeId);
        Assert.Equal(aggregateId, node.ParentId);

        var item = Assert.Single(graph.Items);
        Assert.Equal(itemId, item.Id);
        Assert.Equal(nodeId, item.NodeId);
        Assert.Equal(4, item.Quantity);
        Assert.Equal(500m, item.Volume);
        Assert.Equal("г", item.UnitOfMeasure);
        Assert.Equal("раз в год", item.Periodicity);
        Assert.Equal("примечание", item.Notes);
        Assert.False(string.IsNullOrWhiteSpace(item.AssemblyUnitName));

        var material = Assert.Single(item.Materials);
        Assert.Equal(materialId, material.Id);
        Assert.Equal(gsmId, material.GsmMaterialId);
        Assert.False(string.IsNullOrWhiteSpace(material.Name));
        Assert.False(material.SourceIsDeleted);

        // Чтение проекции ничего не отслеживает: редактор не получает EF-граф.
        Assert.Empty(read.Db.ChangeTracker.Entries()
            .Where(e => e.Entity is Chernika.Domain.Entities.HKCardNode
                or Chernika.Domain.Entities.HKCardItem
                or Chernika.Domain.Entities.HKCardSection));
    }

    /// <summary>
    /// Повторная загрузка того же сохранённого графа даёт те же значения:
    /// редактор после перезагрузки видит ровно то, что сохранил.
    /// </summary>
    [Fact]
    public async Task GetStructureGraphAsync_AfterReload_ReturnsSameValues()
    {
        var aggregateId = Guid.NewGuid();
        var unitId = await UnitAsync();

        Guid cardId;
        uint savedVersion;
        await using (var s = As(_fixture.NormAdminA.Id))
        {
            cardId = await HKApprovalScenario.CreateDraftCardAsync(
                s, _fixture.NormAdminA.Id, withRow: false);

            savedVersion = await VersionAsync(s, cardId);
            await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId,
                Array.Empty<HKCardSectionWrite>(),
                new[]
                {
                    new HKCardStructureNodeWrite(
                        aggregateId, null, HKObjectLevel.EquipmentModel, "Агрегат", 2, 0, null)
                },
                new[]
                {
                    new HKCardStructureItemWrite(
                        Guid.NewGuid(), aggregateId, unitId, 1, 250m, "г", null, null, 0, null,
                        Array.Empty<HKCardMaterialWrite>())
                },
                null,
                savedVersion));
        }

        await using var first = As(_fixture.NormAdminA.Id);
        var graph1 = await first.HK.GetStructureGraphAsync(cardId);

        await using var second = As(_fixture.NormAdminA.Id);
        var graph2 = await second.HK.GetStructureGraphAsync(cardId);

        Assert.NotNull(graph1);
        Assert.NotNull(graph2);
        Assert.Equal(
            graph1!.Nodes.Select(n => (n.Id, n.Name, n.Quantity, n.SortOrder)).ToList(),
            graph2!.Nodes.Select(n => (n.Id, n.Name, n.Quantity, n.SortOrder)).ToList());
        Assert.Equal(
            graph1.Items.Select(i => (i.Id, i.NodeId, i.Volume, i.UnitOfMeasure)).ToList(),
            graph2.Items.Select(i => (i.Id, i.NodeId, i.Volume, i.UnitOfMeasure)).ToList());
        // Версия загрузки — та, по которой команда будет сохранена.
        Assert.NotEqual(0u, graph2.RowVersion);
    }

    /// <summary>
    /// Отказ сохранения не оставляет в трекере снимка, который дописался бы
    /// следующим SaveChanges того же scope.
    /// </summary>
    [Fact]
    public async Task SaveStructure_WhenRefused_DoesNotPolluteNextSave()
    {
        var unitId = await UnitAsync();

        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        // Строка без сборочной единицы — отказ с понятным указанием строки.
        var badVersion = await VersionAsync(s, cardId);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId,
                Array.Empty<HKCardSectionWrite>(),
                Array.Empty<HKCardStructureNodeWrite>(),
                new[]
                {
                    new HKCardStructureItemWrite(
                        Guid.NewGuid(), null, Guid.Empty, 1, 1m, "кг", null, null, 0, null,
                        Array.Empty<HKCardMaterialWrite>())
                },
                null,
                badVersion)));

        Assert.Contains("№1", refusal.Message);

        // Следующее сохранение в этом же scope не дописывает отклонённую строку.
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            Array.Empty<HKCardSectionWrite>(),
            Array.Empty<HKCardStructureNodeWrite>(),
            new[]
            {
                new HKCardStructureItemWrite(
                    Guid.NewGuid(), null, unitId, 1, 1m, "кг", null, null, 0, null,
                    Array.Empty<HKCardMaterialWrite>())
            },
            null,
            await VersionAsync(s, cardId)));

        await using var read = _fixture.CreateScope();
        Assert.Equal(1, await read.Db.HKCardItems.CountAsync(x => x.HKCardId == cardId));
    }

    /// <summary>
    /// Снимки разделов редакции защищены перехватчиком: снимок раздела нельзя
    /// ни изменить, ни удалить внутри существующей редакции.
    /// </summary>
    [Fact]
    public async Task RevisionSection_CannotBeModifiedOrDeleted()
    {
        var oils = Guid.NewGuid();
        Guid revisionId;

        await using (var s = As(_fixture.NormAdminA.Id))
        {
            var cardId = await HKApprovalScenario.CreateDraftCardAsync(
                s, _fixture.NormAdminA.Id, withRow: false);

            await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId,
                new[] { new HKCardSectionWrite(oils, HKCardSectionType.Oils, 0) },
                new[]
                {
                    new HKCardStructureNodeWrite(
                        Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Агрегат", 1, 0, oils)
                },
                Array.Empty<HKCardStructureItemWrite>(),
                null,
                await VersionAsync(s, cardId)));

            revisionId = await s.Revisions.CaptureAsync(cardId, await VersionAsync(s, cardId));
        }

        await using var scope = As(_fixture.NormAdminA.Id);
        var section = await scope.Db.HKCardRevisionSections
            .FirstAsync(x => x.RevisionId == revisionId);

        section.SortOrder = 99;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Db.SaveChangesAsync());

        scope.Db.ChangeTracker.Clear();
        section = await scope.Db.HKCardRevisionSections
            .FirstAsync(x => x.RevisionId == revisionId);
        scope.Db.HKCardRevisionSections.Remove(section);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Db.SaveChangesAsync());

        // Снимок остался прежним.
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(0, (await scope.Db.HKCardRevisionSections
            .FirstAsync(x => x.RevisionId == revisionId)).SortOrder);
    }
}