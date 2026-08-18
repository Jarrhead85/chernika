using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardDeleteIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardDeleteIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Operator_WithDraftPermission_DeletesDraftCard_OwnBranch()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "Не нужна");

        Assert.True(success, error);
        var card = await s.Db.HKCards.AsNoTracking().IgnoreQueryFilters().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Deleted, card.Status);

        var log = await s.Db.HKCardStatusLogs.AsNoTracking()
            .SingleAsync(l => l.HKCardId == cardId && l.ToStatus == HKCardStatus.Deleted);
        Assert.Equal("Не нужна", log.Comment);
        Assert.Equal(Guid.Parse(_fixture.OperatorA.Id), log.ChangedByUserId);

        var audit = await s.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "HKCard" && a.EntityId == cardId.ToString()
                && a.Action == $"Status:{HKCardStatus.Deleted}");
        Assert.Equal("Не нужна", audit.Details);
        Assert.Equal(Guid.Parse(_fixture.OperatorA.Id), audit.UserId);
    }

    [Fact]
    public async Task Operator_CannotDelete_Card_FromOtherBranch()
    {
        await using var s = _fixture.CreateScope();

        var otherBranch = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        s.Db.Branches.Add(new Branch { Id = otherBranch, Name = $"Филиал удаления {suffix}", Code = $"D{suffix}" });
        await s.Db.SaveChangesAsync();

        var otherUser = await CreateUserAsync(s, "operator_c" + Guid.NewGuid().ToString("N")[..6], nameof(UserRole.Operator), otherBranch);
        var cardId = await CreateDraftCardAsync(s, otherUser.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "Попытка удаления");

        Assert.False(success);
        Assert.Contains("другого филиала", error);
    }

    [Fact]
    public async Task Operator_WithoutOnReviewPermission_CannotDelete_OnReviewCard()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "Попытка удаления");

        Assert.False(success);
        Assert.Contains("Недостаточно прав", error);
    }

    [Fact]
    public async Task User_WithOnReviewOverride_Deletes_OnReviewCard_OwnBranch()
    {
        await using var s = _fixture.CreateScope();
        var user = await CreateUserAsync(s, "operator_or" + Guid.NewGuid().ToString("N")[..6], nameof(UserRole.Operator), _fixture.BranchA);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionCode = PermissionCodes.HKDeleteOnReview,
            IsGranted = true,
            GrantedByUserId = _fixture.SystemAdminUser.Id,
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);

        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);

        s.User.CurrentUserId = Guid.Parse(user.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "По согласованию");

        Assert.True(success, error);
        var card = await s.Db.HKCards.AsNoTracking().IgnoreQueryFilters().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Deleted, card.Status);
    }

    [Fact]
    public async Task NormAdmin_Deletes_RevisionRequiredCard_OwnBranch()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.RevisionRequired);

        var (success, error) = await s.HK.DeleteAsync(cardId, "Требует доработки, но не актуально");

        Assert.True(success, error);
        var card = await s.Db.HKCards.AsNoTracking().IgnoreQueryFilters().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Deleted, card.Status);
    }

    [Fact]
    public async Task EmptyReason_IsRejected()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "   ");

        Assert.False(success);
        Assert.Contains("причину", error);

        var card = await s.Db.HKCards.AsNoTracking().SingleAsync(h => h.Id == cardId);
        Assert.Equal(HKCardStatus.Draft, card.Status);
    }

    [Fact]
    public async Task Approved_Card_CannotBeDeleted_EvenBySystemAdmin()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "Попытка удаления");

        Assert.False(success);
        Assert.Contains("не предусмотрено", error);
    }

    [Fact]
    public async Task Archived_Card_CannotBeDeleted_EvenBySystemAdmin()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.OnReview);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Approved);
        await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Archived);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "Попытка удаления");

        Assert.False(success);
        Assert.Contains("не предусмотрено", error);
    }

    [Fact]
    public async Task AlreadyDeleted_Card_CannotBeDeletedAgain()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await s.HK.DeleteAsync(cardId, "Первая причина");

        var (success, error) = await s.HK.DeleteAsync(cardId, "Вторая причина");

        Assert.False(success);
        Assert.Contains("уже удалена", error);
    }

    [Fact]
    public async Task SuccessfulDelete_CreatesExactlyOne_StatusLog_And_Audit()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var (success, error) = await s.HK.DeleteAsync(cardId, "Ровно одна запись");
        Assert.True(success, error);

        var statusLogs = await s.Db.HKCardStatusLogs.AsNoTracking()
            .CountAsync(l => l.HKCardId == cardId && l.ToStatus == HKCardStatus.Deleted);
        Assert.Equal(1, statusLogs);

        var audits = await s.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityType == "HKCard" && a.EntityId == cardId.ToString()
                && a.Action == $"Status:{HKCardStatus.Deleted}");
        Assert.Equal(1, audits);
    }

    [Fact]
    public async Task ChangeStatus_ToDeleted_RequiresReason()
    {
        await using var s = _fixture.CreateScope();
        var cardId = await CreateDraftCardAsync(s, _fixture.NormAdminA.Id);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        var (success, error) = await s.HK.ChangeStatusAsync(cardId, HKCardStatus.Deleted, null);

        Assert.False(success);
        Assert.Contains("причину", error);
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        TestScope s, string login, string role, Guid branchId)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = true,
        };

        var result = await s.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Создание пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        var roleResult = await s.Users.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
            throw new InvalidOperationException(
                "Назначение роли не удалось: " + string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        return user;
    }

    private static async Task<Guid> CreateDraftCardAsync(TestScope s, string actorId)
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
            Purpose = "Тест удаления",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new()
                {
                    AssemblyUnitId = au.Id,
                    Quantity = 1,
                    Volume = 1,
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
