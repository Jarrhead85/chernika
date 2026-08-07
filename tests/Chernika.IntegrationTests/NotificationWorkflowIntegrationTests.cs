using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class NotificationWorkflowIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public NotificationWorkflowIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChangeStatus_RevisionRequired_NotifiesAuthor_WithHKReturnedForRevision()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.RevisionRequired, comment: "Уточнить нормы расхода.");
        Assert.True(success, error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);

        var notification = await s.Db.Notifications.AsNoTracking()
            .SingleAsync(n => n.UserId == _fixture.NormAdminA.Id
                && n.EntityType == "HKCard" && n.EntityId == cardId
                && n.Type == NotificationType.HKReturnedForRevision);

        Assert.Equal($"ХК {card.Code} возвращена на доработку", notification.Title);
        Assert.Equal("Уточнить нормы расхода.", notification.Message);
        Assert.Equal($"/хк/{cardId}", notification.NavigationUrl);
        Assert.False(notification.IsRead);
        Assert.Equal(_fixture.BranchA, notification.BranchId);
    }

    [Fact]
    public async Task ChangeStatus_Approved_NotifiesAuthor_WithHKApproved()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);
        Assert.True(success, error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);

        var notification = await s.Db.Notifications.AsNoTracking()
            .SingleAsync(n => n.UserId == _fixture.NormAdminA.Id
                && n.EntityType == "HKCard" && n.EntityId == cardId
                && n.Type == NotificationType.HKApproved);

        Assert.Equal($"ХК {card.Code} утверждена", notification.Title);
        Assert.Equal($"/хк/{cardId}", notification.NavigationUrl);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public async Task CreateProposalAsync_NotifiesBranchNormAdmins_WithReferenceProposalPending()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var proposal = await s.HK.CreateProposalAsync(
            cardId, ProposalTargetType.Node,
            "N-NEW-1", "Новый узел", "Описание", gost: null, type: null);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.ReferenceProposalPending
                && n.EntityType == "ReferenceProposal" && n.EntityId == proposal.Id)
            .ToListAsync();

        Assert.Equal(2, notifications.Count);
        Assert.Contains(notifications, n => n.UserId == _fixture.NormAdminA.Id);
        Assert.Contains(notifications, n => n.UserId == _fixture.NormAdminA2.Id);

        foreach (var n in notifications)
        {
            Assert.Equal("Новое предложение справочника: Новый узел", n.Title);
            Assert.Equal($"/хк/{cardId}", n.NavigationUrl);
            Assert.Equal($"ref-proposal:{proposal.Id}:{n.UserId}", n.DeduplicationKey);
            Assert.False(n.IsRead);
        }

        var createdAudit = await s.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityType == "ReferenceProposal" && a.EntityId == proposal.Id.ToString() && a.Action == "Created");
        Assert.True(createdAudit);
    }

    [Fact]
    public async Task CreateProposalAsync_NotifiesBranchNormAdminsExactlyOnce()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var proposal = await s.HK.CreateProposalAsync(
            cardId, ProposalTargetType.Node,
            "N-NEW-2", "Новый узел 2", description: null, gost: null, type: null);

        var notifications = await s.Db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.ReferenceProposalPending
                && n.EntityType == "ReferenceProposal" && n.EntityId == proposal.Id)
            .ToListAsync();

        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n => Assert.Equal($"ref-proposal:{proposal.Id}:{n.UserId}", n.DeduplicationKey));
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
            Purpose = "Тест уведомлений",
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
        Assert.Equal(HKCardStatus.Draft, created.Status);
        return created.Id;
    }
}
