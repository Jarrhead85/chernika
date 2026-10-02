using Chernika.Api.Contracts;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Переключение действующих потребителей марок ГСМ на Nd и классификацию (PR-5 §3).
/// <para>
/// Проверяется, что действующий контур ХК/поиска/аудита/контракта API больше не
/// зависит от прежних <c>Type</c>/<c>Gost</c>/<c>Description</c>, что несколько
/// подгрупп не блокируют новое назначение, а справочные связи остаются лишь
/// подсказкой и ничего не подставляют автоматически.
/// </para>
/// <para>
/// <b>Изоляция.</b> Фикстура <c>SeedGraphAsync</c> намеренно создаёт строку ХК
/// со ссылкой на soft-deleted марку — это и есть проверяемый случай. Такая
/// строка опасна для общей тестовой БД: её не видно ни через глобальный фильтр,
/// ни через подсчёт строк, поэтому оставленная марка и её связи тихо меняют
/// результат чужого теста. Каждый тет этого класса поэтому убирает за собой
/// ТОЛЬКО свои Guid через <see cref="GsmTestDataCleaner"/>.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmActiveConsumerSwitchIntegrationTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;

    /// <summary>Идентификаторы, созданные текущим тестом. xUnit создаёт новый
    /// экземпляр класса на каждый тест, поэтому список не протекает между ними.</summary>
    private readonly List<Guid> _created = new();

    public GsmActiveConsumerSwitchIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Уборка выполняется и при падении теста: иначе именно упавший тест
    /// оставляет за собой самый опасный остаток.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_created.Count == 0) return;
        await using var s = _fixture.CreateScope();
        await GsmTestDataCleaner.CleanupAsync(s.Db, _created);
    }

    /// <summary>Регистрирует объект для уборки. Вызывается всеми помощниками.</summary>
    private Guid Track(Guid id)
    {
        _created.Add(id);
        return id;
    }

    /// <summary>
    /// Идентификаторы, зарегистрированные текущим тестом. Открыт наружу, чтобы
    /// тест изоляции проверял РЕАЛЬНЫЙ путь уборки (тот же набор, который
    /// использует <see cref="DisposeAsync"/>), а не собирал свой — иначе
    /// расхождение между ними останется незамеченным.
    /// </summary>
    internal IReadOnlyList<Guid> TrackedIds => _created;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── Изоляция: уборка своих данных, а не чужих ─────────────────────────

    [Fact]
    public async Task SeededGraph_LeavesNothingBehind_AfterCleanup()
    {
        // Фикстура намеренно создаёт строку ХК со ссылкой на soft-deleted марку.
        // Если такая конструкция остаётся в общей БД, она не видна ни глобальному
        // фильтру, ни подсчёту строк, и тихо меняет результат чужого теста.
        // Проверяется уборка, а не сам сценарий: после уборки не должно остаться
        // НИ ОДНОЙ строки тестовых объектов.
        var before = await CountGlobalAsync();

        await SeedGraphAsync("НД изоляции " + Suffix());

        // Проверяется именно тот набор, который использует DisposeAsync после
        // теста. Раньше здесь собирался набор из возвращаемых кортежей, и
        // EquipmentModel в него не входил — уборка оставляла строку, а тест
        // об этом не знал. Именно такой остаток и ломал чужие тесты.
        var ids = TrackedIds;
        Assert.True(ids.Count >= 9,
            "зарегистрировано меньше объектов, чем создаёт фикстура: " + ids.Count);

        await using (var s = _fixture.CreateScope())
        {
            var during = await GsmTestDataCleaner.CountResidueAsync(s.Db, ids);
            Assert.True(during.Values.Sum() > 0,
                "фикстура ничего не создала — проверка уборки была бы пустой");
        }

        // Уборка выполняется на ОТДЕЛЬНОМ scope: общий DbContext первого using
        // уже освобождён, а новый scope гарантирует, что DELETE не будет
        // откачен вместе с его disposal.
        await using (var cleanupScope = _fixture.CreateScope())
        {
            await GsmTestDataCleaner.CleanupAsync(cleanupScope.Db, ids);
        }
        await using (var s = _fixture.CreateScope())
        {
            var residue = await GsmTestDataCleaner.CountResidueAsync(s.Db, ids);
            Assert.True(
                GsmTestDataCleaner.DescribeResidue(residue) == "остатка нет",
                "после уборки остались строки тестовых объектов: "
                + GsmTestDataCleaner.DescribeResidue(residue));
        }

        // Глобальные счётчики общей фикстуры вернулись к исходным. Это и есть
        // требование «порядок запуска классов не влияет на результат».
        var after = await CountGlobalAsync();
        foreach (var key in after.Keys)
        {
            Assert.True(before[key] == after[key],
                $"счётчик {key} не вернулся: было {before[key]}, стало {after[key]}. "
                + "Тест оставил данные в общей фикстуре.");
        }
    }

    [Fact]
    public async Task Cleanup_DoesNotTouchRows_CreatedByOtherTests()
    {
        // Уборка обязана быть направленной: чужие строки общей фикстуры не
        // трогаются. Иначе падение одного теста удалит фикстуры остальных.
        await using var s = _fixture.CreateScope();
        var before = await CountGlobalAsync();

        // Пустой набор идентификаторов — самый простой случай: удалять нечего.
        await GsmTestDataCleaner.CleanupAsync(s.Db, Array.Empty<Guid>());

        // Набор из несуществующих Guid: удалять тоже нечего, а существующие
        // строки должны уцелеть.
        await GsmTestDataCleaner.CleanupAsync(s.Db, new[] { Guid.NewGuid(), Guid.NewGuid() });

        var after = await CountGlobalAsync();
        foreach (var key in after.Keys)
        {
            Assert.True(before[key] == after[key],
                $"уборка несуществующих Guid изменила {key}: было {before[key]}, стало {after[key]}");
        }
    }

    /// <summary>Счётчики общей фикстуры по таблицам, затрагиваемым тестами ГСМ/ХК.</summary>
    private async Task<Dictionary<string, int>> CountGlobalAsync()
    {
        await using var s = _fixture.CreateScope();
        return new Dictionary<string, int>
        {
            ["GsmMaterials"] = await s.Db.GsmMaterials.IgnoreQueryFilters().CountAsync(),
            ["GsmMaterialClassifications"] = await s.Db.GsmMaterialClassifications.CountAsync(),
            ["GsmMaterialRelations"] = await s.Db.GsmMaterialRelations.IgnoreQueryFilters().CountAsync(),
            ["HKCards"] = await s.Db.HKCards.IgnoreQueryFilters().CountAsync(),
            ["HKCardItems"] = await s.Db.HKCardItems.CountAsync(),
            ["HKCardItemMaterials"] = await s.Db.HKCardItemMaterials.CountAsync(),
            ["HKCardComponents"] = await s.Db.HKCardComponents.CountAsync(),
            ["Nodes"] = await s.Db.Nodes.IgnoreQueryFilters().CountAsync(),
            ["AssemblyUnits"] = await s.Db.AssemblyUnits.IgnoreQueryFilters().CountAsync(),
            ["EquipmentModels"] = await s.Db.EquipmentModels.IgnoreQueryFilters().CountAsync(),
        };
    }

    // ── ХК: сводные строки показывают Nd, а не Gost ───────────────────────

    [Fact]
    public async Task HkAggregatedRows_ShowNd_AndSurviveDeletedMaterial()
    {
        var marker = "НД сводки " + Suffix();
        var (_, _, deletedId, _, _, parentCardId, _) = await SeedGraphAsync(marker);

        await using var s2 = _fixture.CreateScope();
        AsNormAdmin(s2);
        var rows = await s2.HK.GetAggregatedRowsAsync(parentCardId);

        Assert.True(rows.Count > 0, "сводка родительской карты пуста: связи компонентов нет");

        // Живая марка показывает актуальное НД из Nd.
        var row = Assert.Single(rows, r => r.Nd == marker);
        Assert.Equal(GsmCategory.Primary.ToString(), row.Category);
        Assert.False(row.MaterialIsDeleted);

        // Строка удалённой марки остаётся в сводке и читается: материал из
        // строки ХК не удаляется, данные не выдумываются, но помечаются.
        var deletedRow = Assert.Single(rows, r => r.MaterialIsDeleted);
        Assert.Equal(GsmCategory.Reserve.ToString(), deletedRow.Category);
        Assert.Equal("Удаляемая", deletedRow.GsmMaterialName![..9]);
        Assert.False(string.IsNullOrWhiteSpace(deletedRow.Nd));

        // Ни одна из трёх строк не потеряна: столько же, сколько сохранено в БД.
        Assert.Equal(3, rows.Count);
    }

    // ── Карточный контракт API: Nd + классификация вместо Type/Gost ────────

    [Fact]
    public async Task HkMaterialRef_ExposesNdAndClassification_AndNoLegacyFields()
    {
        var (primaryId, _, _, _, _, _, childCardId) = await SeedGraphAsync("НД контракта " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // Материалы лежат в строке ДОЧЕРНЕЙ карты: родительская — сводящая.
        var card = await s.HK.GetByIdAsync(childCardId);
        Assert.NotNull(card);

        var refs = GsmMaterialRefFactory.Create(
            card!.Items.SelectMany(i => i.Materials).First(m => m.GsmMaterialId == primaryId));

        Assert.Equal(primaryId, refs.Id);
        Assert.False(string.IsNullOrWhiteSpace(refs.Nd));
        Assert.Equal("Группа контракта", refs.GroupName);
        Assert.Equal(new[] { "Альфа", "Бета" }, refs.SubgroupNames);

        // Прежних полей в контракте больше нет: они удаляются в PR-6, и держать
        // их в публичном DTO значит закрепить переходные поля в API.
        var names = typeof(GsmMaterialRefDto)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Type", names);
        Assert.DoesNotContain("Gost", names);
        Assert.Contains("Nd", names);
        Assert.Contains("GroupName", names);
        Assert.Contains("SubgroupNames", names);
    }

    [Fact]
    public async Task HkMaterialRef_OfDeletedMaterial_StaysReadableWithRealData()
    {
        var (primaryId, _, deletedId, _, _, _, childCardId) = await SeedGraphAsync("НД удалённой " + Suffix());

        await using var s2 = _fixture.CreateScope();
        AsNormAdmin(s2);
        var card = await s2.HK.GetByIdAsync(childCardId);
        Assert.NotNull(card);

        var row = Assert.Single(
            card!.Items.SelectMany(i => i.Materials),
            m => m.GsmMaterialId == deletedId);

        // Строка с удалённой маркой загружена и её данные доступны: скрытая
        // фильтром аномалия читается адресно, строка при этом не удаляется.
        Assert.NotNull(row.GsmMaterial);
        Assert.True(row.GsmMaterial!.IsDeleted);

        var refs = GsmMaterialRefFactory.Create(row);
        Assert.Equal(deletedId, refs.Id);
        Assert.False(string.IsNullOrWhiteSpace(refs.Nd));
        Assert.Equal("Группа контракта", refs.GroupName);

        // Соседняя живая строка при этом читается полностью.
        var live = GsmMaterialRefFactory.Create(
            card.Items.SelectMany(i => i.Materials).First(m => m.GsmMaterialId == primaryId));
        Assert.False(string.IsNullOrWhiteSpace(live.Nd));
        Assert.Equal(live.GroupName, refs.GroupName);
    }

    [Fact]
    public async Task HkMaterialNames_OfDeletedMaterial_AreStillResolved()
    {
        // Название для уже сохранённой строки ХК берётся без фильтра мягкого
        // удаления: иначе историческая строка показывала бы «—» вместо названия.
        var (_, _, deletedId, _, _, _, childCardId) = await SeedGraphAsync("НД имени " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var card = await s.HK.GetByIdAsync(childCardId);
        var names = await s.GsmMaterials.GetNamesAsync(
            card!.Items.SelectMany(i => i.Materials).Select(m => m.GsmMaterialId));

        Assert.Equal(3, names.Count);
        Assert.StartsWith("Удаляемая", names[deletedId]);
    }

    [Fact]
    public async Task HkUpdate_TextOnlyEdit_KeepsRowOfSoftDeletedMaterial()
    {
        // Регрессия на тихую потерю данных. У GsmMaterial есть глобальный фильтр
        // по мягкому удалению; обычный Include навигации GsmMaterial возвращал не
        // только скрытую марку, но и саму строку HKCardItemMaterial. Дальше
        // SyncItemMaterialsAsync сравнивал желаемое состояние с независимой
        // проекцией из БД и УДАЛЯЛ строку при любой правке карточки.
        var (_, _, deletedId, _, _, _, childCardId) = await SeedGraphAsync("НД регресс " + Suffix());

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(childCardId);
            Assert.NotNull(card);

            // Ничего, кроме текста карточки, не меняем.
            card!.Notes = "Правка текста при наличии удалённой марки";
            var updated = await s.HK.UpdateAsync(card);
            Assert.NotNull(updated);
        }

        await using var s2 = _fixture.CreateScope();
        var row = await s2.Db.HKCardItemMaterials.AsNoTracking()
            .SingleAsync(m => m.GsmMaterialId == deletedId);
        Assert.Equal(3, await s2.Db.HKCardItemMaterials.CountAsync(
            m => m.HKCardItemId == row.HKCardItemId));
    }

    // ── Справочные связи остаются только подсказкой ────────────────────────

    [Fact]
    public async Task RelationHints_AreReadOnlyHints_AndDoNotTouchHkRows()
    {
        var (primaryId, relatedId, _, _, _, _, _) = await SeedGraphAsync("НД подсказки " + Suffix());

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = primaryId,
                RelatedGsmMaterialId = relatedId,
                RelationType = GsmRelationType.DuplicateAndReserve,
                Note = "Подсказка",
            });
        }

        await using var s2 = _fixture.CreateScope();
        AsNormAdmin(s2);

        var hints = await s2.GsmMaterials.GetRelationHintsAsync(new[] { primaryId });
        var forPrimary = Assert.Single(hints[primaryId]);
        Assert.Equal(relatedId, forPrimary.RelatedMaterialId);
        Assert.Equal(GsmRelationType.DuplicateAndReserve, forPrimary.RelationType);
        Assert.False(string.IsNullOrWhiteSpace(forPrimary.RelatedName));

        // Обратного направления нет: связь направленная.
        Assert.DoesNotContain(relatedId, hints.Keys);

        // Пустой набор исходных марок — пустой результат, без запроса лишнего.
        Assert.Empty(await s2.GsmMaterials.GetRelationHintsAsync(Array.Empty<Guid>()));

        // Главное свойство подсказки: прочтение связей НЕ меняет строки ХК.
        // Категория остаётся той, что задана в документе, и не выводится из
        // RelationType.
        var rowsBefore = await s2.Db.HKCardItemMaterials.AsNoTracking()
            .Where(r => r.GsmMaterialId == primaryId || r.GsmMaterialId == relatedId)
            .Select(r => new { r.HKCardItemId, r.GsmMaterialId, r.Category })
            .ToListAsync();
        Assert.Equal(2, rowsBefore.Count);
        Assert.Single(rowsBefore, r => r.GsmMaterialId == primaryId && r.Category == GsmCategory.Primary);
        Assert.Single(rowsBefore, r => r.GsmMaterialId == relatedId && r.Category == GsmCategory.Duplicate);
    }

    // ── Предложение из черновика ХК пишет Nd, а не Gost ───────────────────

    [Fact]
    public async Task ReferenceProposal_StubWritesNd_AndNotLegacyGost()
    {
        var nodeId = await SeedNodeAsync();
        var draftCardId = await SeedDraftCardAsync(nodeId);
        var gost = "ГОСТ предложения " + Suffix();

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var proposal = await s.HK.CreateProposalAsync(
            draftCardId, ProposalTargetType.GsmMaterial,
            code: "MAT-" + Suffix(),
            name: "Марка предложения " + Suffix(),
            description: "Назначение предложения",
            gost: gost,
            type: "Прежняя подгруппа");

        var stub = await s.Db.GsmMaterials.AsNoTracking()
            .SingleAsync(m => m.Id == proposal.CreatedStubGsmMaterialId);

        Assert.True(stub.IsDraft);
        Assert.Equal(gost, stub.Nd);
        Assert.Equal("Назначение предложения", stub.IntendedUse);

        // Переходные Gost/Description/Type удалены из GsmMaterials (PR-6, фаза B),
        // поэтому черновик содержит только Nd/IntendedUse. Прежний Type предложения
        // остаётся в САМОМ предложении (ReferenceProposal.Type) — это другое поле
        // другой таблицы, и в марку больше не переносится.
        Assert.False(await s.Db.GsmMaterialClassifications.AnyAsync(c => c.GsmMaterialId == stub.Id));
    }

    [Fact]
    public async Task ReferenceProposal_Accept_StillRequiresClassification()
    {
        var nodeId = await SeedNodeAsync();
        var draftCardId = await SeedDraftCardAsync(nodeId);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var proposal = await s.HK.CreateProposalAsync(
            draftCardId, ProposalTargetType.GsmMaterial,
            code: "MAT-" + Suffix(),
            name: "Марка без классификации " + Suffix(),
            description: null, gost: null, type: null);

        // Публикация без группы и подгруппы отклоняется — как и раньше.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.AcceptProposalAsync(proposal.Id));
        Assert.Contains("не классифицирована", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Предложение осталось в статусе «Ожидает».
        var still = await s.Db.ReferenceProposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(ProposalStatus.Pending, still.Status);
    }

    // ── Предел НД в пути предложения (замечание A2) ──────────────────────

    [Fact]
    public void NdLimits_DoNotDriftBetweenMaterialAndProposalColumn()
    {
        // Предел НД марки (1000) и предел колонки ReferenceProposal.Gost
        // (200) — разные величины, и их нельзя выводить одно из другого. Тест
        // сверяет фактическую конфигурацию EF с константой сервиса: если
        // колонку расширят, тест упадёт и заставит обновить проверку, а не
        // молча оставит путь с сырой ошибкой БД.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=configuration_probe;Username=none")
            .Options;
        using var ctx = new AppDbContext(options);

        var gost = ctx.Model.FindEntityType(typeof(ReferenceProposal))!
            .FindProperty(nameof(ReferenceProposal.Gost))!;
        Assert.Equal(200, gost.GetMaxLength());

        // Предел марки — выбранное продуктовое правило, а НД-колонка предложения
        // — техническое ограничение varchar. Ровно эта разница и ломает путь.
        Assert.Equal(1000, GsmMaterialService.NdMaxLength);
        Assert.True(gost.GetMaxLength() < GsmMaterialService.NdMaxLength);
    }

    [Fact]
    public async Task ReferenceProposal_RejectsLongNdBeforeDatabase_WithAddressableMessage()
    {
        // НД длиннее 200 символов принимается справочником марок, но этот путь
        // ограничен колонкой ReferenceProposal.Gost varchar(200). Без проверки
        // пользователь получил бы 22001 string_data_right_truncation — сырую
        // ошибку PostgreSQL вместо причины.
        var nodeId = await SeedNodeAsync();
        var draftCardId = await SeedDraftCardAsync(nodeId);
        var longNd = new string('Н', 201);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.CreateProposalAsync(
            draftCardId, ProposalTargetType.GsmMaterial,
            code: "MAT-" + Suffix(),
            name: "Марка длинного НД " + Suffix(),
            description: null, gost: longNd, type: null));

        // Сообщение адресное: называет предел и говорит, где вводить полный НД.
        Assert.Contains("200", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);

        // Ничего не записано и, главное, ничего не усечено: ни предложения, ни
        // черновика марки.
        Assert.False(await s.Db.ReferenceProposals.AnyAsync(p => p.HKCardId == draftCardId));
    }

    [Fact]
    public async Task ReferenceProposal_AcceptsNdAtColumnLimit_WithoutTruncation()
    {
        // Граница проверки: ровно 200 символов проходят, и значение сохраняется
        // целиком. Регресс «отрезали бы по 200» здесь ловится.
        var nodeId = await SeedNodeAsync();
        var draftCardId = await SeedDraftCardAsync(nodeId);
        var nd = new string('Н', 200);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var proposal = await s.HK.CreateProposalAsync(
            draftCardId, ProposalTargetType.GsmMaterial,
            code: "MAT-" + Suffix(),
            name: "Марка НД 200 " + Suffix(),
            description: null, gost: nd, type: null);

        var stored = await s.Db.ReferenceProposals.AsNoTracking()
            .SingleAsync(p => p.Id == proposal.Id);
        Assert.Equal(200, stored.Gost!.Length);
        Assert.Equal(nd, stored.Gost);

        // НД переносится в черновик марки целиком.
        var stub = await s.Db.GsmMaterials.AsNoTracking()
            .SingleAsync(m => m.Id == proposal.CreatedStubGsmMaterialId);
        Assert.Equal(nd, stub.Nd);
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────

    /// <summary>
    /// Граф для проверки потребителей ХК: марка с ДВУМЯ подгруппами, вторая
    /// марка и УДАЛЁННАЯ марка, сборочная единица, узел, строка ХК с тремя
    /// материалами и родительская карточка, в сводку которой они попадают.
    /// <para>
    /// Мультиподгруппность выбрана намеренно: именно она раньше была запрещена в
    /// новых строках ХК. Удалённая марка добавляется в строку ПОСЛЕ удаления —
    /// иначе сервис правомерно откажет в soft-delete, защищая активную ссылку.
    /// </para>
    /// </summary>
    private async Task<(Guid primaryId, Guid relatedId, Guid deletedId, Guid nodeId, Guid unitId,
        Guid parentCardId, Guid childCardId)> SeedGraphAsync(string nd)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var primary = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Основная " + Suffix(),
            Nd = nd,
            GroupName = "Группа контракта",
            SubgroupNames = new List<string> { "Альфа", "Бета" },
        });

        var related = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Связанная " + Suffix(),
            Nd = nd + " (связанная)",
            GroupName = "Группа контракта",
            SubgroupNames = new List<string> { "Альфа" },
        });

        var toDelete = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = "Удаляемая " + Suffix(),
            Nd = nd + " (удаляемая)",
            GroupName = "Группа контракта",
            SubgroupNames = new List<string> { "Альфа" },
        });
        Assert.True(await s.GsmMaterials.DeleteAsync(toDelete.Id));

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

        // Родительская карточка уровня выше узла, чтобы строки попали в сводку.
        var model = new EquipmentModel
        {
            Id = Guid.NewGuid(),
            Index = "И-" + Suffix(),
            Name = "Изделие " + Suffix(),
            IsDeleted = false,
        };
        s.Db.EquipmentModels.Add(model);

        // Дочерняя карта — черновик: только черновик и карта на доработке
        // редактируются. Именно на ней проверяется сохранность исторической
        // строки при текстовой правке.
        var childCard = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
        };
        s.Db.HKCards.Add(childCard);

        var item = new HKCardItem
        {
            Id = Guid.NewGuid(),
            HKCardId = childCard.Id,
            AssemblyUnitId = unit.Id,
            SortOrder = 1,
            Quantity = 1,
            Volume = 10,
            UnitOfMeasure = "кг",
        };
        s.Db.HKCardItems.Add(item);

        s.Db.HKCardItemMaterials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = item.Id,
            GsmMaterialId = primary.Id,
            Category = GsmCategory.Primary,
        });
        s.Db.HKCardItemMaterials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = item.Id,
            GsmMaterialId = related.Id,
            Category = GsmCategory.Duplicate,
        });
        // Ссылка на удалённую марку: строка сохраняется, материал скрыт фильтром.
        s.Db.HKCardItemMaterials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = item.Id,
            GsmMaterialId = toDelete.Id,
            Category = GsmCategory.Reserve,
        });

        var parentCard = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = HKCardStatus.Approved,
            ObjectLevel = HKObjectLevel.EquipmentModel,
            EquipmentModelId = model.Id,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            ApprovedDate = DateTime.UtcNow,
        };
        s.Db.HKCards.Add(parentCard);
        s.Db.HKCardComponents.Add(new HKCardComponent
        {
            Id = Guid.NewGuid(),
            ParentHKCardId = parentCard.Id,
            ChildHKCardId = childCard.Id,
            SortOrder = 1,
            AddedAt = DateTime.UtcNow,
            AddedByUserId = _fixture.NormAdminA.Id,
            ChildCode = childCard.Code,
            ChildVersion = childCard.Version,
            ChildApprovedAt = null,
        });

        await s.Db.SaveChangesAsync();

        // Каждый созданный объект регистрируется для уборки. Забытый здесь Guid
        // оставит в общей БД строку ХК со ссылкой на soft-deleted марку.
        Track(primary.Id);
        Track(related.Id);
        Track(toDelete.Id);
        Track(node.Id);
        Track(unit.Id);
        Track(model.Id);
        Track(childCard.Id);
        Track(item.Id);
        Track(parentCard.Id);

        return (primary.Id, related.Id, toDelete.Id, node.Id, unit.Id, parentCard.Id, childCard.Id);
    }

    private async Task<Guid> SeedNodeAsync()
    {
        await using var s = _fixture.CreateScope();
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-" + Suffix(),
            Name = "Узел " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return Track(node.Id);
    }

    /// <summary>Черновик ХК: предложения создаются только для него или для карты на доработке.</summary>
    private async Task<Guid> SeedDraftCardAsync(Guid nodeId)
    {
        await using var s = _fixture.CreateScope();
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
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();
        return Track(card.Id);
    }
}
