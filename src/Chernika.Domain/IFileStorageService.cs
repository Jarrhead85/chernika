namespace Chernika.Domain;

public sealed record FileStoreResult(string StorageKey, long SizeBytes, string Sha256);

public interface IFileStorageService
{
    Task<FileStoreResult> SaveAsync(Stream content, string storageKey, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
