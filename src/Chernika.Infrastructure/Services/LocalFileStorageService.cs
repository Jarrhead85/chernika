using Chernika.Domain;
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

    public async Task<FileStoreResult> SaveAsync(Stream content, string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var sha256 = SHA256.Create();
        var cryptoStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write);
        await content.CopyToAsync(cryptoStream, ct);
        await cryptoStream.FlushAsync(ct);
        await cryptoStream.DisposeAsync();
        var hash = Convert.ToHexString(sha256.Hash!);
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

    public async Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
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

    private string GetFullPath(string storageKey)
    {
        if (storageKey.Contains("..") || storageKey.StartsWith('/') || storageKey.StartsWith('\\'))
            throw new ArgumentException("Недопустимый путь хранения.");

        return Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar));
    }
}
