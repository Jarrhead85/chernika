using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardVersioningIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardVersioningIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateNewVersionAsync_ApprovedSource_CreatesLinkedDraftCopy()
    {
        await using var s = _fixture.CreateScope();
        var sourceId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newCardId, error) = await s.HK.CreateNewVersionAsync(sourceId);
        Assert.True(success, error);
        Assert.NotNull(newCardId);

        var newCard = await s.Db.HKCards
            .AsNoTracking()
            .Include(c => c.Items).ThenInclude(i => i.Materials)
            .Include(c => c.ParentComponents)
            .Include(c => c.MilitaryBranches)
            .SingleAsync(c => c.Id == newCardId.Value);

        Assert.Equal(HKCardStatus.Draft, newCard.Status);
        Assert.Equal(sourceId, newCard.SupersedesHKCardId);
        Assert.NotEmpty(newCard.Version);

        var source = await s.Db.HKCards.AsNoTracking()
            .Include(c => c.Items)
            .SingleAsync(c => c.Id == sourceId);
        Assert.Equal(source.Code, newCard.Code);
        Assert.Equal(source.ObjectLevel, newCard.ObjectLevel);
        Assert.Equal(source.NodeId, newCard.NodeId);
        Assert.Equal(source.BranchId, newCard.BranchId);
        Assert.Equal(source.Items.Count, newCard.Items.Count);
    }

    [Fact]
    public async Task CreateNewVersionAsync_DraftSource_Fails()
    {
        await using var s = _fixture.CreateScope();
        var draftId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newCardId, error) = await s.HK.CreateNewVersionAsync(draftId);
        Assert.False(success);
        Assert.Null(newCardId);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task CreateNewVersionAsync_GeneratesUniqueVersion_ForSameMonth()
    {
        await using var s = _fixture.CreateScope();
        var sourceId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success1, id1, error1) = await s.HK.CreateNewVersionAsync(sourceId);
        Assert.True(success1, error1);

        await s.HK.ChangeStatusAsync(id1!.Value, HKCardStatus.OnReview);
        var (approved, approveError) = await s.HK.ChangeStatusAsync(id1.Value, HKCardStatus.Approved);
        Assert.True(approved, approveError);

        var (success2, id2, error2) = await s.HK.CreateNewVersionAsync(id1.Value);
        Assert.True(success2, error2);

        var v1 = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == id1.Value);
        var v2 = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == id2!.Value);
        Assert.NotEqual(v1.Version, v2.Version);
    }

    [Fact]
    public async Task ChangeStatus_Approved_NewVersion_ArchivesPredecessor()
    {
        await using var s = _fixture.CreateScope();
        var sourceId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (created, newCardId, createError) = await s.HK.CreateNewVersionAsync(sourceId);
        Assert.True(created, createError);

        await s.HK.ChangeStatusAsync(newCardId.Value, HKCardStatus.OnReview);
        var (approved, approveError) = await s.HK.ChangeStatusAsync(newCardId.Value, HKCardStatus.Approved);
        Assert.True(approved, approveError);

        var predecessor = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == sourceId);
        Assert.Equal(HKCardStatus.Archived, predecessor.Status);

        var successor = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == newCardId.Value);
        Assert.Equal(HKCardStatus.Approved, successor.Status);

        var archiveLog = await s.Db.HKCardStatusLogs.AsNoTracking()
            .AnyAsync(l => l.HKCardId == sourceId && l.ToStatus == HKCardStatus.Archived);
        Assert.True(archiveLog);
    }

    [Fact]
    public async Task ArchiveAsync_ApprovedWithApprovedReplacement_Succeeds()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var replacementId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        var oldCard = await s.Db.HKCards.SingleAsync(c => c.Id == oldId);
        var replacement = await s.Db.HKCards.SingleAsync(c => c.Id == replacementId);
        replacement.Code = oldCard.Code;
        replacement.Version = "vR2";
        replacement.NodeId = oldCard.NodeId;
        replacement.ObjectLevel = oldCard.ObjectLevel;
        replacement.BranchId = oldCard.BranchId;
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, replacementId, "Заменена новой версией");
        Assert.True(success, error);

        var archived = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == oldId);
        Assert.Equal(HKCardStatus.Archived, archived.Status);
    }

    [Fact]
    public async Task ArchiveAsync_WithoutReplacement_Fails()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, Guid.NewGuid(), "Заменена новой версией");
        Assert.False(success);
        Assert.NotNull(error);

        var old = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == oldId);
        Assert.Equal(HKCardStatus.Approved, old.Status);
    }

    [Fact]
    public async Task ArchiveAsync_ReplacementIsSameCard_Fails()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, oldId, "Заменена новой версией");
        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsVersionsForSameObject()
    {
        await using var s = _fixture.CreateScope();
        var sourceId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (created, newCardId, createError) = await s.HK.CreateNewVersionAsync(sourceId);
        Assert.True(created, createError);

        var versions = await s.HK.GetVersionsAsync(sourceId);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Id == sourceId);
        Assert.Contains(versions, v => v.Id == newCardId.Value);
        Assert.Single(versions, v => v.IsCurrent);
    }

    [Fact]
    public async Task ArchiveAsync_EmptyReason_Fails()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var replacementId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        await AlignReplacementAsync(s, oldId, replacementId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, replacementId, "   ");
        Assert.False(success);
        Assert.NotNull(error);

        var old = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == oldId);
        Assert.Equal(HKCardStatus.Approved, old.Status);
    }

    [Fact]
    public async Task ArchiveAsync_OperatorWithoutPermission_Throws()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var replacementId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        await AlignReplacementAsync(s, oldId, replacementId);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.HK.ArchiveAsync(oldId, replacementId, "Заменена новой версией"));
        Assert.Contains(PermissionCodes.HKArchive, ex.Message);
    }

    [Fact]
    public async Task ArchiveAsync_ReplacementWrongObject_Fails()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var replacementId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        await AlignReplacementAsync(s, oldId, replacementId);
        var replacement = await s.Db.HKCards.SingleAsync(c => c.Id == replacementId);
        var otherAggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(otherAggregate);
        replacement.ObjectLevel = HKObjectLevel.Aggregate;
        replacement.AggregateId = otherAggregate.Id;
        replacement.NodeId = null;
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, replacementId, "Заменена новой версией");
        Assert.False(success);
        Assert.NotNull(error);

        var old = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == oldId);
        Assert.Equal(HKCardStatus.Approved, old.Status);
    }

    [Fact]
    public async Task ArchiveAsync_ReplacementNotApproved_Fails()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var replacementId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        var oldCard = await s.Db.HKCards.SingleAsync(c => c.Id == oldId);
        var replacement = await s.Db.HKCards.SingleAsync(c => c.Id == replacementId);
        replacement.Code = oldCard.Code;
        replacement.Version = "vR2";
        replacement.NodeId = oldCard.NodeId;
        replacement.ObjectLevel = oldCard.ObjectLevel;
        replacement.BranchId = oldCard.BranchId;
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, replacementId, "Заменена новой версией");
        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ArchiveAsync_ReplacementExpired_Fails()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var replacementId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        await AlignReplacementAsync(s, oldId, replacementId);
        var replacement = await s.Db.HKCards.SingleAsync(c => c.Id == replacementId);
        replacement.ExpirationDate = DateTime.UtcNow.AddDays(-1);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, replacementId, "Заменена новой версией");
        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ChangeStatus_Approved_NewVersion_PredecessorDeleted_RollsBackApproval()
    {
        await using var s = _fixture.CreateScope();
        var sourceId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (created, newCardId, createError) = await s.HK.CreateNewVersionAsync(sourceId);
        Assert.True(created, createError);

        var source = await s.Db.HKCards.SingleAsync(c => c.Id == sourceId);
        source.Status = HKCardStatus.Deleted;
        await s.Db.SaveChangesAsync();

        await s.HK.ChangeStatusAsync(newCardId.Value, HKCardStatus.OnReview);
        var (approved, approveError) = await s.HK.ChangeStatusAsync(newCardId.Value, HKCardStatus.Approved);
        Assert.False(approved);
        Assert.NotNull(approveError);

        var successor = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == newCardId.Value);
        Assert.NotEqual(HKCardStatus.Approved, successor.Status);
    }

    [Fact]
    public void SupersedesHKCardId_IsExplicitEntityProperty()
    {
        var property = typeof(HKCard).GetProperty(nameof(HKCard.SupersedesHKCardId));
        Assert.NotNull(property);
        Assert.Equal(typeof(Guid?), property.PropertyType);

        var nav = typeof(HKCard).GetProperty(nameof(HKCard.SupersedesHKCard));
        Assert.NotNull(nav);
        Assert.Equal(typeof(HKCard), nav.PropertyType);

        var collection = typeof(HKCard).GetProperty(nameof(HKCard.SupersededBy));
        Assert.NotNull(collection);
    }

    [Fact]
    public async Task CreateNewVersionAsync_AuditLogContainsSourceAndNewSnapshots()
    {
        await using var s = _fixture.CreateScope();
        var sourceId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var source = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == sourceId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newCardId, error) = await s.HK.CreateNewVersionAsync(sourceId);
        Assert.True(success, error);

        Assert.Equal("Создана новая версия ХК", AuditDisplayCatalog.GetAction("HKCard.NewVersionCreated").Title);

        var audit = await s.Db.AuditLogs.AsNoTracking()
            .SingleOrDefaultAsync(a => a.EntityType == "HKCard" && a.EntityId == newCardId.ToString() && a.Action == "HKCard.NewVersionCreated");
        Assert.NotNull(audit);
        Assert.Contains(source.Code, audit.Details);
        Assert.Contains(source.Version, audit.Details);
    }

    [Fact]
    public async Task ArchiveAsync_AuditAndStatusLogContainReasonAndReplacement()
    {
        await using var s = _fixture.CreateScope();
        var oldId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);
        var replacementId = await CreateApprovedCardAsync(s, _fixture.NormAdminA.Id);

        await AlignReplacementAsync(s, oldId, replacementId);
        var replacement = await s.Db.HKCards.AsNoTracking().SingleAsync(c => c.Id == replacementId);
        var reason = "Заменена новой версией";

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, error) = await s.HK.ArchiveAsync(oldId, replacementId, reason);
        Assert.True(success, error);

        var statusLog = await s.Db.HKCardStatusLogs.AsNoTracking()
            .SingleAsync(l => l.HKCardId == oldId && l.ToStatus == HKCardStatus.Archived);
        Assert.Equal(reason, statusLog.Comment);

        var audit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "HKCard" && a.EntityId == oldId.ToString() && a.Action == $"Status:{HKCardStatus.Archived}");
        Assert.Contains(reason, audit.Details);
        Assert.Contains(replacement.Code, audit.Details);
        Assert.Contains(replacement.Version, audit.Details);
    }

    private async Task AlignReplacementAsync(TestScope s, Guid oldId, Guid replacementId)
    {
        var oldCard = await s.Db.HKCards.SingleAsync(c => c.Id == oldId);
        var replacement = await s.Db.HKCards.SingleAsync(c => c.Id == replacementId);
        replacement.Code = oldCard.Code;
        replacement.Version = "vR2";
        replacement.NodeId = oldCard.NodeId;
        replacement.ObjectLevel = oldCard.ObjectLevel;
        replacement.BranchId = oldCard.BranchId;
        await s.Db.SaveChangesAsync();
    }

    private async Task<Guid> CreateDraftCardAsync(TestScope s, string actorId)
    {
        s.User.CurrentUserId = Guid.Parse(actorId);

        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел тест" };
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "АУ-" + Guid.NewGuid().ToString("N")[..6], Name = "СЕ тест" };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            Purpose = "Тест versioning",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new()
                {
                    AssemblyUnitId = au.Id,
                    Quantity = 2,
                    Volume = 1.5m,
                    UnitOfMeasure = "кг",
                    SortOrder = 1,
                    Materials = new List<HKCardItemMaterial>(),
                },
            },
        };

        var created = await s.HK.CreateAsync(card);
        return created.Id;
    }

    private async Task<Guid> CreateApprovedCardAsync(TestScope s, string actorId)
    {
        var id = await CreateDraftCardAsync(s, actorId);
        s.User.CurrentUserId = Guid.Parse(actorId);
        await s.HK.ChangeStatusAsync(id, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(id, HKCardStatus.Approved);
        Assert.True(success, error);
        return id;
    }
}
