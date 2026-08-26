using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardRegistryIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardRegistryIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetRegistryPageAsync_BranchScope_ReturnsOnlyAccessibleCards()
    {
        await using var s = _fixture.CreateScope();
        var cardA = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var cardB = await CreateDraftCardAsync(s, _fixture.NormAdminB.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var result = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery());

        Assert.Contains(result.Items, c => c.Id == cardA);
        Assert.DoesNotContain(result.Items, c => c.Id == cardB);
        Assert.All(result.Items, c => Assert.Equal(_fixture.BranchA, c.BranchId));
    }

    [Fact]
    public async Task GetRegistryPageAsync_StatusFilter_ReturnsMatchingCards()
    {
        await using var s = _fixture.CreateScope();
        var draftId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var reviewId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var reviewCard = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == reviewId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(reviewId, HKCardStatus.OnReview);

        var result = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery
        {
            Status = HKCardStatus.OnReview,
            SearchText = reviewCard.Code
        });
        Assert.Single(result.Items);
        Assert.Equal(reviewId, result.Items[0].Id);

        var draftResult = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery
        {
            Status = HKCardStatus.Draft,
            SearchText = reviewCard.Code
        });
        Assert.Empty(draftResult.Items);
    }

    [Fact]
    public async Task GetRegistryPageAsync_SearchText_MatchesCode()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var result = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery { SearchText = card.Code });
        Assert.Single(result.Items);
        Assert.Equal(cardId, result.Items[0].Id);

        var noResult = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery { SearchText = "DefinitelyNotExists" });
        Assert.Empty(noResult.Items);
    }

    [Fact]
    public async Task GetRegistryPageAsync_OnlyMine_ReturnsAuthorsCards()
    {
        await using var s = _fixture.CreateScope();
        var cardA = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var cardB = await CreateDraftCardAsync(s, _fixture.NormAdminA2.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var result = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery { OnlyMine = true });
        Assert.Contains(result.Items, c => c.Id == cardA);
        Assert.DoesNotContain(result.Items, c => c.Id == cardB);
    }

    [Fact]
    public async Task GetRegistryPageAsync_RequiresMyAction_ReturnsActionableCards()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var draftActionable = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery { RequiresMyAction = true });
        Assert.Contains(draftActionable.Items, c => c.Id == cardId);

        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        var reviewActionable = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery { RequiresMyAction = true });
        Assert.Contains(reviewActionable.Items, c => c.Id == cardId);

        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);
        var approvedActionable = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery { RequiresMyAction = true });
        Assert.Contains(approvedActionable.Items, c => c.Id == cardId);
    }

    [Fact]
    public async Task GetRegistryPageAsync_ObjectLevelFilter_ReturnsMatchingCards()
    {
        await using var s = _fixture.CreateScope();
        var nodeCardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var complexCardId = await CreateComplexDraftCardAsync(s, _fixture.NormAdminA.Id);
        var complexCard = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == complexCardId);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var result = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery
        {
            ObjectLevel = HKObjectLevel.Complex,
            SearchText = complexCard.Code
        });
        Assert.Single(result.Items);
        Assert.Equal(complexCardId, result.Items[0].Id);
    }

    [Fact]
    public async Task GetRegistryKpiAsync_CountsOnlyAccessibleBranch()
    {
        await using var s = _fixture.CreateScope();
        await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        await CreateDraftCardAsync(s, _fixture.NormAdminB.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var kpi = await s.HK.GetRegistryKpiAsync();
        var page = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery());

        Assert.Equal(page.TotalCount, kpi.Total);
        Assert.All(page.Items, c => Assert.Equal(_fixture.BranchA, c.BranchId));
        Assert.True(kpi.Draft >= 1);
        Assert.True(kpi.RequiresMyAction >= 1);
    }

    [Fact]
    public async Task GetAuthorsAsync_ReturnsAuthorsFromAccessibleBranch()
    {
        await using var s = _fixture.CreateScope();
        await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        await CreateDraftCardAsync(s, _fixture.NormAdminB.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var authors = await s.HK.GetAuthorsAsync();
        Assert.Contains(authors, a => a.Id == _fixture.NormAdminA.Id);
        Assert.DoesNotContain(authors, a => a.Id == _fixture.NormAdminB.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var allAuthors = await s.HK.GetAuthorsAsync();
        Assert.Contains(allAuthors, a => a.Id == _fixture.NormAdminA.Id);
        Assert.Contains(allAuthors, a => a.Id == _fixture.NormAdminB.Id);
    }

    [Fact]
    public async Task GetRegistryPageAsync_AuthorFilter_WorksForSystemAdmin()
    {
        await using var s = _fixture.CreateScope();
        var cardA = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardA);
        await CreateDraftCardAsync(s, _fixture.NormAdminA2.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var result = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery
        {
            AuthorId = _fixture.NormAdminA.Id,
            SearchText = card.Code
        });
        Assert.Single(result.Items);
        Assert.Equal(cardA, result.Items[0].Id);
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
            Purpose = "Тест реестра",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new()
                {
                    AssemblyUnitId = au.Id,
                    Quantity = 1,
                    Volume = 1m,
                    UnitOfMeasure = "кг",
                    SortOrder = 1,
                    Materials = new List<HKCardItemMaterial>(),
                },
            },
        };

        var created = await s.HK.CreateAsync(card);
        return created.Id;
    }

    private async Task<Guid> CreateComplexDraftCardAsync(TestScope s, string actorId)
    {
        s.User.CurrentUserId = Guid.Parse(actorId);

        var complex = new Complex { Id = Guid.NewGuid(), Code = "K-" + Guid.NewGuid().ToString("N")[..6], Name = "Комплекс тест" };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            ObjectLevel = HKObjectLevel.Complex,
            ComplexId = complex.Id,
            Purpose = "Тест реестра комплекс",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>(),
        };

        var created = await s.HK.CreateAsync(card);
        return created.Id;
    }
}
