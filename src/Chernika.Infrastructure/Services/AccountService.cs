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
/// Аккаунт текущего пользователя: просмотр/изменение профиля, смена пароля,
/// аватар и административный сброс пароля (SystemAdmin).
/// Профильные и парольные операции аудиту не подлежат; никаких секретов
/// (пароль, хэш, токен, storage key) в результатах не возвращается.
/// </summary>
public sealed class AccountService
{
    private const long MaxAvatarSizeBytes = 2 * 1024 * 1024;
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
        var fullName = request?.FullName?.Trim() ?? string.Empty;
        if (fullName.Length == 0)
            throw new ArgumentException("Введите ФИО.");

        var user = await LoadCurrentUserAsync(ct);
        user.FullName = fullName;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearMyForcePasswordChangeAsync(CancellationToken ct = default)
    {
        var user = await LoadCurrentUserAsync(ct);
        if (!user.MustChangePassword)
            return;
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> GetMyMustChangePasswordAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.GetRequiredUserId();
        var flag = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.ToString())
            .Select(u => (bool?)u.MustChangePassword)
            .FirstOrDefaultAsync(ct);
        return flag ?? false;
    }

    // ── Пароль ────────────────────────────────────────────────────────────

    public async Task ChangeMyPasswordAsync(ChangeMyPasswordRequest request, CancellationToken ct = default)
    {
        var current = request.CurrentPassword ?? string.Empty;
        var newPassword = request.NewPassword ?? string.Empty;
        var confirm = request.ConfirmPassword ?? string.Empty;

        if (string.IsNullOrEmpty(confirm) && !string.IsNullOrEmpty(newPassword))
            confirm = newPassword;

        if (string.IsNullOrEmpty(newPassword))
            throw new ArgumentException("Введите новый пароль.");
        if (newPassword != confirm)
            throw new ArgumentException("Пароли не совпадают.");

        var user = await LoadCurrentUserAsync(ct);
        if (newPassword == current)
            throw new ArgumentException("Новый пароль совпадает с текущим.");

        // Проверка текущего пароля через Identity — управляемая ошибка.
        if (!await _userManager.CheckPasswordAsync(user, current))
            throw new ArgumentException("Неверный текущий пароль.");

        // Смена пароля через UserManager: после проверки текущего пароля
        // Remove + Add дают тот же результат, что и ChangePassword, и не
        // требуют зарегистрированного IUserTwoFactorTokenProvider.
        var remove = await _userManager.RemovePasswordAsync(user);
        if (!remove.Succeeded)
            throw new ArgumentException("Не удалось изменить пароль.");

        var add = await _userManager.AddPasswordAsync(user, newPassword);
        if (!add.Succeeded)
        {
            var message = string.Join(" ", add.Errors.Select(e => e.Description));
            throw new ArgumentException(
                string.IsNullOrWhiteSpace(message) ? "Не удалось изменить пароль." : $"Не удалось изменить пароль: {message}");
        }

        // Успешная смена собственного пароля снимает требование смены временного.
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);
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
        var ext = Path.GetExtension(originalFileName ?? string.Empty)
            .ToLowerInvariant();

        if (string.IsNullOrEmpty(ext) || !AllowedAvatarExtensions.Contains(ext))
            throw new ArgumentException("Допустимы форматы JPEG, PNG и WEBP.");
        if (declaredSize <= 0)
            throw new ArgumentException("Файл пуст.");
        if (declaredSize > MaxAvatarSizeBytes)
            throw new ArgumentException("Файл больше 2 МБ.");

        // Проверка декларации content-type по расширению не принимает SVG/GIF.
        if (contentType is { } declared &&
            !declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Файл не является изображением.");

        // Проверка подписи файла: только JPEG / PNG / WEBP, без SVG/GIF/PDF.
        var header = new byte[16];
        var read = await ReadAtLeastAsync(content, header, cancellationToken: ct);
        if (read < 4)
            throw new ArgumentException("Файл повреждён или не является изображением.");

        if (!LooksLikeSupportedImage(header))
            throw new ArgumentException("Поддерживаются только JPEG, PNG и WEBP.");

        var user = await LoadCurrentUserAsync(ct);
        var safeExt = ext switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            _ => ext,
        };
        var contentTypeValue = ContentTypeFromExtension(safeExt);
        var storageKey = $"avatars/{user.Id}/{Guid.NewGuid():N}{safeExt}";
        var stored = await _fileStorage.SaveAsync(content, storageKey, ct);
        if (stored.SizeBytes > MaxAvatarSizeBytes)
        {
            await _fileStorage.DeleteAsync(storageKey, ct);
            throw new ArgumentException("Файл больше 2 МБ.");
        }

        var oldKey = user.AvatarStorageKey;
        user.AvatarStorageKey = storageKey;
        user.AvatarContentType = contentType ?? contentTypeValue;
        user.AvatarSizeBytes = stored.SizeBytes;
        user.AvatarUpdatedAt = DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        await _db.SaveChangesAsync(ct);

        // Успешная замена: старый файл удаляем best-effort.
        if (oldKey != null && oldKey != storageKey)
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

        if (targetUserId == actorId)
            return new ResetUserPasswordResult(false, "Свой пароль меняется в разделе «Мой аккаунт».");

        var target = await _userManager.FindByIdAsync(targetUserId);
        if (target is null)
            return new ResetUserPasswordResult(false, "Пользователь не найден.");
        if (!target.IsActive || target.IsDeleted)
            return new ResetUserPasswordResult(false, "Учётная запись неактивна.");

        var remove = await _userManager.RemovePasswordAsync(target);
        if (!remove.Succeeded)
        {
            var message = string.Join(" ", remove.Errors.Select(e => e.Description));
            return new ResetUserPasswordResult(false,
                string.IsNullOrWhiteSpace(message) ? "Не удалось установить временный пароль." : message);
        }

        var add = await _userManager.AddPasswordAsync(target, password);
        if (!add.Succeeded)
        {
            var message = string.Join(" ", add.Errors.Select(e => e.Description));
            return new ResetUserPasswordResult(false,
                string.IsNullOrWhiteSpace(message) ? "Не удалось установить временный пароль." : message);
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

    private static async Task<int> ReadAtLeastAsync(
        Stream source, byte[] buffer, int max = 0, CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    private static bool LooksLikeSupportedImage(ReadOnlySpan<byte> header)
    {
        // JPEG: FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return true;
        // WEBP: RIFF....WEBP
        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return true;
        return false;
    }

    private static string ContentTypeFromExtension(string extension) => extension switch
    {
        ".jpg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

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
