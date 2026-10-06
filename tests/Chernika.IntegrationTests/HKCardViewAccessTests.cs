using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Регресс: чтение реестра ХК не требовало HK.View, а вложение читалось без
/// HK.Attachment.View и без проверки организации.
/// <para>
/// Права подбираются под сами проверки: NormAdmin получает оба права из роли,
/// поэтому индивидуальный запрет поверх роли что-то отменяет. Guest не получает
/// ничего и берёт только индивидуальным решением — так проверяется, что
/// разрешение действительно открывает доступ, а не только запрет его закрывает.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardViewAccessTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly List<Guid> _cardIds = new();
    private readonly Dictionary<string, string> _users = new(StringComparer.Ordinal);
    private Guid _branchA;
    private Guid _branchB;

    public HKCardViewAccessTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var s = _fixture.CreateScope();

        _branchA = _fixture.BranchA;
        _branchB = _fixture.BranchB;

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "HKV-ND-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Узел проверки доступа",
        };

        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        _nodeId = node.Id;

        await CreateUserAsync(s, "allowed", nameof(UserRole.NormAdmin));
        await CreateUserAsync(s, "denied", nameof(UserRole.NormAdmin));
        await CreateUserAsync(s, "granted", nameof(UserRole.Guest));
    }

    private Guid _nodeId;

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var ids = _users.Values.ToList();

        await s.Db.Notifications.Where(n => ids.Contains(n.UserId)).ExecuteDeleteAsync();
        await s.Db.UserPermissionOverrides.Where(o => ids.Contains(o.UserId)).ExecuteDeleteAsync();
        await s.Db.HKCardAttachments.Where(a => _cardIds.Contains(a.HKCardId)).ExecuteDeleteAsync();

        if (_cardIds.Count > 0)
        {
            await s.Db.HKCardStatusLogs.Where(l => _cardIds.Contains(l.HKCardId)).ExecuteDeleteAsync();
            await s.Db.HKCards.Where(c => _cardIds.Contains(c.Id)).ExecuteDeleteAsync();
        }

        if (_nodeId != Guid.Empty)
            await s.Db.Nodes.Where(n => n.Id == _nodeId).ExecuteDeleteAsync();

        foreach (var id in ids)
        {
            var u = await s.Users.FindByIdAsync(id);
            if (u != null) await s.Users.DeleteAsync(u);
        }
    }

    private async Task CreateUserAsync(TestScope s, string key, string role)
    {
        var user = new ApplicationUser
        {
            UserName = "hkview_" + key + "_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@hkview.test",
            EmailConfirmed = true,
            BranchId = _branchA,
            IsActive = true,
        };

        var created = await s.Users.CreateAsync(user, "Hk-View-Pass-1");
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
            Reason = "регресс-тест чтения ХК",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });

        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_users[key]);
    }

    private async Task<Guid> CreateCardAsync(TestScope s, Guid branchId, HKCardStatus status)
    {
        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HKV-" + Guid.NewGuid().ToString("N")[..8],
            Version = "1",
            ObjectLevel = HKObjectLevel.Node,
            NodeId = _nodeId,
            BranchId = branchId,
            Status = status,
            EffectiveDate = DateTime.UtcNow.AddDays(-10),
        };

        _cardIds.Add(card.Id);
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        return card.Id;
    }

    private async Task<Guid> CreateCardWithAttachmentAsync(TestScope s, Guid branchId, HKCardStatus status)
    {
        var cardId = await CreateCardAsync(s, branchId, status);

        s.Db.HKCardAttachments.Add(new HKCardAttachment
        {
            Id = Guid.NewGuid(),
            HKCardId = cardId,
            OriginalFileName = "Проверка.pdf",
            StorageKey = "test/" + Guid.NewGuid().ToString("N") + ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            Sha256 = new string('0', 64),
            UploadedByUserId = _fixture.SystemAdminUser.Id,
            UploadedByUserName = "Регресс",
            UploadedAt = DateTime.UtcNow,
        });

        await s.Db.SaveChangesAsync();
        return cardId;
    }

    // ── 1. Реестр ХК требовал только организацию, но не HK.View ──────────

    [Fact]
    public async Task RegistryReads_IndividualDenyOfHKView_Refuse()
    {
        await using var s = _fixture.CreateScope();

        await CreateCardAsync(s, _branchA, HKCardStatus.Approved);

        await SetDecisionAsync(s, "denied", PermissionCodes.HKView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.GetPagedAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.GetStatusCountsAsync(null));
    }

    [Fact]
    public async Task RegistryReads_RoleGrants_Pass()
    {
        await using var s = _fixture.CreateScope();

        await CreateCardAsync(s, _branchA, HKCardStatus.Approved);

        s.User.CurrentUserId = Guid.Parse(_users["allowed"]);

        Assert.NotEmpty(await s.HK.GetAllAsync());
    }

    [Fact]
    public async Task RegistryReads_IndividualGrantWithoutRole_Pass()
    {
        await using var s = _fixture.CreateScope();

        await CreateCardAsync(s, _branchA, HKCardStatus.Approved);

        // Роль Guest не даёт HK.View, индивидуальное разрешение даёт.
        await SetDecisionAsync(s, "granted", PermissionCodes.HKView, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["granted"]);

        Assert.NotEmpty(await s.HK.GetAllAsync());
    }

    [Fact]
    public async Task GetById_IndividualDenyOfHKView_Refuses()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await CreateCardAsync(s, _branchA, HKCardStatus.Approved);

        await SetDecisionAsync(s, "denied", PermissionCodes.HKView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.GetByIdAsync(cardId));
    }

    // ── 2. Вложение читалось по HK.View и без проверки организации ───────

    [Fact]
    public async Task AttachmentRead_IndividualDenyOfHKAttachmentView_Refuses()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await CreateCardWithAttachmentAsync(s, _branchA, HKCardStatus.Approved);

        // Роль даёт HK.Attachment.View, индивидуальный запрет его снимает.
        await SetDecisionAsync(s, "denied", PermissionCodes.HKAttachmentView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        Assert.Null(await s.HK.OpenAttachmentAsync(cardId));
        Assert.Null(await s.HK.GetAttachmentInfoAsync(cardId));
    }

    [Fact]
    public async Task AttachmentRead_RoleGrants_Passes()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await CreateCardWithAttachmentAsync(s, _branchA, HKCardStatus.Approved);

        s.User.CurrentUserId = Guid.Parse(_users["allowed"]);

        // Метаданные, а не OpenAttachmentAsync: открытие читает файл из хранилища,
        // и проверка прав тут была бы не про то. Само открытие закрыто проверкой
        // в соседнем тесте на запрет.
        Assert.NotNull(await s.HK.GetAttachmentInfoAsync(cardId));
    }

    [Fact]
    public async Task AttachmentRead_IndividualGrantWithoutRole_Passes()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await CreateCardWithAttachmentAsync(s, _branchA, HKCardStatus.Approved);

        // Роль Guest не даёт ни HK.View, ни HK.Attachment.View — оба выдаются
        // индивидуально, иначе проверка была бы не про то право.
        await SetDecisionAsync(s, "granted", PermissionCodes.HKView, granted: true);
        await SetDecisionAsync(s, "granted", PermissionCodes.HKAttachmentView, granted: true);

        s.User.CurrentUserId = Guid.Parse(_users["granted"]);

        Assert.NotNull(await s.HK.GetAttachmentInfoAsync(cardId));
    }

    [Fact]
    public async Task AttachmentRead_DenyOfHKView_AlsoBlocks()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await CreateCardWithAttachmentAsync(s, _branchA, HKCardStatus.Approved);

        await SetDecisionAsync(s, "denied", PermissionCodes.HKView, granted: false);
        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        Assert.Null(await s.HK.OpenAttachmentAsync(cardId));
    }

    [Fact]
    public async Task AttachmentRead_IndividualGrant_DoesNotCrossOrganization()
    {
        // Ключевая проверка прежней дыры: файл чужой организации отдавался по
        // одному праву просмотра ХК, без сверки организации.
        await using var s = _fixture.CreateScope();

        var foreignCardId = await CreateCardWithAttachmentAsync(s, _branchB, HKCardStatus.Approved);

        await SetDecisionAsync(s, "allowed", PermissionCodes.HKAttachmentView, granted: true);
        s.User.CurrentUserId = Guid.Parse(_users["allowed"]);

        Assert.Null(await s.HK.OpenAttachmentAsync(foreignCardId));
        Assert.Null(await s.HK.GetAttachmentInfoAsync(foreignCardId));
    }

    [Fact]
    public async Task AttachmentEdit_StillRequiresEditRight_NotOnlyView()
    {
        await using var s = _fixture.CreateScope();

        var cardId = await CreateCardWithAttachmentAsync(s, _branchA, HKCardStatus.Draft);

        // Право просмотра есть, права управления нет: удалять нельзя.
        await SetDecisionAsync(s, "denied", PermissionCodes.HKAttachmentView, granted: true);
        await SetDecisionAsync(s, "denied", PermissionCodes.HKAttachmentEdit, granted: false);

        s.User.CurrentUserId = Guid.Parse(_users["denied"]);

        var error = await Record.ExceptionAsync(() => s.HK.DeleteAttachmentAsync(cardId));

        Assert.IsType<UnauthorizedAccessException>(error);

        // Вложение осталось на месте.
        await using var verify = _fixture.CreateScope();
        Assert.True(await verify.Db.HKCardAttachments.AnyAsync(a => a.HKCardId == cardId));
    }
}
