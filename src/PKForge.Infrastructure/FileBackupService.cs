using System.Security.Cryptography;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Durable backup store: raw save bytes plus a JSON metadata sidecar per version,
/// under an app-private directory. Oldest versions beyond <see cref="_maxVersions"/> are pruned
/// per source document. Legacy backups without a document id are retained.
/// </summary>
public sealed class FileBackupService(string rootDirectory, int maxVersions = 20) : IBackupService
{
    private readonly string _root = rootDirectory;
    private readonly int _maxVersions = maxVersions > 0
        ? maxVersions
        : throw new ArgumentOutOfRangeException(nameof(maxVersions), "Backup retention must keep at least one version.");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, CancellationToken cancellationToken = default)
        => await CreateAsync(source, changeDescription, cancellationToken, documentId: null).ConfigureAwait(false);

    public async ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription, CancellationToken cancellationToken, string? documentId)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(_root);

        var createdUtc = DateTimeOffset.UtcNow;
        var id = $"{createdUtc:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var bytes = source.OriginalBytes.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var info = new BackupInfo(id, createdUtc, sha, source.DisplayName, source.Format, source.Generation,
            bytes.LongLength, changeDescription, documentId);

        // Bytes first, sidecar last: a backup without a sidecar is ignored, never half-trusted.
        // Flush both files before publishing the sidecar so a successful receipt means the
        // restore point reached durable storage before the live save may be touched.
        await WriteDurableAsync(BytesPath(id), bytes, cancellationToken).ConfigureAwait(false);
        var sidecarTemp = SidecarPath(id) + ".tmp";
        await WriteDurableAsync(sidecarTemp, JsonSerializer.SerializeToUtf8Bytes(info, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(sidecarTemp, SidecarPath(id));

        // Retention is housekeeping after the new restore point exists. A locked or damaged
        // old file must not make callers think this backup failed and write without it.
        TryPrune(documentId, id);
        return new BackupReceipt(id, createdUtc, sha);
    }

    public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<BackupInfo>>(ReadAll());
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string backupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        if (backupId.Contains('/') || backupId.Contains('\\') || backupId.Contains(".."))
            throw new ArgumentException("Invalid backup id.", nameof(backupId));

        var info = ReadSidecar(SidecarPath(backupId))
            ?? throw new FileNotFoundException($"Backup {backupId} not found.");
        var bytes = await File.ReadAllBytesAsync(BytesPath(backupId), cancellationToken).ConfigureAwait(false);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Backup {backupId} is corrupt: stored hash does not match its bytes.");
        return bytes;
    }

    private List<BackupInfo> ReadAll()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateFiles(_root, "*.json")
            .Select(ReadSidecar)
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
    }

    private void TryPrune(string? documentId, string preserveId)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return;

        try
        {
            foreach (var stale in ReadAll()
                         .Where(x => x.IsForDocument(documentId))
                         .Where(x => x.BackupId != preserveId)
                         .Skip(_maxVersions - 1))
            {
                // Hide the restore point first. If deleting its bytes then fails, only an
                // ignored orphan remains instead of a listed restore point with no data.
                File.Delete(SidecarPath(stale.BackupId));
                File.Delete(BytesPath(stale.BackupId));
            }
        }
        catch (IOException)
        {
            // A later create retries retention. The new backup remains valid and listed.
        }
        catch (UnauthorizedAccessException)
        {
            // Treat retention as best effort; never invalidate the backup just created.
        }
    }

    private static async Task WriteDurableAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static BackupInfo? ReadSidecar(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string BytesPath(string id) => Path.Combine(_root, id + ".bin");
    private string SidecarPath(string id) => Path.Combine(_root, id + ".json");
}
