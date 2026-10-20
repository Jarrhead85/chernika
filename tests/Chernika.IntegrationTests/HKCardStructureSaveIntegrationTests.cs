using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Chernika.Web.Pages.Structure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Атомарное сохранение разделов, позиций и строк.
/// <para>
/// Проверяется, что единый сценарий сохраняет весь граф целиком или не
/// сохраняет ничего, что уровень соответствует сохранённым строкам, а
/// принадлежность раздела остаётся в пределах одной карты.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardStructureSaveIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardStructureSaveIntegrationTests(TestDatabaseFixture fixture) =>
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

    private static SaveHKCardStructureCommand Command(
        Guid cardId, uint version, uint expectedVersion) =>
        new(cardId, Array.Empty<HKCardSectionWrite>(), Array.Empty<HKCardStructureNodeWrite>(),
            Array.Empty<HKCardStructureItemWrite>(), null, expectedVersion);

    // ── Атомарность и уровень ──

    [Fact]
    public async Task SaveStructure_SectionsPositionsRows_AreSavedTogetherWithLevel()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var sectionId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var unitId = await UnitAsync();

        var version = await VersionAsync(s, cardId);

        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[] { new HKCardSectionWrite(sectionId, HKCardSectionType.Oils, 0) },
            new[]
            {
                new HKCardStructureNodeWrite(
                    aggregateId, null, HKObjectLevel.Aggregate, "Агрегат", 2, 0, sectionId)
            },
            new[]
            {
                new HKCardStructureItemWrite(
                    Guid.NewGuid(), aggregateId, unitId, 4, 500m, "г", null, null, 0, null,
                    Array.Empty<HKCardMaterialWrite>())
            },
            null,
            version));

        await using var read = _fixture.CreateScope();

        Assert.Equal(1, await read.Db.HKCardSections.CountAsync(x => x.HKCardId == cardId));
        Assert.Equal(1, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
        Assert.Equal(1, await read.Db.HKCardItems.CountAsync(x => x.HKCardId == cardId));

        // Верхний агрегат даёт ХК изделия.
        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(HKObjectLevel.EquipmentModel, card.ObjectLevel);
    }

    [Fact]
    public async Task SaveStructure_IdsRemainStable_OnRepeatedSave()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var sectionId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var unitId = await UnitAsync();

        var version = await VersionAsync(s, cardId);
        var command = new SaveHKCardStructureCommand(
            cardId,
            new[] { new HKCardSectionWrite(sectionId, HKCardSectionType.Oils, 0) },
            new[]
            {
                new HKCardStructureNodeWrite(
                    aggregateId, null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, sectionId)
            },
            new[]
            {
                new HKCardStructureItemWrite(
                    itemId, aggregateId, unitId, 1, 1m, "кг", null, null, 0, null,
                    Array.Empty<HKCardMaterialWrite>())
            },
            null, version);

        await s.HK.SaveStructureAsync(command);

        // Повторное сохранение с теми же идентификаторами не создаёт дублей.
        var again = command with { ExpectedRowVersion = await VersionAsync(s, cardId) };
        await s.HK.SaveStructureAsync(again);

        await using var read = _fixture.CreateScope();
        Assert.Equal(1, await read.Db.HKCardSections.CountAsync(x => x.HKCardId == cardId));
        Assert.Equal(1, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
        Assert.Equal(1, await read.Db.HKCardItems.CountAsync(x => x.HKCardId == cardId));
    }

    // ── Принадлежность разделу ──

    [Fact]
    public async Task SaveStructure_ForeignSection_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        // Раздел другой карты.
        Guid foreignSection;
        await using (var other = As(_fixture.NormAdminA.Id))
        {
            var otherCard = await HKApprovalScenario.CreateDraftCardAsync(
                other, _fixture.NormAdminA.Id, withRow: false);

            foreignSection = Guid.NewGuid();
            other.Db.HKCardSections.Add(new Chernika.Domain.Entities.HKCardSection
            {
                Id = foreignSection,
                HKCardId = otherCard,
                SectionType = HKCardSectionType.Oils,
                SortOrder = 0
            });
            await other.Db.SaveChangesAsync();
        }

        var version = await VersionAsync(s, cardId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId,
                Array.Empty<HKCardSectionWrite>(),
                new[]
                {
                    new HKCardStructureNodeWrite(
                        Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, 0,
                        foreignSection)
                },
                Array.Empty<HKCardStructureItemWrite>(),
                null, version)));

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
    }

    [Fact]
    public async Task SaveStructure_MovingBranchBetweenSections_IsPersisted()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var oils = Guid.NewGuid();
        var fuels = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var unitId = await UnitAsync();
        var itemId = Guid.NewGuid();
        var gsmId = await GsmAsync();

        var version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[]
            {
                new HKCardSectionWrite(oils, HKCardSectionType.Oils, 0),
                new HKCardSectionWrite(fuels, HKCardSectionType.Fuels, 1)
            },
            new[]
            {
                new HKCardStructureNodeWrite(
                    aggregateId, null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, oils)
            },
            new[]
            {
                new HKCardStructureItemWrite(
                    itemId, aggregateId, unitId, 3, 250m, "г", null, null, 0, null,
                    new[]
                    {
                        new HKCardMaterialWrite(Guid.NewGuid(), gsmId, GsmCategory.Primary)
                    })
            },
            null, version));

        // Перенос ветки в другой раздел.
        version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[]
            {
                new HKCardSectionWrite(oils, HKCardSectionType.Oils, 0),
                new HKCardSectionWrite(fuels, HKCardSectionType.Fuels, 1)
            },
            new[]
            {
                new HKCardStructureNodeWrite(
                    aggregateId, null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, fuels)
            },
            new[]
            {
                new HKCardStructureItemWrite(
                    itemId, aggregateId, unitId, 3, 250m, "г", null, null, 0, null,
                    new[]
                    {
                        new HKCardMaterialWrite(Guid.NewGuid(), gsmId, GsmCategory.Primary)
                    })
            },
            null, version));

        await using var read = _fixture.CreateScope();
        Assert.Equal(fuels, (await read.Db.HKCardNodes.AsNoTracking()
            .FirstAsync(x => x.Id == aggregateId)).SectionId);
    }

    [Fact]
    public async Task SaveStructure_MovingRowBetweenParents_KeepsIdNormsAndMaterials()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var unitId = await UnitAsync();
        var gsmId = await GsmAsync();

        var version = await VersionAsync(s, cardId);
        var sections = Array.Empty<HKCardSectionWrite>();

        HKCardStructureNodeWrite[] Positions(params Guid[] ids) => ids
            .Select((id, i) => new HKCardStructureNodeWrite(
                id, null, HKObjectLevel.Aggregate, "Агрегат " + i, 1, i, null))
            .ToArray();

        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId, sections, Positions(first, second),
            new[]
            {
                new HKCardStructureItemWrite(
                    itemId, first, unitId, 5, 750m, "г", "раз в год", "примечание", 0, null,
                    new[] { new HKCardMaterialWrite(Guid.NewGuid(), gsmId, GsmCategory.Reserve) })
            },
            null, version));

        // Перенос той же строки под другую позицию.
        version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId, sections, Positions(first, second),
            new[]
            {
                new HKCardStructureItemWrite(
                    itemId, second, unitId, 5, 750m, "г", "раз в год", "примечание", 0, null,
                    new[] { new HKCardMaterialWrite(Guid.NewGuid(), gsmId, GsmCategory.Reserve) })
            },
            null, version));

        await using var read = _fixture.CreateScope();
        var stored = await read.Db.HKCardItems.AsNoTracking().SingleAsync(x => x.Id == itemId);

        Assert.Equal(second, stored.HKCardNodeId);
        Assert.Equal(750m, stored.Volume);
        Assert.Equal("раз в год", stored.Periodicity);
        Assert.Equal("примечание", stored.Notes);
        Assert.Equal(1, await read.Db.HKCardItemMaterials.CountAsync(m => m.HKCardItemId == itemId));
    }

    [Fact]
    public async Task SaveStructure_DeletingNonEmptySection_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var oils = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();

        var version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId,
            new[] { new HKCardSectionWrite(oils, HKCardSectionType.Oils, 0) },
            new[]
            {
                new HKCardStructureNodeWrite(
                    aggregateId, null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, oils)
            },
            Array.Empty<HKCardStructureItemWrite>(), null, version));

        // Раздел уходит из набора, но его ветка остаётся: молчаливого удаления
        // содержимого быть не должно.
        version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId, Array.Empty<HKCardSectionWrite>(), Array.Empty<HKCardStructureNodeWrite>(),
                Array.Empty<HKCardStructureItemWrite>(), null, version)));

        await using var read = _fixture.CreateScope();
        Assert.Equal(1, await read.Db.HKCardSections.CountAsync(x => x.HKCardId == cardId));
        Assert.Equal(1, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
    }

    // ── Специальный выбор верхнего узла ──

    [Fact]
    public async Task SaveStructure_TopNode_AcceptsAndKeepsChoice()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var nodeId = Guid.NewGuid();

        var version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId, Array.Empty<HKCardSectionWrite>(),
            new[]
            {
                new HKCardStructureNodeWrite(
                    nodeId, null, HKObjectLevel.Node, "Узел", 1, 0, null)
            },
            Array.Empty<HKCardStructureItemWrite>(),
            HKObjectLevel.Aggregate, version));

        // Повторное сохранение того же дерева не теряет выбор.
        version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId, Array.Empty<HKCardSectionWrite>(),
            new[]
            {
                new HKCardStructureNodeWrite(
                    nodeId, null, HKObjectLevel.Node, "Узел иной", 2, 0, null)
            },
            Array.Empty<HKCardStructureItemWrite>(),
            HKObjectLevel.Aggregate, version));

        await using var read = _fixture.CreateScope();
        var card = await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        Assert.Equal(HKObjectLevel.Aggregate, card.NodeRootCardLevel);
        Assert.Equal(HKObjectLevel.Aggregate, card.ObjectLevel);
    }

    // ── Конфликт версии и блокировки ──

    [Fact]
    public async Task SaveStructure_StaleVersion_LeavesNothingSaved()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var stale = await VersionAsync(s, cardId);

        await using (var other = _fixture.CreateScope())
        {
            var card = await other.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.UpdatedAt = DateTime.UtcNow.AddMinutes(3);
            await other.Db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<HkCardVersionConflictException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId, Array.Empty<HKCardSectionWrite>(),
                new[]
                {
                    new HKCardStructureNodeWrite(
                        Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, null)
                },
                Array.Empty<HKCardStructureItemWrite>(), null, stale)));

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
        Assert.Equal(HKObjectLevel.Undefined,
            (await read.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).ObjectLevel);
    }

    [Fact]
    public async Task SaveStructure_WhileCompositionOnReview_IsRefused()
    {
        // Строка под корнем даёт карте определённый уровень, иначе отправка
        // состава отказала бы ещё раньше по уровню, а не по блокировке.
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: true);

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);
        await HKApprovalScenario.ApproveDetailsAsync(As(_fixture.NormAdminA2.Id), cardId);
        await HKApprovalScenario.SubmitCompositionAsync(s, cardId);

        var version = await VersionAsync(s, cardId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId, Array.Empty<HKCardSectionWrite>(),
                new[]
                {
                    new HKCardStructureNodeWrite(
                        Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, null)
                },
                Array.Empty<HKCardStructureItemWrite>(), null, version)));

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
    }

    /// <summary>На рассмотрении реквизитов автор структуру заполнять может.</summary>
    [Fact]
    public async Task SaveStructure_WhileDetailsOnReview_IsAllowed()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await HKApprovalScenario.SubmitDetailsAsync(
            s, cardId, _fixture.NormAdminA2.Id);

        var version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId, Array.Empty<HKCardSectionWrite>(),
            new[]
            {
                new HKCardStructureNodeWrite(
                    Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, null)
            },
            Array.Empty<HKCardStructureItemWrite>(), null, version));

        await using var read = _fixture.CreateScope();
        Assert.Equal(1, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
    }

    // ── Недопустимые команды ──

    [Fact]
    public async Task SaveStructure_IncompleteRow_IsRefusedAndNotSaved()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var version = await VersionAsync(s, cardId);

        // Строка без сборочной единицы: Guid.Empty в обязательной ссылке в базу
        // не пишется и молча не выбрасывается.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId, Array.Empty<HKCardSectionWrite>(),
                Array.Empty<HKCardStructureNodeWrite>(),
                new[]
                {
                    new HKCardStructureItemWrite(
                        Guid.NewGuid(), null, Guid.Empty, 1, 1m, "кг", null, null, 0, null,
                        Array.Empty<HKCardMaterialWrite>())
                },
                null, version)));

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardItems.CountAsync(x => x.HKCardId == cardId));
    }

    [Fact]
    public async Task SaveStructure_ComplexPosition_IsRefused()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var version = await VersionAsync(s, cardId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId, Array.Empty<HKCardSectionWrite>(),
                new[]
                {
                    new HKCardStructureNodeWrite(
                        Guid.NewGuid(), null, HKObjectLevel.Complex, "Комплекс", 1, 0, null)
                },
                Array.Empty<HKCardStructureItemWrite>(), null, version)));

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
    }

    [Fact]
    public async Task SaveStructure_ServicePropertyIsNotEditable()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        await using var before = _fixture.CreateScope();
        var codeBefore = (await before.Db.HKCards.AsNoTracking()
            .FirstAsync(c => c.Id == cardId)).Code;

        var version = await VersionAsync(s, cardId);
        await s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
            cardId, Array.Empty<HKCardSectionWrite>(),
            new[]
            {
                new HKCardStructureNodeWrite(
                    Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Агрегат", 1, 0, null)
            },
            Array.Empty<HKCardStructureItemWrite>(), null, version));

        await using var after = _fixture.CreateScope();
        var card = await after.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        Assert.Equal(codeBefore, card.Code);
        Assert.Equal(HKCardStatus.Draft, card.Status);
        Assert.Null(card.CurrentAcceptedRevisionId);
    }

    // ── Название позиции и раздел новой ветки ──

    /// <summary>
    /// Пустое название позиции не подменяется шаблонным текстом: команда
    /// отклоняется с понятным указанием номера позиции, и в базе ничего нет.
    /// </summary>
    [Fact]
    public async Task SaveStructure_EmptyPositionName_IsRefusedWithPositionNumber()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var version = await VersionAsync(s, cardId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.HK.SaveStructureAsync(new SaveHKCardStructureCommand(
                cardId, Array.Empty<HKCardSectionWrite>(),
                new[]
                {
                    new HKCardStructureNodeWrite(
                        Guid.NewGuid(), null, HKObjectLevel.Aggregate, "   ", 1, 0, null)
                },
                Array.Empty<HKCardStructureItemWrite>(), null, version)));

        Assert.Contains("наименование позиции", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("№1", error.Message);

        await using var read = _fixture.CreateScope();
        Assert.Equal(0, await read.Db.HKCardNodes.CountAsync(x => x.HKCardId == cardId));
    }

    /// <summary>
    /// Выбранный вид раздела применяется к ветке: раздел создаётся вместе с
    /// позицией, и после сохранения позиция остаётся в нём.
    /// </summary>
    [Fact]
    public async Task NewBranchSectionType_PositionIsSavedInChosenSection()
    {
        await using var s = As(_fixture.NormAdminA.Id);
        var cardId = await HKApprovalScenario.CreateDraftCardAsync(
            s, _fixture.NormAdminA.Id, withRow: false);

        var version = await VersionAsync(s, cardId);
        var graph = await s.HK.GetStructureGraphAsync(cardId);
        Assert.NotNull(graph);

        var state = HKStructureEditorState.FromGraph(graph!);
        state.UpdateVersion(version);
        state.NewBranchSectionType = HKCardSectionType.Oils;
        var position = state.AddTopPosition(HKObjectLevel.Aggregate);
        position.Name = "Ветка масла";

        await s.HK.SaveStructureAsync(state.BuildCommand(cardId));

        s.Db.ChangeTracker.Clear();
        var reloaded = await s.HK.GetStructureGraphAsync(cardId);
        Assert.NotNull(reloaded);

        var section = Assert.Single(reloaded!.Sections);
        Assert.Equal(HKCardSectionType.Oils, section.SectionType);

        var saved = Assert.Single(reloaded.Nodes);
        Assert.Equal(section.Id, saved.SectionId);
        Assert.Equal("Ветка масла", saved.Name);
    }
}
