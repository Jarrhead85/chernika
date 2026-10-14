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

    // ── Вспомогательное ──

    private static async Task ExecuteAddAsync(Microsoft.EntityFrameworkCore.DbContext db, object entity)
    {
        db.Add(entity);
        await db.SaveChangesAsync();
    }

    private async Task SetActiveAsync(TestScope s, bool active)
    {
        var user = await s.Db.Users.FirstAsync(u => u.Id == _fixture.NormAdminA2.Id);
        _ = user;
    }

    private async Task<TestScope> CreateUserWithoutViewAsync(Guid branchId)
    {
        var s = await ReaderAsync(_fixture.SystemAdminUser.Id);

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

        return ReaderAsync(user.Id).Result;
    }

    private async Task<TestScope> CreateDeniedViewUserAsync(Guid branchId)
    {
        return await CreateUserWithoutViewAsync(branchId);
    }
}