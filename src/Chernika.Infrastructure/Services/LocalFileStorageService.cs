using Chernika.Domain;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Chernika.Infrastructure.Services;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _rootPath;
    private readonly ILogger<LocalFileStorageService> _logger;

    public LocalFileStorageService(IConfiguration config, ILogger<LocalFileStorageService> logger)
    {
        _logger = logger;
        _rootPath = config.GetValue<string>("FileStorage:RootPath")
            ?? throw new InvalidOperationException("FileStorage:RootPath is not configured.");
        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// Файл с зафиксированным ключом не перезаписывается.
    /// <para>
    /// <c>FileMode.Create</c> перезаписывал бы содержимое на месте, и замена
    /// рабочего вложения уничтожила бы сохранённое основание, на которое ссылается
    /// редакция. Новое вложение и так получает собственный ключ; повторная запись
    /// по существующему ключу означает либо ошибку генерации ключа, либо
    /// попытку подмены — в обоих случаях отказ правильнее.
    /// </para>
    /// </summary>
    public async Task<FileStoreResult> SaveAsync(Stream content, string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        if (File.Exists(fullPath))
            throw new InvalidOperationException(
                "Файл с таким ключом уже существует. Перезапись сохранённого файла запрещена.");

        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        // CreateNew вместо Create: не перезаписывать существующий файл.
        await using var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var sha256 = SHA256.Create();
        await using var cryptoStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write);
        await content.CopyToAsync(cryptoStream, ct);
        await cryptoStream.FlushFinalBlockAsync(ct);
        var hash = Convert.ToHexString(sha256.Hash!);
        // Размер читается до закрытия fileStream (cryptoStream при dispose закрывает его).
        var size = fileStream.Length;
        return new FileStoreResult(storageKey, size, hash);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Файл вложения не найден.", storageKey);

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult(stream);
    }

    /// <summary>
    /// Удаляет файл, если на него не ссылается ни одна зафиксированная редакция.
    /// <para>
    /// Файл, используемый редакцией, не удаляется при замене или удалении рабочего
    /// вложения: иначе редакция осталась бы со ссылкой на уже удалённый файл.
    /// Массовой очистки здесь нет — проверка выполняется на каждое удаление.
    /// </para>
    /// </summary>
    public async Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        if (await IsReferencedByRevisionAsync(storageKey, ct))
            return;

        var fullPath = GetFullPath(storageKey);
        if (File.Exists(fullPath))
        {
            try
            {
                await Task.Run(() => File.Delete(fullPath), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file {StorageKey}", storageKey);
            }
        }
    }

    /// <summary>
    /// Есть ли ссылка на файл в зафиксированных редакциях.
    /// <para>
    /// Снимок хранит ключ хранения, поэтому проверка выполняется по нему, без
    /// чтения содержимого файла.
    /// </para>
    /// </summary>
    private static async Task<bool> IsReferencedByRevisionAsync(string storageKey, CancellationToken ct)
    {
        // Отдельное подключение: сервис файлов не должен зависеть от текущей
        // транзакции вызывающего, а решение о сохранности файла принадлежит базе.
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(DatabaseConnection.Build(ResolveConfiguration()))
                .Options);

        return await db.HKCardRevisions
            .AsNoTracking()
            .AnyAsync(r => r.AttachmentStorageKey == storageKey, ct);
    }

    private static IConfiguration ResolveConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables();
        return builder.Build();
    }

    private string GetFullPath(string storageKey)
    {
        if (storageKey.Contains("..") || storageKey.StartsWith('/') || storageKey.StartsWith('\\'))
            throw new ArgumentException("Недопустимый путь хранения.");

        return Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar));
    }
}
