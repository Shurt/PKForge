using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PKHeX.Core;

namespace PKForge.App.Services;

/// <summary>
/// The projectpokemon EventsGallery, packed per generation at build time by
/// tools/EventGen. All supported generations are
/// extracted once and handed to PKHeX's local event tables (EGDB),
/// which the event service merges with the embedded database (MGDB).
/// </summary>
public static class EventArchive
{
    private static bool _loaded;
    private static readonly Lock Gate = new();

    public static void EnsureLoaded(int generation)
    {
        if (generation is < 4 or > 9) return;
        EnsureAllLoaded();
    }

    public static bool EnsureAllLoaded(CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            if (_loaded) return true;
            try
            {
                // Scanning the Bank/shelf must retain all origin generations, including gifts
                // transferred to newer saves. A package hash avoids using an old extracted pack.
                var bundles = new Dictionary<int, byte[]>();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var generation = 4; generation <= 9; generation++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var asset = FileSystem.OpenAppPackageFileAsync($"events/events-g{generation}.bin.gz")
                        .GetAwaiter().GetResult();
                    using var buffer = new MemoryStream();
                    asset.CopyTo(buffer);
                    var bytes = buffer.ToArray();
                    hash.AppendData(bytes);
                    bundles.Add(generation, bytes);
                }
                var target = Path.Combine(FileSystem.CacheDirectory, "events", "collection-v1",
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
                foreach (var (generation, bytes) in bundles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Extract(generation, bytes, target, cancellationToken);
                }
                EncounterEvent.RefreshMGDB(target);
                PKForge.Engine.EventDatabaseService.IndexArchive(target);
                _loaded = true;
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Console.WriteLine($"PKForge EventArchive: {error}");
                // A missing or corrupt archive must never break the wonder card menu;
                // the embedded database still works.
                return false;
            }
        }
    }

    private static void Extract(int generation, byte[] bundle, string root, CancellationToken cancellationToken)
    {
        var target = Path.Combine(root, "Released", $"Gen {generation}");
        var marker = target + ".complete";
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(target);

        using var asset = new MemoryStream(bundle, writable: false);
        using var gz = new GZipStream(asset, CompressionMode.Decompress);
        using var reader = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);
        var count = reader.ReadInt32();
        if (count is < 0 or > 10_000) throw new InvalidDataException("Invalid event archive count.");
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameLength = reader.ReadUInt16();
            if (nameLength is < 1 or > 2_000) throw new InvalidDataException("Invalid event archive path.");
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            var length = reader.ReadInt32();
            if (length is < 1 or > 1_048_576) throw new InvalidDataException("Invalid event archive file size.");
            var data = reader.ReadBytes(length);
            if (data.Length != length) throw new EndOfStreamException("Truncated event archive.");

            var path = Path.GetFullPath(Path.Combine(target, name));
            if (!path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Event archive path escapes its generation.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        File.WriteAllText(marker, count.ToString());
    }
}
