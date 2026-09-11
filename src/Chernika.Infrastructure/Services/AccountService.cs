using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Аккаунт текущего пользователя: профиль, собственный пароль, аватар
/// и административный сброс пароля. Аудит для этих действий не применяется;
/// секреты (пароль/хэш/токен/storage key) в результатах не возвращаются.
/// </summary>
public sealed class AccountService
{
    private const int MaxAvatarSizeBytes = 2 * 1024 * 1024;
    private static readonly string[] AllowedAvatarExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    private readonly AppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TimeProvider _time;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        AppDbContext db,
        ICurrentUserService currentUser,
        UserManager<ApplicationUser> userManager,
        TimeProvider time,
        IFileStorageService fileStorage,
        ILogger<AccountService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
        _time = time;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    // ── Профиль ───────────────────────────────────────────────────────────

    public async Task<MyAccountDto> GetMyAccountAsync(CancellationToken ct = default)
    {
        var user = await LoadCurrentUserAsync(ct);
        var roles = await _userManager.GetRolesAsync(user);
        var baseRole = roles.Count > 0 ? roles[0] : null;
        string? branchName = null;
        if (user.BranchId is { } branchId)
        {
            branchName = await _db.Branches.AsNoTracking()
                .Where(b => b.Id == branchId)
                .Select(b => (string?)b.Name)
                .FirstOrDefaultAsync(ct);
        }

        return new MyAccountDto(
            user.Id,
            user.UserName ?? string.Empty,
            user.FullName,
            user.Position,
            RoleDisplay(baseRole),
            branchName,
            DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc),
            user.IsActive && !user.IsDeleted,
            user.AvatarStorageKey is not null,
            user.AvatarUpdatedAt?.Ticks ?? 0,
            user.MustChangePassword);
    }

    public async Task UpdateMyProfileAsync(UpdateMyProfileRequest request, CancellationToken ct = default)
    {
        var fullName = request.FullName?.Trim() ?? string.Empty;
        if (fullName.Length == 0)
            throw new ArgumentException("Введите ФИО.");

        var user = await LoadCurrentUserAsync(ct);
        user.FullName = fullName;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> GetMyMustChangePasswordAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId().ToString();
        return await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.MustChangePassword)
            .FirstOrDefaultAsync(ct);
    }

    // ── Смена собственного пароля ─────────────────────────────────────────

    public async Task ChangeMyPasswordAsync(ChangeMyPasswordRequest request, CancellationToken ct = default)
    {
        var current = request.CurrentPassword ?? string.Empty;
        var newPassword = request.NewPassword ?? string.Empty;
        var confirm = request.ConfirmPassword ?? string.Empty;

        if (newPassword.Length == 0)
            throw new ArgumentException("Введите новый пароль.");
        if (newPassword != confirm)
            throw new ArgumentException("Пароли не совпадают.");

        var user = await LoadCurrentUserAsync(ct);
        if (newPassword == current)
            throw new ArgumentException("Новый пароль совпадает с текущим.");

        // Единственная точка смены собственного пароля — ChangePasswordAsync:
        // Identity атомарно проверяет текущий пароль и обновляет хэш.
        var result = await _userManager.ChangePasswordAsync(user, current, newPassword);
        if (!result.Succeeded)
            throw new ArgumentException(MapIdentityPasswordError(result));

        // Успешная смена снимает обязательную смену временного пароля.
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);
    }

    private static string MapIdentityPasswordError(IdentityResult result)
    {
        var details = string.Join(" ",
            result.Errors
                .Where(e => e.Code != "PasswordMismatch")
                .Select(e => e.Description));
        var failedCurrent = result.Errors.Any(e => e.Code == "PasswordMismatch");
        if (failedCurrent)
            return "Неверный текущий пароль.";
        return string.IsNullOrWhiteSpace(details)
            ? "Не удалось изменить пароль."
            : details;
    }

    // ── Аватар ────────────────────────────────────────────────────────────

    public async Task UploadMyAvatarAsync(
        Stream content,
        string originalFileName,
        string? contentType,
        long declaredSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var ext = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || !AllowedAvatarExtensions.Contains(ext))
            throw new ArgumentException("Неверное расширение: поддерживаются JPEG, PNG и WEBP.");

        // Поток браузерного файла не поддерживает seek: читаем целиком в буфер.
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, ct);

        if (buffered.Length <= 0)
            throw new ArgumentException("Файл пуст.");
        if (buffered.Length > MaxAvatarSizeBytes)
            throw new ArgumentException("Файл больше 2 МБ.");

        var bytes = buffered.ToArray();
        // Content-Type определяется сервером по фактическим байтам; значение,
        // присланное клиентом, авторитетным не является.
        var detectedType = DetectAvatarContentType(bytes);
        if (detectedType is null)
            throw new ArgumentException("Поддерживаются только JPEG, PNG и WEBP.");

        // Согласованность расширения и фактической подписи.
        if (detectedType is "image/jpeg" && ext is not (".jpg" or ".jpeg"))
            throw new ArgumentException("Файл JPEG должен иметь расширение .jpg или .jpeg.");
        if (detectedType is "image/png" && ext is not ".png")
            throw new ArgumentException("Файл PNG должен иметь расширение .png.");
        if (detectedType is "image/webp" && ext is not ".webp")
            throw new ArgumentException("Файл WEBP должен иметь расширение .webp.");

        var user = await LoadCurrentUserAsync(ct);
        var safeExt = ext is ".jpeg" ? ".jpg" : ext;
        var storageKey = $"avatars/{user.Id}/{Guid.NewGuid():N}{safeExt}";

        await using var upload = new MemoryStream(bytes, writable: false);
        var stored = await _fileStorage.SaveAsync(upload, storageKey, ct);

        var oldKey = user.AvatarStorageKey;
        user.AvatarStorageKey = storageKey;
        user.AvatarContentType = detectedType;
        user.AvatarSizeBytes = stored.SizeBytes;
        user.AvatarUpdatedAt = DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        await _db.SaveChangesAsync(ct);

        // После успешного сохранения в БД старый файл удаляем best-effort.
        if (oldKey is not null && oldKey != storageKey)
        {
            try
            {
                await _fileStorage.DeleteAsync(oldKey, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Сбой удаления старого аватара");
            }
        }
    }

    public async Task DeleteMyAvatarAsync(CancellationToken ct = default)
    {
        var user = await LoadCurrentUserAsync(ct);
        var oldKey = user.AvatarStorageKey;
        if (oldKey is null)
            return;

        user.AvatarStorageKey = null;
        user.AvatarContentType = null;
        user.AvatarSizeBytes = null;
        user.AvatarUpdatedAt = DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _fileStorage.DeleteAsync(oldKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сбой удаления файла аватара");
        }
    }

    public async Task<MyAvatarContentDto?> GetMyAvatarAsync(CancellationToken ct = default)
    {
        var user = await LoadCurrentUserAsync(ct);
        if (user.AvatarStorageKey is not { } key || user.AvatarContentType is not { } type)
            return null;

        try
        {
            await using var stream = await _fileStorage.OpenReadAsync(key, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return new MyAvatarContentDto(type, ms.ToArray());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Определение типа по реальным магическим байтам; возвращает
    /// image/jpeg, image/png или image/webp, иначе null.
    /// </summary>
    private static string? DetectAvatarContentType(ReadOnlySpan<byte> bytes)
    {
        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";
        // WEBP: RIFF....WEBP
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        return null;
    }

    // ── Административный сброс пароля ─────────────────────────────────────

    public async Task<ResetUserPasswordResult> ResetUserPasswordByAdminAsync(
        string targetUserId,
        ResetUserPasswordByAdminRequest request,
        CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId().ToString();
        var actor = await _userManager.FindByIdAsync(actorId)
            ?? throw new UnauthorizedAccessException("Пользователь не найден.");
        var isSystemAdmin = await _userManager.IsInRoleAsync(actor, UserRole.SystemAdmin.ToString());
        if (!isSystemAdmin)
            throw new UnauthorizedAccessException("Доступно только системному администратору.");

        var password = request.NewPassword ?? string.Empty;
        if (password.Length == 0)
            throw new ArgumentException("Введите временный пароль.");
        if (password != request.ConfirmPassword)
            throw new ArgumentException("Пароли не совпадают.");

        if (string.Equals(targetUserId, actorId, StringComparison.Ordinal))
            return new ResetUserPasswordResult(false, "Свой пароль меняется в разделе «Мой аккаунт».");

        var target = await _userManager.FindByIdAsync(targetUserId);
        if (target is null)
            return new ResetUserPasswordResult(false, "Пользователь не найден.");
        if (!target.IsActive || target.IsDeleted)
            return new ResetUserPasswordResult(false, "Учётная запись неактивна.");

        // Атомарная Identity-схема: токен сброса + ResetPassword.
        var token = await _userManager.GeneratePasswordResetTokenAsync(target);
        var result = await _userManager.ResetPasswordAsync(target, token, password);
        if (!result.Succeeded)
        {
            var details = string.Join(" ", result.Errors.Select(e => e.Description));
            return new ResetUserPasswordResult(false,
                string.IsNullOrWhiteSpace(details) ? "Не удалось установить временный пароль." : details);
        }

        target.MustChangePassword = true;
        await _db.SaveChangesAsync(ct);
        return new ResetUserPasswordResult(true, null);
    }

    // ── Вспомогательные методы ────────────────────────────────────────────

    private async Task<ApplicationUser> LoadCurrentUserAsync(CancellationToken ct)
    {
        var userId = _currentUser.GetRequiredUserId();
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDeleted || !user.IsActive)
            throw new UnauthorizedAccessException("Пользователь недоступен.");
        return user;
    }

    private static string RoleDisplay(string? baseRole) => baseRole switch
    {
        nameof(UserRole.SystemAdmin) => "Системный администратор",
        nameof(UserRole.NormAdmin) => "Нормировщик",
        nameof(UserRole.Operator) => "Оператор",
        nameof(UserRole.HeadOfDepartment) => "Начальник отдела",
        nameof(UserRole.Guest) => "Гость",
        _ => "Пользователь",
    };
}
