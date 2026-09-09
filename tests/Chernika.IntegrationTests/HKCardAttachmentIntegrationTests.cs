using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardAttachmentIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardAttachmentIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task GrantAsync(TestScope s, ApplicationUser user, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = code,
            IsGranted = true, Reason = "Test", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private async Task DenyAsync(TestScope s, ApplicationUser user, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = code,
            IsGranted = false, Reason = "Test deny", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private static byte[] PdfBytes(string marker = "test") =>
        System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4 {marker}");

    private static Stream PdfStream(string marker = "test") => new MemoryStream(PdfBytes(marker));

    private static async Task<ApplicationUser> CreateUserAsync(TestScope s, string role, Guid? branchId)
    {
        var login = role.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..8];
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
                "Создание тестового пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);
        return user;
    }

    private async Task<int> CountAuditsAsync(TestScope s, string entityType, string entityId, string action) =>
        await s.Db.AuditLogs.CountAsync(a =>
            a.EntityType == entityType && a.EntityId == entityId && a.Action == action);

    private async Task<Guid> CreateNodeAsync(TestScope s)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    /// <summary>Создаёт Draft ХК уровня Узел напрямую в БД (без preflight).</summary>
    private async Task<(Guid CardId, string Code)> CreateDraftCardAsync(TestScope s, Guid branchId)
    {
        var nodeId = await CreateNodeAsync(s);
        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "ХК-T-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = branchId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();
        return (card.Id, card.Code);
    }

    // ── 1. Info: HK.View + branch scope ───────────────────────────────────

    [Fact]
    public async Task GetAttachmentInfo_RequiresViewPermission()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);
        await s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "application/pdf", 100);

        var user = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        await GrantAsync(s, user, PermissionCodes.HKView);
        await DenyAsync(s, user, PermissionCodes.HKView);
        SetUser(s, user);
        // Без HK.View вложение не раскрывается (no data leak).
        Assert.Null(await s.HK.GetAttachmentInfoAsync(cardId));
        Assert.Null(await s.HK.OpenAttachmentAsync(cardId));
    }

    [Fact]
    public async Task GetAttachmentInfo_ForeignBranch_ReturnsNull()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        await s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "application/pdf", 100);

        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        await GrantAsync(s, foreign, PermissionCodes.HKView);
        await GrantAsync(s, foreign, PermissionCodes.HKAttachmentEdit);
        SetUser(s, foreign);

        Assert.Null(await s.HK.GetAttachmentInfoAsync(cardId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "application/pdf", 100));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.HK.DeleteAttachmentAsync(cardId));
        Assert.Null(await s.HK.OpenAttachmentAsync(cardId));
    }

    [Fact]
    public async Task GetAttachmentInfo_ReturnsInfo_WhenPresent()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        Assert.Null(await s.HK.GetAttachmentInfoAsync(cardId));
        var saved = await s.HK.SaveAttachmentAsync(cardId, PdfStream(), "скан.pdf", "application/pdf", 100);
        Assert.Equal("скан.pdf", saved.OriginalFileName);

        var info = await s.HK.GetAttachmentInfoAsync(cardId);
        Assert.NotNull(info);
        Assert.Equal("скан.pdf", info!.OriginalFileName);
        Assert.Equal(saved.SizeBytes, info.SizeBytes);
        Assert.False(string.IsNullOrEmpty(info.UploadedByUserName));
    }

    // ── 2. Upload: права и статус ─────────────────────────────────────────

    [Fact]
    public async Task SaveAttachment_RequiresEditPermission()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        var user = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        await GrantAsync(s, user, PermissionCodes.HKView);
        await DenyAsync(s, user, PermissionCodes.HKAttachmentEdit);
        SetUser(s, user);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "application/pdf", 100));
    }

    [Fact]
    public async Task SaveAttachment_RejectsNonDraftStatus()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        var card = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        card.Status = HKCardStatus.Approved;
        await s.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "application/pdf", 100));
    }

    // ── 3. Валидация файла ────────────────────────────────────────────────

    [Fact]
    public async Task SaveAttachment_RejectsInvalidExtension()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.docx", "application/pdf", 100));
    }

    [Fact]
    public async Task SaveAttachment_RejectsInvalidContentType()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "image/jpeg", 100));
    }

    [Fact]
    public async Task SaveAttachment_RejectsInvalidPdfSignature()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(cardId, new MemoryStream(System.Text.Encoding.ASCII.GetBytes("NOTPDF!")), "scan.pdf", "application/pdf", 100));
    }

    [Fact]
    public async Task SaveAttachment_RejectsOversizedFile()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "application/pdf", long.MaxValue));
    }

    // ── 4. Replace: атомарность и аудит ───────────────────────────────────

    [Fact]
    public async Task ReplaceAttachment_SavesOldOnValidationError()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        var first = await s.HK.SaveAttachmentAsync(cardId, PdfStream("first"), "first.pdf", "application/pdf", 100);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.HK.SaveAttachmentAsync(cardId, new MemoryStream("NOTPDF!"u8.ToArray()), "second.pdf", "application/pdf", 100));

        var info = await s.HK.GetAttachmentInfoAsync(cardId);
        Assert.NotNull(info);
        Assert.Equal(first.OriginalFileName, info!.OriginalFileName);
        Assert.Equal(first.SizeBytes, info.SizeBytes);
    }

    [Fact]
    public async Task ReplaceAttachment_WritesDeletedAndCreatedAudits()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        var first = await s.HK.SaveAttachmentAsync(cardId, PdfStream("first"), "first.pdf", "application/pdf", 100);
        var second = await s.HK.SaveAttachmentAsync(cardId, PdfStream("second"), "second.pdf", "application/pdf", 100);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, await CountAuditsAsync(s, "HKCardAttachment", second.Id.ToString(), "Created"));
        Assert.Equal(1, await CountAuditsAsync(s, "HKCardAttachment", first.Id.ToString(), "Deleted"));

        var record = await s.Db.HKCardAttachments.AsNoTracking()
            .SingleAsync(a => a.HKCardId == cardId);
        Assert.Equal(second.Id, record.Id);
        Assert.Equal("second.pdf", record.OriginalFileName);
    }

    // ── 5. Delete: аудит и удаление записи ────────────────────────────────

    [Fact]
    public async Task DeleteAttachment_RemovesRecordAndWritesAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        var saved = await s.HK.SaveAttachmentAsync(cardId, PdfStream(), "scan.pdf", "application/pdf", 100);
        await s.HK.DeleteAttachmentAsync(cardId);

        Assert.Equal(1, await CountAuditsAsync(s, "HKCardAttachment", saved.Id.ToString(), "Deleted"));
        Assert.Null(await s.Db.HKCardAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.HKCardId == cardId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.DeleteAttachmentAsync(cardId));
    }

    // ── 6. Open: content delivery ─────────────────────────────────────────

    [Fact]
    public async Task OpenAttachment_ReturnsContentForAccessibleCard()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        await s.HK.SaveAttachmentAsync(cardId, PdfStream("content"), "scan.pdf", "application/pdf", 100);

        var content = await s.HK.OpenAttachmentAsync(cardId);
        Assert.NotNull(content);
        Assert.Equal("scan.pdf", content!.OriginalFileName);
        Assert.Equal("application/pdf", content.ContentType);
        using var reader = new MemoryStream();
        await content.Content.CopyToAsync(reader);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(reader.ToArray()));
    }

    [Fact]
    public async Task OpenAttachment_Missing_ReturnsNull()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _) = await CreateDraftCardAsync(s, _fixture.BranchA);

        Assert.Null(await s.HK.OpenAttachmentAsync(cardId));
    }
}
