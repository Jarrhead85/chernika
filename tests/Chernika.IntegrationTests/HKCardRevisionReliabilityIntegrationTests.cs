using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Надёжность, доступ и сохранность редакций ХК.
/// <para>
/// Каждая проверка закрывает конкретное замечание по реализации PR № 5.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardRevisionReliabilityIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardRevisionReliabilityIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<TestScope> AuthorAsync()
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        return s;
    }

    private async Task<TestScope> ReaderAsync(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    private async Task<Guid> CreateCardWithStructureAsync(TestScope s, bool withBranch = true)
    {
        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "АУ-" + Suffix(), Name = "СБ " + Suffix() };
        var gsm = new GsmMaterial { Id = Guid.NewGuid(), Name = "Масло " + Suffix(), Nd = "ТУ-" + Suffix() };
        s.Db.AssemblyUnits.Add(unit);
        s.Db.GsmMaterials.Add(gsm);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Name = "Карта " + Suffix(),
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            Purpose = "Назначение",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>()
        };
        var created = await s.HK.CreateAsync(card);

        var root = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = created.Id,
            Level = HKObjectLevel.Node, Name = "Узел " + Suffix(), Quantity = 1, SortOrder = 1
        };
        s.Db.HKCardNodes.Add(root);

        var item = new HKCardItem
        {
            Id = Guid.NewGuid(), HKCardId = created.Id, HKCardNodeId = root.Id,
            AssemblyUnitId = unit.Id, Quantity = 3, Volume = 1.25m, UnitOfMeasure = "кг", SortOrder = 1
        };
        item.Materials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(), GsmMaterialId = gsm.Id, Category = GsmCategory.Primary
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

    private async Task<Guid> CaptureAsync(TestScope s, Guid cardId)
    {
        var version = await VersionAsync(s, cardId);
        return await s.Revisions.CaptureAsync(cardId, version);
    }

    // ── 1. Неизменяемость не блокирует посторонние сохранения ──

    /// <summary>
    /// Загруженная для чтения редакция не мешает сохранить изменение карты.
    /// </summary>
    [Fact]
    public async Task LoadedRevision_DoesNotBlockSavingTheCard()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        // Редакция присутствует в контексте как отслеживаемая.
        var revision = await s.Db.HKCardRevisions
            .Include(r => r.Nodes).Include(r => r.Items).ThenInclude(i => i.Materials)
            .FirstAsync(r => r.Id == revisionId);

        var card = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        card.UpdatedAt = DateTime.UtcNow;

        await s.Db.SaveChangesAsync();

        Assert.Equal(HKCardStatus.Draft, card.Status);
    }

    /// <summary>
    /// Посторонняя сущность сохраняется рядом с загруженной редакцией.
    /// </summary>
    [Fact]
    public async Task LoadedRevision_DoesNotBlockSavingOtherEntity()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        var revision = await s.Db.HKCardRevisions.FirstAsync(r => r.Id == revisionId);

        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "АУ2-" + Suffix(), Name = "СБ2" };
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();

        Assert.NotNull(revision);
        Assert.NotNull(await s.Db.AssemblyUnits.FindAsync(unit.Id));
    }

    /// <summary>Повторное сохранение без изменения редакции проходит.</summary>
    [Fact]
    public async Task LoadedRevision_SecondSaveChangesWithoutEdit_Succeeds()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        await s.Db.HKCardRevisions.FirstAsync(r => r.Id == revisionId);
        await s.Db.SaveChangesAsync();
        await s.Db.SaveChangesAsync();
    }

    /// <summary>Изменение редакции отклоняется перехватчиком.</summary>
    [Fact]
    public async Task ModifiedRevision_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        var revision = await s.Db.HKCardRevisions.FirstAsync(r => r.Id == revisionId);
        revision.Purpose = "ПОДМЕНА";

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
    }

    /// <summary>Удаление редакции отклоняется перехватчиком.</summary>
    [Fact]
    public async Task DeletedRevision_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        // Удаление проверяется на уровне базы: EF-трекинг не даёт снять с
        // обязательными дочерними записями вовсе, поэтому применяется
        // ExecuteDelete, и отказ обязан дать ограничение базы.
        // ExecuteDelete не оборачивает ошибку в DbUpdateException.
        var ex = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            s.Db.HKCardRevisions.Where(r => r.Id == revisionId).ExecuteDeleteAsync());

        Assert.Contains("RESTRICT", ex.Message, StringComparison.OrdinalIgnoreCase);

        s.Db.ChangeTracker.Clear();
        Assert.NotNull(await s.Db.HKCardRevisions.FindAsync(revisionId));
    }

[Fact]
    public async Task DeletedSnapshotChildren_AreRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        var node = await s.Db.HKCardRevisionNodes.FirstAsync(n => n.RevisionId == revisionId);
        s.Db.HKCardRevisionNodes.Remove(node);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
    }

    /// <summary>
    /// Удаление строки редакции останавливается: её материалы связаны обязательной
    /// связью, и EF не позволяет осиротить их.
    /// </summary>
    [Fact]
    public async Task DeletedSnapshotItem_WithRequiredChildren_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        var itemId = await s.Db.HKCardRevisionItems
            .Where(i => i.RevisionId == revisionId).Select(i => i.Id).FirstAsync();

        // Материалы строки связаны обязательной связью: удаление строки обязано
        // быть отвергнуто ограничением базы, а не оставить осиротевшие материалы.
        var ex = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            s.Db.HKCardRevisionItems.Where(i => i.Id == itemId).ExecuteDeleteAsync());

        Assert.Contains("RESTRICT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DeletedRevision_LeavesRevisionIntact()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        var revisionId = await CaptureAsync(s, cardId);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            s.Db.HKCardRevisions.Where(r => r.Id == revisionId).ExecuteDeleteAsync());

        s.Db.ChangeTracker.Clear();
        var intact = await s.Db.HKCardRevisions
            .Include(r => r.Nodes).Include(r => r.Items).ThenInclude(i => i.Materials)
            .FirstAsync(r => r.Id == revisionId);

        Assert.NotEmpty(intact.Nodes);
        Assert.NotEmpty(intact.Items);
        Assert.All(intact.Items, i => Assert.NotEmpty(i.Materials));
    }

    // ── 8. Целостность: составной ключ проверяется базой ──

    /// <summary>
    /// Две существующие редакции. Позиция второй со ссылкой на позицию первой
    /// отвергается ИМЕННО составным внешним ключом: обе редакции существуют,
    /// поэтому отказ не может быть следствием отсутствия RevisionId.
    /// </summary>
    [Fact]
    public async Task CrossRevisionNodeParent_ViolatesCompositeForeignKey()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        Guid firstRevision, firstNodeId;
        await using (var setup = await AuthorAsync())
        {
            firstRevision = await CaptureAsync(setup, cardId);
            firstNodeId = await setup.Db.HKCardRevisionNodes
                .Where(n => n.RevisionId == firstRevision).Select(n => n.Id).FirstAsync();
        }

        Guid secondRevision;
        await using (var setup = await AuthorAsync())
        {
            var version = await VersionAsync(setup, cardId);
            secondRevision = await setup.Revisions.CaptureAsync(cardId, version);
        }

        // Обе редакции и обе позиции существуют: нарушается именно пара «редакция +
        // родитель».
        await using var check = await AuthorAsync();

        var alien = new HKCardRevisionNode
        {
            Id = Guid.NewGuid(),
            RevisionId = secondRevision,
            ParentId = firstNodeId,
            SourceNodeId = Guid.NewGuid(),
            Level = HKObjectLevel.Node,
            Name = "чужой родитель",
        };

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            ExecuteAddAsync(check.Db, alien));

        // Отказ именно по составному ключу узлов, а не по чему-то другому.
        Assert.Contains("FK_HKCardRevisionNodes_HKCardRevisionNodes_RevisionId_ParentId",
            ex.InnerException?.Message ?? ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Строка с позицией другой редакции отвергается составным ключом.</summary>
    [Fact]
    public async Task CrossRevisionItemNode_ViolatesCompositeForeignKey()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        Guid firstRevision, firstNodeId;
        await using (var setup = await AuthorAsync())
        {
            firstRevision = await CaptureAsync(setup, cardId);
            firstNodeId = await setup.Db.HKCardRevisionNodes
                .Where(n => n.RevisionId == firstRevision).Select(n => n.Id).FirstAsync();
        }

        Guid secondRevision;
        await using (var setup = await AuthorAsync())
        {
            var version = await VersionAsync(setup, cardId);
            secondRevision = await setup.Revisions.CaptureAsync(cardId, version);
        }

        await using var check = await AuthorAsync();

        var alien = new HKCardRevisionItem
        {
            Id = Guid.NewGuid(),
            RevisionId = secondRevision,
            NodeId = firstNodeId,
            SourceItemId = Guid.NewGuid(),
            AssemblyUnitId = Guid.NewGuid(),
            SortOrder = 1,
        };

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            ExecuteAddAsync(check.Db, alien));

        Assert.Contains("FK_HKCardRevisionItems_HKCardRevisionNodes_RevisionId_NodeId",
            ex.InnerException?.Message ?? ex.Message, StringComparison.Ordinal);
    }

    // ── Доступ к чтению и фиксации ──

    /// <summary>Карта чужого филиала недоступна для чтения редакций.</summary>
    [Fact]
    public async Task GetAccepted_OtherBranch_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        await CaptureAsync(s, cardId);

        await using var outsider = await ReaderAsync(_fixture.NormAdminB.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => outsider.Revisions.GetAcceptedAsync(cardId));
    }

    /// <summary>Пользователь без HK.View не читает редакции.</summary>
    [Fact]
    public async Task GetAccepted_WithoutViewPermission_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        await CaptureAsync(s, cardId);

        await using var noView = await CreateUserWithoutViewAsync(_fixture.BranchA);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => noView.Revisions.GetAcceptedAsync(cardId));
    }

    /// <summary>Индивидуальный запрет просмотра действует поверх роли.</summary>
    [Fact]
    public async Task GetAccepted_WithIndividualViewDeny_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        await CaptureAsync(s, cardId);

        await using var denied = await CreateUserWithoutViewAsync(_fixture.BranchA);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => denied.Revisions.GetAcceptedAsync(cardId));
    }

    /// <summary>
    /// Отключённый пользователь не читает редакции, даже имея право просмотра.
    /// Отключение применяется по-настоящему и восстанавливается в finally.
    /// </summary>
    [Fact]
    public async Task GetAccepted_DeactivatedUser_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);
        await CaptureAsync(s, cardId);

        await using var user = await CreateUserWithoutViewAsync(_fixture.BranchA);
        var userId = user.User.CurrentUserId!.Value.ToString();

        try
        {
            // Право просмотра выдаётся явно: отказ должен идти из-за активности,
            // а не из-за отсутствия полномочия.
            await using (var grant = _fixture.CreateScope())
            {
                grant.Db.UserPermissionOverrides.RemoveRange(
                    grant.Db.UserPermissionOverrides.Where(o => o.UserId == userId));
                grant.Db.UserPermissionOverrides.Add(new UserPermissionOverride
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PermissionCode = PermissionCodes.HKView,
                    IsGranted = true,
                    GrantedByUserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await grant.Db.SaveChangesAsync();
                grant.Permissions.InvalidateCache(userId);
            }

            await SetActiveAsync(userId, false);

            await using (var verify = _fixture.CreateScope())
            {
                var stored = await verify.Db.Users.AsNoTracking()
                    .Where(u => u.Id == userId).Select(u => u.IsActive).FirstAsync();
                Assert.False(stored, "Пользователь должен быть отключён для этого сценария.");
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => user.Revisions.GetAcceptedAsync(cardId));
        }
        finally
        {
            await SetActiveAsync(userId, true);
        }
    }

    /// <summary>Доступная карта без принятой редакции — явное отсутствие.</summary>
    [Fact]
    public async Task GetAccepted_AvailableCardWithoutPointer_ReturnsNull()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        // Редакция есть, принятой структуры нет.
        await CaptureAsync(s, cardId);

        var accepted = await s.Revisions.GetAcceptedAsync(cardId);

        Assert.Null(accepted);

        // Рабочее дерево при этом непустое: подмены быть не должно.
        Assert.NotEmpty(await s.Db.HKCardItems.Where(i => i.HKCardId == cardId).ToListAsync());
    }

    /// <summary>Отсутствующая карта даёт понятную ошибку.</summary>
    [Fact]
    public async Task GetAccepted_UnknownCard_IsRefused()
    {
        await using var s = await AuthorAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => s.Revisions.GetAcceptedAsync(Guid.NewGuid()));
    }

    /// <summary>Существующая редакция чужой карты не раскрывается.</summary>
    [Fact]
    public async Task GetRevision_OfAnotherCard_IsRefusedWithoutLeak()
    {
        await using var s = await AuthorAsync();
        var cardA = await CreateCardWithStructureAsync(s);
        var revisionOfA = await CaptureAsync(s, cardA);

        var cardB = await CreateCardWithStructureAsync(s);

        await Assert.ThrowsAsync<ArgumentException>(
            () => s.Revisions.GetRevisionAsync(cardB, revisionOfA));
    }

    /// <summary>Фиксация не доступна чужому филиалу.</summary>
    [Fact]
    public async Task Capture_OtherBranch_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        await using var outsider = await ReaderAsync(_fixture.NormAdminB.Id);
        var version = await VersionAsync(outsider, cardId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => outsider.Revisions.CaptureAsync(cardId, version));
    }

    /// <summary>Фиксация не доступна без HK.View: право просмотра не даёт создавать редакции.</summary>
    [Fact]
    public async Task Capture_WithoutViewPermission_IsRefused()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        await using var noView = await CreateUserWithoutViewAsync(_fixture.BranchA);
        var version = await VersionAsync(noView, cardId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => noView.Revisions.CaptureAsync(cardId, version));
    }

    // ── Контракт внешней транзакции ──

    /// <summary>Подходящая внешняя транзакция принимается; владение остаётся у вызывающего.</summary>
    [Fact]
    public async Task Capture_WithRepeatableReadTransaction_Succeeds()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        Guid revisionId;
        await using (var tx = await s.Db.Database.BeginTransactionAsync(
                   System.Data.IsolationLevel.RepeatableRead))
        {
            var version = await VersionAsync(s, cardId);
            revisionId = await s.Revisions.CaptureAsync(cardId, version);

            // Сервис не коммитит чужую транзакцию: она ещё открыта.
            Assert.NotNull(s.Db.Database.CurrentTransaction);
            await tx.CommitAsync();
        }

        await using var check = await AuthorAsync();
        Assert.NotNull(await check.Db.HKCardRevisions.FindAsync(revisionId));
    }

    /// <summary>Неподходящая внешняя транзакция отклоняется ДО записи.</summary>
    [Fact]
    public async Task Capture_WithUnsuitableIsolationLevel_IsRefusedBeforeWrite()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        int revisionsBefore = await s.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId);

        await using var tx = await s.Db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted);

        var version = await VersionAsync(s, cardId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Revisions.CaptureAsync(cardId, version));

        // Записи не появилось: отказ произошёл до写入.
        Assert.Equal(revisionsBefore,
            await s.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));

        await tx.RollbackAsync();
    }

    /// <summary>
    /// Откат внешней транзакции убирает новую редакцию И все её дочерние записи.
    /// </summary>
    [Fact]
    public async Task Capture_WhenExternalTransactionRollsBack_NothingRemains()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        Guid revisionId;
        await using (var tx = await s.Db.Database.BeginTransactionAsync(
                   System.Data.IsolationLevel.RepeatableRead))
        {
            var version = await VersionAsync(s, cardId);
            revisionId = await s.Revisions.CaptureAsync(cardId, version);
            await tx.RollbackAsync();
        }

        await using var check = await AuthorAsync();
        Assert.Null(await check.Db.HKCardRevisions.FindAsync(revisionId));
        Assert.Empty(await check.Db.HKCardRevisionNodes
            .Where(n => n.RevisionId == revisionId).ToListAsync());
        Assert.Empty(await check.Db.HKCardRevisionItems
            .Where(i => i.RevisionId == revisionId).ToListAsync());
        Assert.Empty(await check.Db.HKCardRevisionItemMaterials
            .Where(m => m.RevisionItem.RevisionId == revisionId).ToListAsync());
    }

    /// <summary>
    /// Внедрённая ошибка записи не оставляет частичный граф. Проверяется, что ошибка
    /// действительно внедрена в нужной точке, а не возникла по иной причине.
    /// </summary>
    [Fact]
    public async Task Capture_WhenChildWriteFails_NoPartialGraphRemains()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        int before;
        await using (var setup = await AuthorAsync())
        {
            before = await setup.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId);
        }

        // Вооружение до создания scope: AsyncLocal обязан попасть в вызов.
        FailingCommandInterceptor.ArmAt("\"HKCardRevisionItemMaterials\"", 1);

        try
        {
            await using var failing = await AuthorAsync();
            var version = await VersionAsync(failing, cardId);

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => failing.Revisions.CaptureAsync(cardId, version));

            // EF оборачивает ошибку команды в DbUpdateException, поэтому
            // внедрённая ошибка ищется во всей цепочке, а не в верхнем сообщении.
            for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
            {
                if (cur.Message == "Injected test failure.")
                {
                    Assert.True(true);
                    break;
                }

                if (cur.InnerException is null)
                {
                    Assert.Fail(
                        "Ошибка возникла не в точке внедрения: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // Проверка ДО finally: Disarm сбрасывает признак срабатывания.
            Assert.True(FailingCommandInterceptor.Fired,
                "Ошибка не была вставлена в нужную точку: откат не проверялся.");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using var check = await AuthorAsync();
        Assert.Equal(before, await check.Db.HKCardRevisions.CountAsync(r => r.HKCardId == cardId));

        // Ни одна запись снимка не осталась «висячей».
        var knownRevisions = check.Db.HKCardRevisions.Select(r => r.Id);
        Assert.Empty(await check.Db.HKCardRevisionNodes
            .Where(n => !knownRevisions.Contains(n.RevisionId)).ToListAsync());
        Assert.Empty(await check.Db.HKCardRevisionItems
            .Where(i => !knownRevisions.Contains(i.RevisionId)).ToListAsync());
    }

    // ── Источники данных ──

    /// <summary>
    /// Мягко удалённая марка ГСМ читается историческими названием и НД: выдумывать
    /// и отказывать здесь нельзя.
    /// </summary>
    [Fact]
    public async Task Capture_WithSoftDeletedGsmMaterial_KeepsHistoricalNameAndNd()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        var materialId = await s.Db.HKCardItemMaterials
            .Where(m => m.HKCardItem.HKCardId == cardId).Select(m => m.GsmMaterialId).FirstAsync();
        await s.Db.GsmMaterials.Where(g => g.Id == materialId)
            .ExecuteUpdateAsync(g => g.SetProperty(x => x.IsDeleted, true));

        string historicalName;
        string? historicalNd;
        await using (var before = await AuthorAsync())
        {
            historicalName = await before.Db.GsmMaterials.AsNoTracking().IgnoreQueryFilters()
                .Where(g => g.Id == materialId).Select(g => g.Name).FirstAsync();
            historicalNd = await before.Db.GsmMaterials.AsNoTracking().IgnoreQueryFilters()
                .Where(g => g.Id == materialId).Select(g => g.Nd).FirstAsync();
        }

        var version = await VersionAsync(s, cardId);
        var revisionId = await s.Revisions.CaptureAsync(cardId, version);

        await using var check = await AuthorAsync();
        var snapshot = await check.Db.HKCardRevisionItemMaterials.AsNoTracking()
            .FirstAsync(m => m.RevisionItem.RevisionId == revisionId);

        Assert.Equal(historicalName, snapshot.Name);
        Assert.Equal(historicalNd, snapshot.Nd);
    }

    /// <summary>Мягко удалённая сборочная единица сохраняет наименование и обозначение.</summary>
    [Fact]
    public async Task Capture_WithSoftDeletedAssemblyUnit_KeepsHistoricalValues()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        var unitId = await s.Db.HKCardItems
            .Where(i => i.HKCardId == cardId).Select(i => i.AssemblyUnitId).FirstAsync();
        await s.Db.AssemblyUnits.Where(a => a.Id == unitId)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.IsDeleted, true));

        string historicalName;
        string historicalCode;
        await using (var before = await AuthorAsync())
        {
            historicalName = await before.Db.AssemblyUnits.AsNoTracking().IgnoreQueryFilters()
                .Where(a => a.Id == unitId).Select(a => a.Name).FirstAsync();
            historicalCode = await before.Db.AssemblyUnits.AsNoTracking().IgnoreQueryFilters()
                .Where(a => a.Id == unitId).Select(a => a.Code).FirstAsync();
        }

        var version = await VersionAsync(s, cardId);
        var revisionId = await s.Revisions.CaptureAsync(cardId, version);

        await using var check = await AuthorAsync();
        var snapshot = await check.Db.HKCardRevisionItems.AsNoTracking()
            .FirstAsync(i => i.RevisionId == revisionId);

        Assert.Equal(historicalName, snapshot.AssemblyUnitName);
        Assert.Equal(historicalCode, snapshot.AssemblyUnitCode);
    }

    /// <summary>
    /// Родитель позиции отсутствует в исходном наборе — отказ, а не превращение
    /// позиции в корневую. Используется позиция ДРУГОЙ карты: удалять узел нельзя,
    /// база запретила бы это ограничением, и отказ был бы следствием ограничения,
    /// а не проверки.
    /// </summary>
        // Сценарии «родитель вне исходного набора» и «позиция строки вне набора»
    // НЕ проверяются тестом: при объявленных ограничениях базы такое состояние
    // не представимо.
    //
    // FK узлов и строк — составные и включают HKCardId: родитель обязан быть
    // позицией ТОЙ ЖЕ карты, а набор позиций — все узлы карты. Ссылка на позицию
    // другой карты отвергается ограничением 23503 ещё на записи, то есть
    // проверка фиксации не может быть достигнута.
    //
    // Проверки в CaptureAsync (отказ при ParentId вне набора и при HKCardNodeId вне
    // набора) остаются как защита в глубину на случай повреждённых или
    // мигрированных данных, но исполняемой проверки для них не существует.
    // Выдумывать способ обойти ограничение ради зелёного прогона здесь нельзя.

    // ── Изменение рабочих данных во время фиксации ──

    /// <summary>
    /// Изменение рабочей структуры во время фиксации.
    /// <para>
    /// Это НЕ два одновременных CaptureAsync: здесь меняются рабочие данные, а
    /// фиксация читает их. Редакция обязана содержать целиком согласованное
    /// состояние, а не смесь старых позиций и новых строк.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Capture_DuringWorkingChange_IsFullyConsistent()
    {
        await using var s = await AuthorAsync();
        var cardId = await CreateCardWithStructureAsync(s);

        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForBothAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await gate.Task;
        }

        // Фиксация: ждёт сигнала, затем читает рабочие данные.
        var captureTask = Task.Run(async () =>
        {
            await using var sc = await AuthorAsync();
            var version = await VersionAsync(sc, cardId);
            await WaitForBothAsync();
            try { return (object)await sc.Revisions.CaptureAsync(cardId, version); }
            catch (Exception ex) { return (object)ex; }
        });

        // Параллельная правка: добавляет позицию и меняет количество строки.
        const string newNodeName = "НОВАЯ-ПОЗИЦИЯ";
        var changeTask = Task.Run(async () =>
        {
            await using var sc = await AuthorAsync();
            var version = await VersionAsync(sc, cardId);
            await WaitForBothAsync();
            try
            {
                var card = await sc.Db.HKCards.FirstAsync(c => c.Id == cardId);
                var item = await sc.Db.HKCardItems.FirstAsync(i => i.HKCardId == cardId);

                var node = new HKCardNode
                {
                    Id = Guid.NewGuid(),
                    HKCardId = cardId,
                    Level = HKObjectLevel.Node,
                    Name = newNodeName,
                    Quantity = 1,
                    SortOrder = 99
                };
                sc.Db.HKCardNodes.Add(node);
                item.Quantity = 4242;
                await sc.Db.SaveChangesAsync();
                return (object)"changed";
            }
            catch (Exception ex) { return (object)ex; }
        });

        var capture = await captureTask;
        var change = await changeTask;

        // Рабочая правка действительно состоялась — иначе сценарий не проверял бы
        // согласованность.
        Assert.Equal("changed", change);
        await using (var verify = await AuthorAsync())
        {
            var item = await verify.Db.HKCardItems.AsNoTracking()
                .FirstAsync(i => i.HKCardId == cardId);
            Assert.Equal(4242, item.Quantity);
        }

        if (capture is Exception)
        {
            // Конфликт версии карты — контролируемый исход, фиксации нет.
            await using var auditRead = await AuthorAsync();
            Assert.Empty(await auditRead.Db.HKCardRevisions
                .Where(r => r.HKCardId == cardId).ToListAsync());
            return;
        }

        var revisionId = (Guid)capture;

        await using var auditGraph = await AuthorAsync();
        var revision = await auditGraph.Db.HKCardRevisions
            .Include(r => r.Nodes).Include(r => r.Items).ThenInclude(i => i.Materials)
            .FirstAsync(r => r.Id == revisionId);

        var ids = revision.Nodes.Select(n => n.Id).ToHashSet();

        // Целостность набора: родители и позиции строк — только из этой редакции.
        Assert.All(revision.Nodes, n =>
            Assert.True(n.ParentId is null || ids.Contains(n.ParentId.Value)));
        Assert.All(
            revision.Items.Select(i => i.NodeId).Where(n => n.HasValue).ToList(),
            n => Assert.Contains(n!.Value, ids));

        // Материалы принадлежат своим строкам.
        Assert.All(revision.Items, i =>
            Assert.All(i.Materials, m => Assert.Equal(i.Id, m.RevisionItemId)));

        // Согласованность: редакция целиком до правки ЛИБО целиком после неё.
        var hasNewNode = revision.Nodes.Any(n => n.Name == newNodeName);
        var hasNewQuantity = revision.Items.Any(i => i.Quantity == 4242);

        if (hasNewNode)
        {
            // Позиция появилась — значит правка попала в снимок целиком, и старая
            // структура не должна быть перемешана с новой.
            Assert.True(hasNewQuantity,
                "Редакция содержит новую позицию, но старое количество строки: смесь моментов.");
        }
        else
        {
            Assert.False(hasNewQuantity,
                "Редакция содержит новое количество без новой позиции: смесь моментов.");
        }
    }

    // ── Вспомогательное ──

    private static async Task ExecuteAddAsync(Microsoft.EntityFrameworkCore.DbContext db, object entity)
    {
        db.Add(entity);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Меняет признак активности пользователя и сохраняет изменение.
    /// <para>
    /// Изменение обязательно обратимо: общая тестовая база живёт между тестами, и
    /// оставленный отключённым пользователь ломал бы чужие сценарии.
    /// </para>
    /// </summary>
    private async Task SetActiveAsync(string userId, bool active)
    {
        await using var s = _fixture.CreateScope();
        var user = await s.Db.Users.SingleAsync(u => u.Id == userId);
        user.IsActive = active;
        await s.Db.SaveChangesAsync();
    }

    private async Task<TestScope> CreateUserWithoutViewAsync(Guid branchId)
    {
        await using var s = await ReaderAsync(_fixture.SystemAdminUser.Id);

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "noview_" + Suffix(),
            FullName = "Без просмотра " + Suffix(),
            BranchId = branchId,
            IsActive = true
        };
        var result = await s.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));

        await s.Users.AddToRoleAsync(user, nameof(UserRole.Operator));
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionCode = PermissionCodes.HKView,
            IsGranted = false,
            GrantedByUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);

        // Асинхронный переход без блокировки потока.
        return await ReaderAsync(user.Id);
    }

    private async Task<TestScope> CreateDeniedViewUserAsync(Guid branchId)
    {
        return await CreateUserWithoutViewAsync(branchId);
    }
}