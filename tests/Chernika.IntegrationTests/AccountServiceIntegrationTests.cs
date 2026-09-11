using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class AccountServiceIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public AccountServiceIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private static AccountService Service(TestScope s)
    {
        var storageDir = Path.Combine(Path.GetTempPath(), "chernika_acc_" + Suffix());
        Directory.CreateDirectory(storageDir);
        var options = new Microsoft.Extensions.Options.OptionsWrapper<Chernika.Domain.FileStorageOptions>(
            new Chernika.Domain.FileStorageOptions { MaxPdfSizeBytes = 5 * 1024 * 1024 });
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = storageDir,
            })
            .Build();
        var storage = new LocalFileStorageService(configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalFileStorageService>.Instance);
        return new AccountService(
            s.Db, s.User, s.Users, TimeProvider.System, storage,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountService>());
    }

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task<ApplicationUser> CreateUserAsync(TestScope s, string role, Guid branchId, bool isActive = true)
    {
        var login = role.ToLowerInvariant() + "_" + Suffix();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = isActive,
        };
        var result = await s.Users.CreateAsync(user, InitialPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Создание пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);
        return user;
    }

    private Task<ApplicationUser> CreateAdminAsync(TestScope s) =>
        CreateUserAsync(s, nameof(UserRole.SystemAdmin), _fixture.BranchA);

    private static string InitialPassword => "Admin@12345";

    private static async Task<bool> CheckPasswordAsync(TestScope s, ApplicationUser user, string password) =>
        await s.Users.CheckPasswordAsync(user, password);

    [Fact]
    public async Task GetMyAccountAsync_ReturnsOnlyOwnProfileData()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);

        var account = await service.GetMyAccountAsync();

        Assert.Equal(admin.Id, account.Id);
        Assert.Equal(admin.UserName, account.Login);
        Assert.Equal(admin.FullName, account.FullName);
        Assert.Equal("Системный администратор", account.BaseRoleDisplay);
        Assert.Equal(_fixture.BranchA, Guid.NewGuid() == Guid.Empty ? throw new InvalidOperationException() : await s.Db.Branches
            .AsNoTracking()
            .Where(b => b.Id == admin.BranchId)
            .Select(b => b.Name)
            .FirstAsync() is { } name && account.BranchName == name ? admin.BranchId ?? default : default);
    }

    [Fact]
    public async Task UpdateMyProfileAsync_ChangesFullNameOnly()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);
        var login = admin.UserName;

        await service.UpdateMyProfileAsync(new UpdateMyProfileRequest { FullName = "Новое ФИО" });

        var account = await service.GetMyAccountAsync();
        Assert.Equal("Новое ФИО", account.FullName);
        Assert.Equal(login, account.Login);
        var role = await s.Users.GetRolesAsync(await s.Users.FindByIdAsync(admin.Id)!);
        Assert.Contains(nameof(UserRole.SystemAdmin), role);
    }

    [Fact]
    public async Task ChangeMyPasswordAsync_SucceedsWithCorrectCurrentPassword()
    {
        await using var s = Scope();
        var user = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        SetUser(s, user);
        var service = Service(s);

        await service.ChangeMyPasswordAsync(new ChangeMyPasswordRequest
        {
            CurrentPassword = InitialPassword,
            NewPassword = "x",
            ConfirmPassword = "x",
        });

        var reloaded = await s.Users.FindByIdAsync(user.Id);
        Assert.True(await CheckPasswordAsync(s, reloaded!, "x"));
    }

    [Fact]
    public async Task ChangeMyPasswordAsync_WrongCurrentPassword_FailsControlled()
    {
        await using var s = Scope();
        var user = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        SetUser(s, user);
        var service = Service(s);
        var before = await s.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeMyPasswordAsync(new ChangeMyPasswordRequest
        {
            CurrentPassword = "Несовпадающий",
            NewPassword = "новый",
            ConfirmPassword = "новый",
        }));

        var after = await s.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.True(await CheckPasswordAsync(s, after, InitialPassword));
    }

    [Fact]
    public async Task ChangeMyPasswordAsync_MismatchedConfirm_Fails()
    {
        await using var s = Scope();
        var user = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        SetUser(s, user);
        var service = Service(s);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeMyPasswordAsync(new ChangeMyPasswordRequest
        {
            CurrentPassword = InitialPassword,
            NewPassword = "новый",
            ConfirmPassword = "другой",
        }));
    }

    [Fact]
    public async Task ChangeMyPasswordAsync_SameAsCurrent_Fails()
    {
        await using var s = Scope();
        var user = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        SetUser(s, user);
        var service = Service(s);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeMyPasswordAsync(new ChangeMyPasswordRequest
        {
            CurrentPassword = InitialPassword,
            NewPassword = InitialPassword,
            ConfirmPassword = InitialPassword,
        }));
    }

    [Fact]
    public async Task PasswordPolicy_AcceptsSimpleNonEmptyPasswords()
    {
        await using var s = Scope();
        var user = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        SetUser(s, user);
        var service = Service(s);

        await service.ChangeMyPasswordAsync(new ChangeMyPasswordRequest
        {
            CurrentPassword = InitialPassword,
            NewPassword = "а1",
            ConfirmPassword = "а1",
        });

        var reloaded = await s.Users.FindByIdAsync(user.Id);
        Assert.True(await CheckPasswordAsync(s, reloaded!, "а1"));
    }

    [Fact]
    public async Task AdminReset_SetsMustChangePasswordAndReplacesPassword()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var target = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        var auditsBefore = await s.Db.AuditLogs.CountAsync(a => a.EntityType == "ApplicationUser");
        var service = Service(s);
        var actorId = admin.Id;

        var result = await service.ResetUserPasswordByAdminAsync(
            target.Id,
            new ResetUserPasswordByAdminRequest { NewPassword = "врем", ConfirmPassword = "врем" });

        Assert.True(result.Success);
        var reloaded = await s.Users.FindByIdAsync(target.Id);
        Assert.True(reloaded!.MustChangePassword);
        Assert.True(await CheckPasswordAsync(s, reloaded, "врем"));
        Assert.Equal(auditsBefore, await s.Db.AuditLogs.CountAsync(a => a.EntityType == "ApplicationUser"));
        Assert.Equal(0, await s.Db.AuditLogs.CountAsync(a => a.UserId.ToString() == actorId && a.CreatedAt > DateTime.UtcNow.AddMinutes(-1) && a.Action.Contains("Password")));
    }

    [Fact]
    public async Task AdminReset_ByNonSystemAdmin_Forbidden()
    {
        await using var s = Scope();
        var nonAdmin = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        SetUser(s, nonAdmin);
        var target = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchB);
        var service = Service(s);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ResetUserPasswordByAdminAsync(target.Id,
                new ResetUserPasswordByAdminRequest { NewPassword = "вр", ConfirmPassword = "вр" }));
    }

    [Fact]
    public async Task AdminReset_ForOwnAccount_FailsControlled()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);

        var result = await service.ResetUserPasswordByAdminAsync(admin.Id,
            new ResetUserPasswordByAdminRequest { NewPassword = "вр", ConfirmPassword = "вр" });

        Assert.False(result.Success);
        Assert.Contains("Мой аккаунт", result.Error);
    }

    [Fact]
    public async Task MustChangePassword_ClearedByOwnChange()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var target = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchA);
        var service = Service(s);
        await service.ResetUserPasswordByAdminAsync(
            target.Id,
            new ResetUserPasswordByAdminRequest { NewPassword = "вр", ConfirmPassword = "вр" });

        var operatorScopeUser = await s.Users.FindByIdAsync(target.Id);
        SetUser(s, operatorScopeUser!);
        Assert.True(await service.GetMyMustChangePasswordAsync());

        await service.ChangeMyPasswordAsync(new ChangeMyPasswordRequest
        {
            CurrentPassword = "вр",
            NewPassword = "новый9",
            ConfirmPassword = "новый9",
        });

        Assert.False(await service.GetMyMustChangePasswordAsync());
    }

    [Fact]
    public async Task AvatarUpload_ValidJpeg_CanReplace()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);

        byte[] jpeg1 =
        [
            0xFF, 0xD8,
        ];
        jpeg1 = jpeg1.Concat(new byte[] { 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 })
            .Concat(new byte[] { 0xFF, 0xD9 })
            .ToArray();
        using (var first = new MemoryStream(jpeg1))
        {
            await service.UploadMyAvatarAsync(first, "a.jpg", "image/jpeg", jpeg1.Length);
        }

        var account = await service.GetMyAccountAsync();
        Assert.True(account.HasAvatar);
        var storedKey = (await s.Db.Users.AsNoTracking()
            .FirstAsync(u => u.Id == admin.Id)).AvatarStorageKey;
        Assert.NotNull(storedKey);

        byte[] jpeg2 =
        [
            0xFF, 0xD8,
        ];
        jpeg2 = jpeg2.Concat(new byte[] { 0xFF, 0xE1, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x02 })
            .Concat(new byte[] { 0xFF, 0xD9 })
            .ToArray();
        using (var second = new MemoryStream(jpeg2))
        {
            await service.UploadMyAvatarAsync(second, "b.jpg", "image/jpeg", jpeg2.Length);
        }

        var again = await s.Db.Users.AsNoTracking().FirstAsync(u => u.Id == admin.Id);
        Assert.NotEqual(storedKey, again.AvatarStorageKey);
    }

    [Theory]
    [InlineData(".gif", "GIF89a0")]
    [InlineData(".svg", "<svg/>")]
    [InlineData(".pdf", "%PDF-1.4")]
    public async Task AvatarUpload_ForbiddenFormats_Rejected(string extension, string body)
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UploadMyAvatarAsync(
            new MemoryStream(bytes), "file" + extension, "image/x-test", bytes.Length));
    }

    [Fact]
    public async Task AvatarUpload_Oversize_Rejected()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);
        using var big = new MemoryStream(new byte[3 * 1024 * 1024]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UploadMyAvatarAsync(
            big, "a.jpg", "image/jpeg", big.Length));
    }

    [Fact]
    public async Task AvatarDelete_RestoresInitialsFallback()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9];
        await service.UploadMyAvatarAsync(new MemoryStream(jpeg), "a.jpg", "image/jpeg", jpeg.Length);

        await service.DeleteMyAvatarAsync();

        var account = await service.GetMyAccountAsync();
        Assert.False(account.HasAvatar);
        var user = await s.Db.Users.AsNoTracking().FirstAsync(u => u.Id == admin.Id);
        Assert.Null(user.AvatarStorageKey);
        Assert.Null(user.AvatarContentType);
    }

    [Fact]
    public async Task NoAuditWritten_ForAccountOperations()
    {
        await using var s = Scope();
        var admin = await CreateAdminAsync(s);
        SetUser(s, admin);
        var service = Service(s);

        await service.UpdateMyProfileAsync(new UpdateMyProfileRequest { FullName = "Без аудита" });
        await service.ChangeMyPasswordAsync(new ChangeMyPasswordRequest
        {
            CurrentPassword = InitialPassword,
            NewPassword = "ново1",
            ConfirmPassword = "ново1",
        });
        await service.UploadMyAvatarAsync(
            new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9]),
            "a.jpg", "image/jpeg", 12);
        await service.DeleteMyAvatarAsync();
        await service.ResetUserPasswordByAdminAsync(
            (await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchB)).Id,
            new ResetUserPasswordByAdminRequest { NewPassword = "вр", ConfirmPassword = "вр" });

        Assert.Equal(0, await s.Db.AuditLogs
            .CountAsync(a => a.Details != null && a.Details.Contains("Без аудита")));
    }
}
