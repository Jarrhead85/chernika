using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Регресс: изменение и чтение состава карточки, а также счётчик непрочитанных
/// были доступны без прав.
/// <para>
/// Каждый закрытый дефект проверяется в обе стороны: отказ при запрете И проход
/// при разрешении. Тест только на отказ бесполезен — он не отличает новую
/// проверку от «операция всегда падает».
/// </para>
/// <para>
/// Пользователи у каждого теста свои, общий шаблон роли не меняется: правка
/// общей фикстуры однажды уже ломала чужие тесты.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardComponentAccessTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly Dictionary<string, string> _users = new(StringComparer.Ordinal);
    private Guid _branchA;
    private Guid _branchB;
    private readonly List<Guid> _cardIds = new();
    private readonly List<Guid> _compositionIds = new();
    private Guid _aggregateId;
    private Guid _nodeId;

    public HKCardComponentAccessTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var s = _fixture.CreateScope();

        _branchA = _fixture.BranchA;
        _branchB = _fixture.BranchB;

        // Агрегат и узел создаются здесь: общая фикстура их не засевает, а связь
        // состава должна быть настоящей, иначе бизнес-проверка отклонит её раньше,
        // чем дойдёт дело до проверки прав.
        var aggregate = new Aggregate
        {
            Id = Guid.NewGuid(),
            Code = "REG-AGG-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Агрегат регресса",
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "REG-ND-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Узел регресса",
        };

        s.Db.Aggregates.Add(aggregate);
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();

        _aggregateId = aggregate.Id;
        _nodeId = node.Id;

        // Утверждённый и действующий состав агрегата, включающий узел: без него
        // ValidateCompositionLinkAsync отклонит связь по бизнес-правилу.
        var composition = new AggregateComposition
        {
            Id = Guid.NewGuid(),
            AggregateId = _aggregateId,
            Version = "1",
            Status = ProductCompositionStatus.Approved,
            BranchId = _branchA,
            EffectiveDate = DateTime.UtcNow.AddDays(-30),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ApprovedAt = DateTime.UtcNow.AddDays(-30),
            IsActive = true,
        };

        composition.Nodes.Add(new AggregateCompositionNode
        {
            Id = Guid.NewGuid(),
            AggregateCompositionId = composition.Id,
            NodeId = _nodeId,
            Quantity = 1,
            SortOrder = 1,
        });

        _compositionIds.Add(composition.Id);
        s.Db.AggregateCompositions.Add(composition);
        await s.Db.SaveChangesAsync();

        // Роли подобраны под сами права, а не наоборот. NormAdmin получает
        // HKAggregateEditDraft и HK.View из шаблона, поэтому «запрет» поверх его
        // роли действительно что-то отменяет. Если бы запрет ставился роли, у
        // которой этого права и так нет, тест проходил бы вхолостую.
        // Guest ничего из этого не получает и берёт только индивидуальным решением.
        await CreateUserAsync(s, "denied", nameof(UserRole.NormAdmin));
        await CreateUserAsync(s, "granted", nameof(UserRole.Guest));
        await CreateUserAsync(s, "owner", nameof(UserRole.NormAdmin));
    }

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        // Уборка обязана быть полной. Общая база тестов переживает классы, и
        // оставленные здесь карточки и уведомления попадали в выборку других
        // тестов — это уже стоило четырёх падений в HKCardExpirationIntegrationTests.
        var userIds = _users.Values.ToList();

        await s.Db.Notifications.Where(n => userIds.Contains(n.UserId)).ExecuteDeleteAsync();
        await s.Db.UserPermissionOverrides.Where(o => userIds.Contains(o.UserId)).ExecuteDeleteAsync();

        if (_cardIds.Count > 0)
        {
            await s.Db.HKCardComponents
                .Where(c => _cardIds.Contains(c.ParentHKCardId) || _cardIds.Contains(c.ChildHKCardId))
                .ExecuteDeleteAsync();

            await s.Db.HKCardStatusLogs.Where(l => _cardIds.Contains(l.HKCardId)).ExecuteDeleteAsync();
            await s.Db.HKCards.Where(c => _cardIds.Contains(c.Id)).ExecuteDeleteAsync();
        }

        if (_compositionIds.Count > 0)
        {
            await s.Db.AggregateCompositionNodes
                .Where(n => _compositionIds.Contains(n.AggregateCompositionId))
                .ExecuteDeleteAsync();

            await s.Db.AggregateCompositions
                .Where(c => _compositionIds.Contains(c.Id))
                .ExecuteDeleteAsync();
        }

        if (_aggregateId != Guid.Empty)
            await s.Db.Aggregates.Where(a => a.Id == _aggregateId).ExecuteDeleteAsync();

        if (_nodeId != Guid.Empty)
            await s.Db.Nodes.Where(n => n.Id == _nodeId).ExecuteDeleteAsync();

        // Удаление по идентификатору, а не по ключу: ключ — это "denied"/"owner",
        // а настоящий логин с суффиксом. Поиск по ключу не находил ничего, и
        // пользователи накапливались в общей базе десятками — из-за них обработчик
        // сроков рассылал уведомления полутора десяткам «нормальноровщиков» вместо
        // двух и ронял чужие тесты.
        foreach (var id in userIds)
        {
            var u = await s.Users.FindByIdAsync(id);
            if (u != null) await s.Users.DeleteAsync(u);
        }
    }

    private async Task CreateUserAsync(TestScope s, string key, string role)
    {
        var user = new ApplicationUser
        {
            UserName = "hkcomp_" + key + "_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@hkcomp.test",
            EmailConfirmed = true,
            BranchId = _branchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Hk-Comp-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);

        _users[key] = user.Id;
    }

    private async Task SetDecisionAsync(TestScope s, string key, string code, bool granted)
    {
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = _users[key],
            PermissionCode = code,
            IsGranted = granted,
            Reason = "регресс-тест состава",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });

        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_users[key]);
    }

    /// <summary>Черновик агрегата — родительская карточка состава.</summary>
    private async Task<Guid> CreateParentDraftAsync(TestScope s)
    {
        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "REG-P-" + Guid.NewGuid().ToString("N")[..8],
            Version = "1",
            ObjectLevel = HKObjectLevel.Aggregate,
            AggregateId = _aggregateId,
            BranchId = _branchA,
            Status = HKCardStatus.Draft,
            EffectiveDate = DateTime.UtcNow.AddDays(-10),
        };

        _cardIds.Add(card.Id);
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        return card.Id;
    }

    /// <summary>Утверждённая карточка узла — дочерняя карточка состава.</summary>
    private async Task<Guid> CreateApprovedChildAsync(TestScope s)
    {
        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "REG-C-" + Guid.NewGuid().ToString("N")[..8],
            Version = "1",
            ObjectLevel = HKObjectLevel.Node,
            NodeId = _nodeId,
            BranchId = _branchA,
            Status = HKCardStatus.Approved,
            EffectiveDate = DateTime.UtcNow.AddDays(-10),
            ApprovedDate = DateTime.UtcNow.AddDays(-10),
        };

        _cardIds.Add(card.Id);
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        return card.Id;
    }

    // ── 1. Изменение состава: было доступно любому аутентифицированному ──

    [Fact]
    public async Task AddComponent_IndividualDeny_Refuses_AndWritesNothing()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);
        var childId = await CreateApprovedChildAsync(s);

        // Роль NormAdmin даёт HKAggregateEditDraft, индивидуальный запрет его снимает.
        await SetDecisionAsync(s, "denied", PermissionCodes.HKAggregateEditDraft, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.AddComponentAsync(parentId, childId));

        Assert.False(await s.Db.HKCardComponents
            .AnyAsync(c => c.ParentHKCardId == parentId && c.ChildHKCardId == childId));
    }

    [Fact]
    public async Task AddComponent_RoleGrants_Passes()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);
        var childId = await CreateApprovedChildAsync(s);

        s.User.CurrentUserId = Guid.Parse(_users["owner"]);

        await s.HK.AddComponentAsync(parentId, childId);

        Assert.True(await s.Db.HKCardComponents
            .AnyAsync(c => c.ParentHKCardId == parentId && c.ChildHKCardId == childId));
    }

    [Fact]
    public async Task AddComponent_IndividualGrantWithoutRole_Passes()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);
        var childId = await CreateApprovedChildAsync(s);

        // Роль Guest права редактирования не даёт, индивидуальное разрешение даёт.
        await SetDecisionAsync(s, "granted", PermissionCodes.HKAggregateEditDraft, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["granted"]);

        await s.HK.AddComponentAsync(parentId, childId);

        Assert.True(await s.Db.HKCardComponents
            .AnyAsync(c => c.ParentHKCardId == parentId && c.ChildHKCardId == childId));
    }

    [Fact]
    public async Task AddComponent_RevokeDecision_ReturnsRoleAccess()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);
        var childId = await CreateApprovedChildAsync(s);

        // Решение разрешает, затем снимается — доступ возвращается к роли.
        await SetDecisionAsync(s, "denied", PermissionCodes.HKAggregateEditDraft, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        var decision = await s.Db.UserPermissionOverrides.FirstAsync(o => o.UserId == _users["denied"]);
        s.Db.UserPermissionOverrides.Remove(decision);
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_users["denied"]);

        await s.HK.AddComponentAsync(parentId, childId);

        Assert.True(await s.Db.HKCardComponents
            .AnyAsync(c => c.ParentHKCardId == parentId && c.ChildHKCardId == childId));
    }

    [Fact]
    public async Task RemoveComponent_IndividualDeny_Refuses_AndKeepsRow()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);
        var childId = await CreateApprovedChildAsync(s);

        s.User.CurrentUserId = Guid.Parse(_users["owner"]);
        var component = await s.HK.AddComponentAsync(parentId, childId);

        await SetDecisionAsync(s, "denied", PermissionCodes.HKAggregateEditDraft, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.RemoveComponentAsync(component.Id));

        Assert.True(await s.Db.HKCardComponents.AnyAsync(c => c.Id == component.Id));
    }

    [Fact]
    public async Task ComponentWrite_IndividualGrant_DoesNotCrossOrganization()
    {
        // Обратная сторона индивидуального разрешения: оно не расширяет область
        // организации. Пользователь подтвердил право, но карточка чужая.
        await using var s = _fixture.CreateScope();

        var foreignAggregate = _aggregateId;

        var foreignCard = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "REG-F-" + Guid.NewGuid().ToString("N")[..8],
            Version = "1",
            ObjectLevel = HKObjectLevel.Aggregate,
            AggregateId = foreignAggregate,
            BranchId = _branchB,
            Status = HKCardStatus.Draft,
            EffectiveDate = DateTime.UtcNow.AddDays(-10),
        };

        _cardIds.Add(foreignCard.Id);
        s.Db.HKCards.Add(foreignCard);
        await s.Db.SaveChangesAsync();

        var childId = await CreateApprovedChildAsync(s);

        await SetDecisionAsync(s, "owner", PermissionCodes.HKAggregateEditDraft, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["owner"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.AddComponentAsync(foreignCard.Id, childId));

        Assert.False(await s.Db.HKCardComponents.AnyAsync(c => c.ParentHKCardId == foreignCard.Id));
    }

    // ── 2. Чтение состава: было без проверок вовсе ──────────────────────

    [Fact]
    public async Task GetComponents_IndividualDenyOfHKView_Refuses()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);

        await SetDecisionAsync(s, "denied", PermissionCodes.HKView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.GetComponentsAsync(parentId));
    }

    [Fact]
    public async Task GetParentComponents_IndividualDenyOfHKView_Refuses()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);

        await SetDecisionAsync(s, "denied", PermissionCodes.HKView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.GetParentComponentsAsync(parentId));
    }

    [Fact]
    public async Task GetAggregatedRows_IndividualDenyOfHKView_Refuses()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);

        await SetDecisionAsync(s, "denied", PermissionCodes.HKView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.GetAggregatedRowsAsync(parentId));
    }

    [Fact]
    public async Task ComponentReads_PassForAllowedUser()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);
        s.User.CurrentUserId = Guid.Parse(_users["owner"]);

        Assert.NotNull(await s.HK.GetComponentsAsync(parentId));
        Assert.NotNull(await s.HK.GetParentComponentsAsync(parentId));
        Assert.NotNull(await s.HK.GetAggregatedRowsAsync(parentId));
    }

    [Fact]
    public async Task ComponentReads_IndividualGrantWithoutRole_Passes()
    {
        await using var s = _fixture.CreateScope();

        var parentId = await CreateParentDraftAsync(s);

        // Роль Guest не даёт HK.View, индивидуальное разрешение даёт.
        await SetDecisionAsync(s, "granted", PermissionCodes.HKView, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["granted"]);

        Assert.NotNull(await s.HK.GetComponentsAsync(parentId));
    }

    [Fact]
    public async Task ComponentReads_IndividualGrant_DoesNotCrossOrganization()
    {
        await using var s = _fixture.CreateScope();

        var foreignAggregate = _aggregateId;

        var foreignCard = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "REG-FR-" + Guid.NewGuid().ToString("N")[..8],
            Version = "1",
            ObjectLevel = HKObjectLevel.Aggregate,
            AggregateId = foreignAggregate,
            BranchId = _branchB,
            Status = HKCardStatus.Draft,
            EffectiveDate = DateTime.UtcNow.AddDays(-10),
        };

        _cardIds.Add(foreignCard.Id);
        s.Db.HKCards.Add(foreignCard);
        await s.Db.SaveChangesAsync();

        await SetDecisionAsync(s, "owner", PermissionCodes.HKView, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["owner"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.GetComponentsAsync(foreignCard.Id));
    }

    // ── 3. Счётчик непрочитанных: утекал вопреки запрету ────────────────

    [Fact]
    public async Task UnreadCount_IndividualDeny_ReturnsZero_AndDoesNotThrow()
    {
        await using var s = _fixture.CreateScope();

        var userId = _users["denied"];

        s.Db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = NotificationType.Information,
            Channel = NotificationChannel.InApp,
            Title = "Регресс",
            Message = "Уведомление для проверки счётчика",
            CreatedAtUtc = DateTime.UtcNow,
        });

        await s.Db.SaveChangesAsync();

        await SetDecisionAsync(s, "denied", PermissionCodes.NotificationView, granted: false);
        s.User.CurrentUserId = Guid.Parse(userId);

        // Не исключение: счётчик рисуется в общей раскладке на каждой странице, и
        // падение здесь уронило бы несвязанные страницы. Запрет означает «не
        // показывать счётчик», данные при этом не раскрываются.
        Assert.Equal(0, await s.Notifications.GetUnreadCountAsync());
    }

    [Fact]
    public async Task UnreadCount_AllowedUser_SeesCount()
    {
        await using var s = _fixture.CreateScope();

        var userId = _users["owner"];

        s.Db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = NotificationType.Information,
            Channel = NotificationChannel.InApp,
            Title = "Регресс",
            Message = "Уведомление для проверки счётчика",
            CreatedAtUtc = DateTime.UtcNow,
        });

        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(userId);

        Assert.Equal(1, await s.Notifications.GetUnreadCountAsync());
    }
}
