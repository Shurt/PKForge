using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>Receipts stay on the device. A damaged ledger is never overwritten with an empty one.</summary>
public sealed class JsonEventReceiptStore : IEventReceiptStore
{
    private readonly string? _path;
    private readonly Lock _gate = new();
    private Dictionary<string, EventReceipt> _receipts = new(StringComparer.Ordinal);
    public bool IsAvailable { get; private set; } = true;

    public JsonEventReceiptStore(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var receipts = JsonSerializer.Deserialize<Dictionary<string, EventReceipt>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty event receipt ledger.");
            foreach (var (fingerprint, receipt) in receipts)
            {
                if (!EventObservationRules.IsFingerprint(fingerprint) || receipt?.SourcePaths is null
                    || receipt.SourcePaths.Count is < 1 or > EventObservationRules.MaxMatches
                    || receipt.SourcePaths.Any(p => p is null || !EventObservationRules.IsSourcePath(p)))
                    throw new InvalidDataException("Invalid event receipt ledger.");
            }
            _receipts = receipts;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            IsAvailable = false;
        }
    }

    public EventReceipt? Find(string entityFingerprint)
    {
        lock (_gate)
        {
            return _receipts.TryGetValue(entityFingerprint, out var receipt)
                ? receipt with { SourcePaths = receipt.SourcePaths.ToArray() } : null;
        }
    }

    public void Record(string entityFingerprint, IReadOnlyList<string> sourcePaths)
    {
        if (!EventObservationRules.IsFingerprint(entityFingerprint)) throw new ArgumentException("Invalid entity fingerprint.", nameof(entityFingerprint));
        if (sourcePaths.Count == 0 || sourcePaths.Any(p => !EventObservationRules.IsSourcePath(p)))
            throw new ArgumentException("Receipts require exact Gallery paths.", nameof(sourcePaths));
        lock (_gate)
        {
            if (!IsAvailable) throw new IOException("The event receipt ledger could not be read.");
            var previous = _receipts.GetValueOrDefault(entityFingerprint);
            var paths = (previous?.SourcePaths ?? []).Concat(sourcePaths)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var receipt = new EventReceipt(paths.Take(EventObservationRules.MaxMatches).ToArray(),
                previous?.Ambiguous == true || paths.Length > 1);
            var next = new Dictionary<string, EventReceipt>(_receipts, StringComparer.Ordinal)
            {
                [entityFingerprint] = receipt,
            };
            if (_path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                var temporary = _path + ".tmp";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            _receipts = next;
        }
    }
}
