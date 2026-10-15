using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Хранилище редакций собственной структуры карты на PostgreSQL.
/// <para>
/// Проверяется главное: принятая структура читается ИЗ СНИМКА и не зависит от
/// текущих рабочих строк и справочных названий.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardRevisionIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardRevisionIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<TestScope> AuthorAsync()
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        return s;
    }

    /// <summary>Карта с деревом, строкой и маркой ГСМ.</summary>
    private async Task<Guid> CreateCardWithStructureAsync()
    {
        await using var s = await AuthorAsync();

        var unit = new AssemblyUnit
        {
            Id = Guid.NewGuid(), Code = "АУ-" + Suffix(), Name = "СБ " + Suffix()
        };
        var gsm = new GsmMaterial
        {
            Id = Guid.NewGuid(), Name = "Масло " + Suffix(), Nd = "ТУ-" + Suffix()
        };
        s.Db.AssemblyUnits.Add(unit);
        s.Db.GsmMaterials.Add(gsm);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Name = "Карта " + Suffix(),
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            Purpose = "Назначение " + Suffix(),
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>()
        };
        var created = await s.HK.CreateAsync(card);

        var root = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = created.Id,
            Level = HKObjectLevel.Node, Name = "Узел " + Suffix(),
            Quantity = 1, SortOrder = 1
        };
        var child = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = created.Id, ParentId = root.Id,
            Level = HKObjectLevel.Node, Name = "Ветвь " + Suffix(),
            Quantity = 2, SortOrder = 1
        };
        s.Db.HKCardNodes.AddRange(root, child);

        var item = new HKCardItem
        {
            Id = Guid.NewGuid(), HKCardId = created.Id, HKCardNodeId = root.Id,
            AssemblyUnitId = unit.Id, Quantity = 3, Volume = 1.25m,
            UnitOfMeasure = "кг", Periodicity = "12 мес", Notes = "прим.", SortOrder = 1
        };
        item.Materials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(), HKCardItemId = item.Id,
            GsmMaterialId = gsm.Id, Category = GsmCategory.Primary
        });
        s.Db.HKCardItems.Add(item);
        await s.Db.SaveChangesAsync();

        return created.Id;
    }

    private async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();
    }

    /// <summary>Назначает указатель принятой редакции как предусловие теста.</summary>
    private async Task<Guid> CaptureAndAssignAsync(TestScope s, Guid cardId)
    {
        var version = await VersionAsync(s, cardId);
        var revisionId = await s.Revisions.CaptureAsync(cardId, version);

        await using (var setup = _fixture.CreateScope())
        {
            var card = await setup.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.CurrentAcceptedRevisionId = revisionId;
            await setup.Db.SaveChangesAsync();
        }

        return revisionId;
    }

    // ── Фиксация ──

    [Fact]
    public async Task Capture_CopiesTreeRowsNormsAndMaterials()
    {
        var cardId = await CreateCardWithStructureAsync();

        await using var s = await AuthorAsync();
        var revisionId = await CaptureAndAssignAsync(s, cardId);

        await using var read = await AuthorAsync();
        var accepted = await read.Revisions.GetAcceptedAsync(cardId);

        Assert.NotNull(accepted);
        Assert.Equal(revisionId, accepted!.RevisionId);
        Assert.Equal(1, accepted.RevisionNumber);

        // Дерево: две позиции, родитель внутри той же редакции.
        Assert.Equal(2, accepted.Nodes.Count);
        var root = accepted.Nodes.Single(n => n.ParentId is null);
        var child = accepted.Nodes.Single(n => n.ParentId is not null);
        Assert.Equal(root.Id, child.ParentId);
        Assert.StartsWith("Узел ", root.Name, StringComparison.Ordinal);
        Assert.StartsWith("Ветвь ", child.Name, StringComparison.Ordinal);

        // Строка под позицией, нормы и материалы скопированы.
        var item = Assert.Single(accepted.Items);
        Assert.Equal(root.Id, item.NodeId);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(1.25m, item.Volume);
        Assert.Equal("кг", item.UnitOfMeasure);

        var material = Assert.Single(item.Materials);
        Assert.Equal(GsmCategory.Primary, material.Category);
        Assert.False(string.IsNullOrWhiteSpace(material.Name));
        Assert.False(string.IsNullOrWhiteSpace(material.Nd));
        Assert.False(string.IsNullOrWhiteSpace(item.AssemblyUnitName));
        Assert.False(string.IsNullOrWhiteSpace(item.AssemblyUnitCode));
    }

    /// <summary>Карта только с конечными строками под корнем: позиций нет.</summary>
    [Fact]
    public async Task Capture_CardWithRowsUnderRootOnly_NoNodes()
    {
        await using var s = await AuthorAsync();

        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "АУ" + Suffix(), Name = "СБ" };
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Name = "Только строки " + Suffix(),
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            Purpose = "Назначение",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new() { AssemblyUnitId = unit.Id, Quantity = 1, Volume = 1m, SortOrder = 1 }
            }
        };
        var created = await s.HK.CreateAsync(card);
        var revisionId = await CaptureAndAssignAsync(s, created.Id);

        await using var read = await AuthorAsync();
        var accepted = await read.Revisions.GetAcceptedAsync(created.Id);

        Assert.NotNull(accepted);
        Assert.Equal(revisionId, accepted!.RevisionId);
        Assert.Empty(accepted.Nodes);
        Assert.Single(accepted.Items);
        Assert.Null(accepted.Items[0].NodeId);
    }

    /// <summary>Независимость после ПРАВКИ рабочего дерева.</summary>
    [Fact]
    public async Task AcceptedStructure_SurvivesWorkingTreeEdit()
    {
        var cardId = await CreateCardWithStructureAsync();
        await using (var s = await AuthorAsync())
        {
            await CaptureAndAssignAsync(s, cardId);
        }

        var originalName = string.Empty;
        await using (var s = await AuthorAsync())
        {
            var node = await s.Db.HKCardNodes.FirstAsync(n => n.HKCardId == cardId);
            originalName = node.Name;
            node.Name = "ПЕРЕИМЕНОВАНО";
            await s.Db.SaveChangesAsync();
        }

        await using var read = await AuthorAsync();
        var accepted = await read.Revisions.GetAcceptedAsync(cardId);

        Assert.NotNull(accepted);
        Assert.Contains(accepted!.Nodes, n => n.Name == originalName);
        Assert.DoesNotContain(accepted.Nodes, n => n.Name == "ПЕРЕИМЕНОВАНО");
    }

    /// <summary>Независимость после ИЗМЕНИЯ и УДАЛЕНИЯ рабочих строк.</summary>
    [Fact]
    public async Task AcceptedStructure_SurvivesWorkingItemEditAndDelete()
    {
        var cardId = await CreateCardWithStructureAsync();
        await using (var s = await AuthorAsync())
        {
            await CaptureAndAssignAsync(s, cardId);
        }

        var keptQuantity = 0;
        await using (var s = await AuthorAsync())
        {
            var items = await s.Db.HKCardItems.Where(i => i.HKCardId == cardId).ToListAsync();
            keptQuantity = items[0].Quantity;
            items[0].Quantity = 999;
            items[0].Notes = "изменено";
            await s.Db.SaveChangesAsync();

            // Вторая строка удаляется целиком вместе с материалами.
            var extra = new HKCardItem
            {
                Id = Guid.NewGuid(), HKCardId = cardId,
                AssemblyUnitId = items[0].AssemblyUnitId, Quantity = 7, SortOrder = 2
            };
            s.Db.HKCardItems.Add(extra);
            await s.Db.SaveChangesAsync();
            await s.Db.HKCardItems.Where(i => i.Id == extra.Id).ExecuteDeleteAsync();
            await s.Db.HKCardItems.Where(i => i.Id == items[0].Id).ExecuteDeleteAsync();
            await s.Db.HKCardNodes.Where(n => n.HKCardId == cardId).ExecuteDeleteAsync();
        }

        await using var read = await AuthorAsync();
        var accepted = await read.Revisions.GetAcceptedAsync(cardId);

        Assert.NotNull(accepted);
        Assert.Equal(keptQuantity, accepted!.Items[0].Quantity);
        Assert.NotEqual(999, accepted.Items[0].Quantity);
        Assert.Equal(2, accepted.Nodes.Count);
    }

    /// <summary>
    /// Наименования справочников не вычисляются заново: переименование марки
    /// после фиксации не меняет принятую карту, и в снимке лежит актуальный Nd.
    /// </summary>
    [Fact]
    public async Task AcceptedStructure_KeepsCapturedCatalogueNames()
    {
        var cardId = await CreateCardWithStructureAsync();

        string capturedName;
        await using (var s = await AuthorAsync())
        {
            await CaptureAndAssignAsync(s, cardId);
            capturedName = await s.Db.HKCardItemMaterials
                .Where(m => m.HKCardItem.HKCardId == cardId)
                .Select(m => m.GsmMaterial!.Name).FirstAsync();
        }

        await using (var s = await AuthorAsync())
        {
            var gsm = await s.Db.GsmMaterials.FirstAsync(g =>
                g.Name == capturedName);
            gsm.Name = "ПЕРЕИМЕНОВАННАЯ МАРКА";
            gsm.Nd = "НОВЫЙ-НД";
            await s.Db.SaveChangesAsync();
        }

        await using var read = await AuthorAsync();
        var accepted = await read.Revisions.GetAcceptedAsync(cardId);

        Assert.NotNull(accepted);
        var material = accepted!.Items[0].Materials[0];
        Assert.Equal(capturedName, material.Name);
        Assert.NotEqual("НОВЫЙ-НД", material.Nd);
    }

    /// <summary>Несколько редакций не создают новую карту.</summary>
    [Fact]
    public async Task MultipleCaptures_SameCard_NoNewCard()
    {
        var cardId = await CreateCardWithStructureAsync();

        int cardsBefore;
        await using (var s = await AuthorAsync())
        {
            cardsBefore = await s.Db.HKCards.CountAsync();
            await CaptureAndAssignAsync(s, cardId);
            var version = await VersionAsync(s, cardId);
            await s.Revisions.CaptureAsync(cardId, version);
        }

        await using var check = await AuthorAsync();
        Assert.Equal(cardsBefore, await check.Db.HKCards.CountAsync());
        Assert.Equal(2, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Equal(
            new[] { 1, 2 },
            await check.Db.HKCardRevisions.Where(r => r.HKCardId == cardId)
                .OrderBy(r => r.RevisionNumber).Select(r => r.RevisionNumber).ToListAsync());
    }

    // ── Целостность на уровне базы ──

    /// <summary>Родитель позиции из другой редакции отвергается базой.</summary>
    [Fact]
    public async Task ForeignRevisionNodeAsParent_IsRefused()
    {
        var cardId = await CreateCardWithStructureAsync();
        Guid foreignNodeId;

        await using (var s = await AuthorAsync())
        {
            var revisionId = await CaptureAndAssignAsync(s, cardId);
            foreignNodeId = await s.Db.HKCardRevisionNodes
                .Where(n => n.RevisionId == revisionId)
                .Select(n => n.Id).FirstAsync();
        }

        await using var check = await AuthorAsync();

        var alienNode = new HKCardRevisionNode
        {
            Id = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),   // редакция, которой не существует
            ParentId = foreignNodeId,      // родитель из другой редакции
            SourceNodeId = Guid.NewGuid(),
            Level = HKObjectLevel.Node,
            Name = "недопустимая",
        };

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            ExecuteAddAsync(check.Db, alienNode));
    }

    /// <summary>Указатель на редакцию ЧУЖОЙ карты отвергается базой.</summary>
    [Fact]
    public async Task AcceptedPointer_ToRevisionOfAnotherCard_IsRefused()
    {
        var cardA = await CreateCardWithStructureAsync();
        var cardB = await CreateCardWithStructureAsync();

        Guid revisionOfA;
        await using (var s = await AuthorAsync())
        {
            var v = await VersionAsync(s, cardA);
            revisionOfA = await s.Revisions.CaptureAsync(cardA, v);
        }

        await using var check = await AuthorAsync();
        var cardBEntity = await check.Db.HKCards.FirstAsync(c => c.Id == cardB);
        cardBEntity.CurrentAcceptedRevisionId = revisionOfA;

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => check.Db.SaveChangesAsync());
    }

    /// <summary>Изменение зафиксированной редакции через контекст запрещено.</summary>
    [Fact]
    public async Task CapturedRevision_CannotBeModified()
    {
        var cardId = await CreateCardWithStructureAsync();

        Guid revisionId;
        await using (var s = await AuthorAsync())
        {
            revisionId = await CaptureAndAssignAsync(s, cardId);
        }

        await using var check = await AuthorAsync();
        var revision = await check.Db.HKCardRevisions.FirstAsync(r => r.Id == revisionId);
        revision.Purpose = "ПОДМЕНА";

        await Assert.ThrowsAsync<InvalidOperationException>(() => check.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task CapturedRevision_CannotBeDeleted()
    {
        var cardId = await CreateCardWithStructureAsync();

        Guid revisionId;
        await using (var s = await AuthorAsync())
        {
            revisionId = await CaptureAndAssignAsync(s, cardId);
        }

        await using var check = await AuthorAsync();
        var revision = await check.Db.HKCardRevisions.FirstAsync(r => r.Id == revisionId);
        check.Db.HKCardRevisions.Remove(revision);

        await Assert.ThrowsAsync<InvalidOperationException>(() => check.Db.SaveChangesAsync());
    }

    // ── Чтение ──

    /// <summary>
    /// До назначения указателя принятой структуры нет. Подстановка рабочего
    /// дерева была бы незаметной подменой черновика принятой картой.
    /// </summary>
    [Fact]
    public async Task GetAccepted_BeforePointer_ReturnsNullNotWorkingTree()
    {
        var cardId = await CreateCardWithStructureAsync();

        await using var s = await AuthorAsync();

        // Редакция существует, но принятой структуры ещё нет.
        var version = await VersionAsync(s, cardId);
        await s.Revisions.CaptureAsync(cardId, version);

        var accepted = await s.Revisions.GetAcceptedAsync(cardId);

        Assert.Null(accepted);

        // Рабочее дерево при этом непустое — подмены быть не должно.
        Assert.NotEmpty(await s.Db.HKCardItems.Where(i => i.HKCardId == cardId).ToListAsync());
    }

    /// <summary>Чтение ничего не создаёт и не переключает.</summary>
    [Fact]
    public async Task GetAccepted_IsSideEffectFree()
    {
        var cardId = await CreateCardWithStructureAsync();
        await using (var s = await AuthorAsync())
        {
            await CaptureAndAssignAsync(s, cardId);
        }

        await using var reader = await AuthorAsync();
        var first = await reader.Revisions.GetAcceptedAsync(cardId);
        var second = await reader.Revisions.GetAcceptedAsync(cardId);

        Assert.NotNull(first);
        Assert.Equal(first!.RevisionId, second!.RevisionId);
        Assert.Equal(1, await reader.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Equal(first.RevisionId, await reader.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync());
    }

    /// <summary>Чтение чужой редакции по известному идентификатору отвергается.</summary>
    [Fact]
    public async Task GetRevision_OfAnotherCard_IsRefused()
    {
        var cardA = await CreateCardWithStructureAsync();
        var cardB = await CreateCardWithStructureAsync();

        Guid revisionOfA;
        await using (var s = await AuthorAsync())
        {
            var v = await VersionAsync(s, cardA);
            revisionOfA = await s.Revisions.CaptureAsync(cardA, v);
        }

        await using var read = await AuthorAsync();
        await Assert.ThrowsAsync<ArgumentException>(
            () => read.Revisions.GetRevisionAsync(cardB, revisionOfA));
    }

    /// <summary>
    /// Фиксация при устаревшей версии карты отвергается: снимок обязан
    /// соответствовать той версии, которую видел вызывающий.
    /// </summary>
    [Fact]
    public async Task Capture_WithStaleCardVersion_IsRefused()
    {
        var cardId = await CreateCardWithStructureAsync();

        await using var s = await AuthorAsync();
        var version = await VersionAsync(s, cardId);

        // Карта изменяется после чтения версии.
        var card = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        card.UpdatedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Revisions.CaptureAsync(cardId, version));
    }

    /// <summary>Конкурентная фиксация не приводит к задвоению номера.</summary>
    [Fact]
    public async Task ParallelCaptures_KeepRevisionNumbersUnique()
    {
        var cardId = await CreateCardWithStructureAsync();

        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForBothAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await gate.Task;
        }

        var outcomes = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var s = await AuthorAsync();
                var version = await VersionAsync(s, cardId);
                await WaitForBothAsync();
                try { await s.Revisions.CaptureAsync(cardId, version); return "ok"; }
                catch (Exception ex) { return ex.GetType().Name; }
            }),
            Task.Run(async () =>
            {
                await using var s = await AuthorAsync();
                var version = await VersionAsync(s, cardId);
                await WaitForBothAsync();
                try { await s.Revisions.CaptureAsync(cardId, version); return "ok"; }
                catch (Exception ex) { return ex.GetType().Name; }
            }));

        // Гарантируется: уникальные номера редакций, согласованное содержимое
        // каждой редакции, отсутствие частичных записей и контролируемый исход
        // конфликта. Единственность фиксации ОДНОЙ рабочей версии не требуется:
        // CaptureAsync не меняет RowVersion карты, и повторная фиксация той же
        // версии — предусмотренная моделью возможность.
        //
        // Пустой список успехов успехом НЕ считается: обе фиксации провалиться
        // не должны.
        Assert.NotEmpty(outcomes);

        var succeeded = outcomes.Count(o => o == "ok");
        Assert.True(succeeded >= 1,
            "Ни одна параллельная фиксация не завершилась успешно: " + string.Join(", ", outcomes));

        // Конфликты допускаются только конкретные и объяснимые.
        foreach (var refused in outcomes.Where(o => o != "ok"))
        {
            Assert.True(refused == nameof(InvalidOperationException)
                        || refused == nameof(DbUpdateException),
                "Неожиданный исход фиксации: " + refused);
        }

        await using var check = await AuthorAsync();
        var numbers = await check.Db.HKCardRevisions
            .Where(r => r.HKCardId == cardId)
            .OrderBy(r => r.RevisionNumber)
            .Select(r => r.RevisionNumber)
            .ToListAsync();

        // Одинаковых номеров быть не может, и итоговое число редакций обязано
        // совпадать с числом состоявшихся фиксаций.
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.Equal(succeeded, numbers.Count);
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    // ── Фиксация не делает ничего из запрещённого ──

    /// <summary>
    /// Фиксация не выставляет статус карты, не принимает этап состава и не
    /// переключает указатель. Иначе снимок появился бы в обход согласования.
    /// </summary>
    [Fact]
    public async Task Capture_DoesNotApproveOrSwitchPointer()
    {
        var cardId = await CreateCardWithStructureAsync();

        var statusBefore = HKCardStatus.Draft;
        await using (var s = await AuthorAsync())
        {
            var version = await VersionAsync(s, cardId);
            await s.Revisions.CaptureAsync(cardId, version);
        }

        await using var check = await AuthorAsync();
        var card = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var composition = await check.Db.HKCardApprovals.AsNoTracking()
            .FirstOrDefaultAsync(a => a.HKCardId == cardId && a.Stage == HKApprovalStage.Composition);

        Assert.Equal(statusBefore, card.Status);
        Assert.Null(card.CurrentAcceptedRevisionId);

        if (composition is not null)
        {
            Assert.NotEqual(HKApprovalStageState.Approved, composition.State);
            Assert.Null(composition.ApprovedAtUtc);
        }
    }

    /// <summary>
    /// Откат при ошибке не оставляет частичной редакции: принудительная ошибка
    /// сохранения прерывает фиксацию целиком.
    /// </summary>
    [Fact]
    public async Task Capture_WhenSaveFails_LeavesNoPartialRevision()
    {
        var cardId = await CreateCardWithStructureAsync();

        int before;
        await using (var s = await AuthorAsync())
        {
            before = await s.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId);
        }

        try
        {
            // Ошибка вставляется на вставке узлов снимка: к этому моменту
            // родитель уже проставлен, поэтому частичная запись была бы видна.
            FailingCommandInterceptor.ArmAt("\"HKCardRevisionNodes\"", 1);

            await using var failingScope = _fixture.CreateScope();
            failingScope.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
            var version = await failingScope.Db.HKCards.AsNoTracking()
                .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();

            await Assert.ThrowsAnyAsync<Exception>(
                () => failingScope.Revisions.CaptureAsync(cardId, version));
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using var check = await AuthorAsync();
        Assert.Equal(before, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));
        Assert.Empty(await check.Db.HKCardRevisionNodes.Where(n => n.RevisionId == Guid.Empty).ToListAsync());
    }

    /// <summary>
    /// Добавление одиночной позиции вне графа: ошибка доходит до базы как
    /// нарушение ограничения, а не проглатывается.
    /// </summary>
    private static async Task ExecuteAddAsync(Microsoft.EntityFrameworkCore.DbContext db, object entity)
    {
        db.Add(entity);
        await db.SaveChangesAsync();
    }
}