using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Durable bank store: immutable raw .bin revisions plus an atomically-written JSON index
/// (tmp → rename, previous index and its data kept for recovery). Boxes are 30 slots and auto-grow.
/// </summary>
public sealed class FileBankService : IBankService
{
    /// <summary>Alias of <see cref="IBankService.SlotsPerBox"/>; the vault shape lives in the contract.</summary>
    public const int SlotsPerBox = IBankService.SlotsPerBox;

    private readonly string _root;
    private readonly Lock _gate = new();
    private List<BankEntry> _entries;
    private Dictionary<Guid, string> _dataFiles;
    private int _boxCount;
    private int _migrationVersion;
    private string? _lastGoodIndexJson;
    // Set when an index exists on disk but neither it nor its backup could be read: the bank
    // then stays read-only rather than overwrite the real index with an empty fallback.
    private readonly bool _indexUnreadable;

    public FileBankService(string rootDirectory)
    {
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
        (_entries, _dataFiles, _boxCount, _migrationVersion, _lastGoodIndexJson, _indexUnreadable) = LoadIndex();
    }

    public IReadOnlyList<BankEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    public int BoxCount
    {
        get { lock (_gate) return _boxCount; }
    }

    public BankEntry Add(byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            EnsureWritable();
            return Commit(() =>
            {
                var (box, slot) = FirstEmpty();
                var entry = new BankEntry(Guid.NewGuid(), box, slot, info, DateTimeOffset.UtcNow);
                var fileName = NewDataFileName(entry.Id);
                WriteDataFile(fileName, data);
                _dataFiles.Add(entry.Id, fileName);
                _entries.Add(entry);
                try { SaveIndex(); }
                catch
                {
                    try { File.Delete(Path.Combine(_root, fileName)); }
                    catch { /* an orphan .bin is harmless: the index never lists it */ }
                    throw;
                }
                return entry;
            });
        }
    }

    public byte[] GetData(Guid id)
    {
        lock (_gate)
        {
            if (_entries.All(e => e.Id != id))
                throw new InvalidOperationException("Unknown bank entry.");
            return File.ReadAllBytes(DataPath(id));
        }
    }

    public void Move(Guid id, int box, int slot)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) throw new InvalidOperationException("Unknown bank entry.");
            EnsureWritable();
            var occupant = _entries.FindIndex(e => e.Box == box && e.Slot == slot);
            var moving = _entries[index];
            Commit(() =>
            {
                if (occupant >= 0 && occupant != index)
                {
                    // Swap: the occupant takes the mover's old place.
                    _entries[occupant] = _entries[occupant] with { Box = moving.Box, Slot = moving.Slot };
                }
                _entries[index] = moving with { Box = box, Slot = slot };
                _boxCount = Math.Max(_boxCount, box + 1);
                SaveIndex();
                return 0;
            });
        }
    }

    public void Replace(Guid id, byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) throw new InvalidOperationException("Unknown bank entry.");
            EnsureWritable();
            var fileName = NewDataFileName(id);
            var path = Path.Combine(_root, fileName);
            try
            {
                // A replacement gets a new immutable file. The index backup therefore keeps
                // referring to the previous bytes if this index commit must be recovered.
                WriteDataFile(fileName, data);
                Commit(() =>
                {
                    _dataFiles[id] = fileName;
                    _entries[index] = _entries[index] with { Info = info };
                    SaveIndex();
                    return 0;
                });
            }
            catch
            {
                try { File.Delete(path); }
                catch { /* an unreferenced revision is harmless and can be collected later */ }
                throw;
            }
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            EnsureWritable();
            Commit(() =>
            {
                _entries.RemoveAt(index);
                _dataFiles.Remove(id);
                SaveIndex();
                return 0;
            });
        }
    }

    public int RemoveMany(IReadOnlyList<Guid> ids)
    {
        lock (_gate)
        {
            if (ids.Count == 0) return 0;
            var wanted = ids.ToHashSet();
            var releasing = _entries.Where(e => wanted.Contains(e.Id)).Select(e => e.Id).ToList();
            if (releasing.Count == 0) return 0;
            EnsureWritable();
            // All or nothing: a failed index write leaves every entry (and its bytes) in place.
            Commit(() =>
            {
                _entries.RemoveAll(e => wanted.Contains(e.Id));
                foreach (var id in releasing) _dataFiles.Remove(id);
                SaveIndex();
                return 0;
            });
            return releasing.Count;
        }
    }

    public int Place(IReadOnlyList<(Guid Id, int Box, int Slot)> placements)
    {
        lock (_gate)
        {
            if (placements.Count == 0) return 0;

            // Validate the whole batch before touching anything: a rejected batch must leave
            // the vault exactly as it was.
            var rows = new Dictionary<Guid, int>(placements.Count);
            var targets = new HashSet<(int Box, int Slot)>();
            foreach (var (id, box, slot) in placements)
            {
                if (!rows.TryAdd(id, _entries.FindIndex(e => e.Id == id)) || rows[id] < 0)
                    throw new InvalidOperationException("Place requires distinct, known bank entries.");
                if (box < 0 || slot < 0 || slot >= SlotsPerBox)
                    throw new ArgumentOutOfRangeException(nameof(placements), "Box or slot out of range.");
                if (!targets.Add((box, slot)))
                    throw new InvalidOperationException("Two entries would share one slot.");
            }
            foreach (var (_, box, slot) in placements)
            {
                var occupant = _entries.FirstOrDefault(e => e.Box == box && e.Slot == slot);
                if (occupant is not null && !rows.ContainsKey(occupant.Id))
                    throw new InvalidOperationException("Target slot is held by an entry that is not moving.");
            }

            EnsureWritable();
            return Commit(() =>
            {
                var moved = 0;
                foreach (var (id, box, slot) in placements)
                {
                    var index = rows[id];
                    var entry = _entries[index];
                    if (entry.Box == box && entry.Slot == slot) continue;
                    _entries[index] = entry with { Box = box, Slot = slot };
                    moved++;
                }
                _boxCount = Math.Max(_boxCount, placements.Max(p => p.Box) + 1);
                SaveIndex();
                return moved;
            });
        }
    }

    public int UpdateInfo(IReadOnlyList<(Guid Id, BankEntryInfo Info)> updates)
    {
        lock (_gate)
        {
            if (!updates.Any(u => _entries.FindIndex(e => e.Id == u.Id) is var i && i >= 0 && _entries[i].Info != u.Info))
                return 0;
            EnsureWritable();
            return Commit(() =>
            {
                var changed = 0;
                foreach (var (id, info) in updates)
                {
                    var index = _entries.FindIndex(e => e.Id == id);
                    if (index < 0 || _entries[index].Info == info) continue;
                    _entries[index] = _entries[index] with { Info = info };
                    changed++;
                }
                SaveIndex();
                return changed;
            });
        }
    }

    public int MigrationVersion
    {
        get { lock (_gate) return _migrationVersion; }
    }

    public int CompleteMigration(int version, IReadOnlyList<(Guid Id, BankEntryInfo Expected, BankEntryInfo Info)> updates)
    {
        lock (_gate)
        {
            if (version <= _migrationVersion) return 0;
            EnsureWritable();
            var previous = _migrationVersion;
            try
            {
                return Commit(() =>
                {
                    var changed = 0;
                    foreach (var (id, expected, info) in updates)
                    {
                        var index = _entries.FindIndex(e => e.Id == id);
                        if (index < 0 || _entries[index].Info != expected || expected == info) continue;
                        _entries[index] = _entries[index] with { Info = info };
                        changed++;
                    }
                    _migrationVersion = version;
                    SaveIndex();
                    return changed;
                });
            }
            catch
            {
                _migrationVersion = previous;
                throw;
            }
        }
    }

    public void AddBox()
    {
        lock (_gate)
        {
            EnsureWritable();
            Commit(() =>
            {
                _boxCount++;
                SaveIndex();
                return 0;
            });
        }
    }

    public void RemapBoxes(BankBoxRemap remap)
    {
        ArgumentNullException.ThrowIfNull(remap);
        lock (_gate)
        {
            EnsureWritable();
            remap.Validate(_boxCount);
            if (_entries.FirstOrDefault(e => remap.Map(e.Box) is null) is { } stranded)
                throw new InvalidOperationException($"Box {stranded.Box + 1} is not empty.");
            if (remap.IsIdentity) return;
            Commit(() =>
            {
                for (var i = 0; i < _entries.Count; i++)
                    _entries[i] = _entries[i] with { Box = remap.Map(_entries[i].Box)!.Value };
                _boxCount = remap.NewCount;
                SaveIndex();
                return 0;
            });
        }
    }

    private (int Box, int Slot) FirstEmpty()
    {
        var occupied = _entries.Select(e => (e.Box, e.Slot)).ToHashSet();
        for (var box = 0; box < _boxCount; box++)
        {
            for (var slot = 0; slot < SlotsPerBox; slot++)
            {
                if (!occupied.Contains((box, slot)))
                    return (box, slot);
            }
        }
        _boxCount++;
        return (_boxCount - 1, 0);
    }

    private string DataPath(Guid id) => Path.Combine(_root, DataFileName(id));
    private string DataFileName(Guid id) =>
        _dataFiles.TryGetValue(id, out var fileName) ? fileName : LegacyDataFileName(id);
    private static string LegacyDataFileName(Guid id) => id.ToString("N") + ".bin";
    private static string NewDataFileName(Guid id) =>
        $"{id:N}.{Guid.NewGuid():N}.bin";
    private string IndexPath => Path.Combine(_root, "index.json");

    private void WriteDataFile(string fileName, byte[] data)
    {
        using var stream = new FileStream(
            Path.Combine(_root, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    // MigrationVersion and DataFiles are absent from older indexes. Missing data-file mappings
    // keep using the original <entry-id>.bin layout.
    private sealed record IndexFile(
        int BoxCount,
        List<BankEntry> Entries,
        int MigrationVersion = 0,
        Dictionary<Guid, string>? DataFiles = null);

    /// <summary>Runs one mutation of the in-memory index (the caller holds the gate); if it throws,
    /// typically because the index write failed, memory is put back to match the disk.</summary>
    private T Commit<T>(Func<T> mutate)
    {
        var entries = _entries.ToList();
        var dataFiles = new Dictionary<Guid, string>(_dataFiles);
        var boxCount = _boxCount;
        try { return mutate(); }
        catch
        {
            _entries = entries;
            _dataFiles = dataFiles;
            _boxCount = boxCount;
            throw;
        }
    }

    private void EnsureWritable()
    {
        if (_indexUnreadable)
            throw new InvalidOperationException("The bank index could not be read, so the bank is read-only to protect it. Check the bank folder's index.json.");
    }

    private void SaveIndex()
    {
        EnsureWritable();
        var json = JsonSerializer.Serialize(new IndexFile(_boxCount, _entries, _migrationVersion, _dataFiles));
        var tmp = IndexPath + ".tmp";
        WriteDurableText(tmp, json);

        // Back up the JSON that this instance successfully loaded or committed. Copying the
        // current path could replace a good backup with a corrupt index we recovered from.
        if (_lastGoodIndexJson is not null)
        {
            var backupTmp = IndexPath + ".bak.tmp";
            WriteDurableText(backupTmp, _lastGoodIndexJson);
            File.Move(backupTmp, IndexPath + ".bak", overwrite: true);
        }

        File.Move(tmp, IndexPath, overwrite: true);
        _lastGoodIndexJson = json;
        TryDeleteUnreferencedDataFiles();
    }

    private static void WriteDurableText(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private (List<BankEntry>, Dictionary<Guid, string>, int, int, string? LastGoodJson, bool Unreadable) LoadIndex()
    {
        var anyIndex = false;
        foreach (var candidate in new[] { IndexPath, IndexPath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                anyIndex = true;
                var json = File.ReadAllText(candidate);
                var loaded = JsonSerializer.Deserialize<IndexFile>(json);
                if (loaded is not null)
                {
                    var entries = loaded.Entries ?? [];
                    var dataFiles = ValidateDataFiles(loaded, entries);
                    return (entries, dataFiles, Math.Max(1, loaded.BoxCount), loaded.MigrationVersion, json, false);
                }
            }
            catch
            {
                // Try the backup index next.
            }
        }
        // A fresh bank opens with three inviting boxes; an index that exists but cannot be
        // read is never replaced by that empty fallback.
        return ([], [], 3, 0, null, anyIndex);
    }

    private Dictionary<Guid, string> ValidateDataFiles(IndexFile index, IReadOnlyList<BankEntry> entries)
    {
        var files = index.DataFiles ?? [];
        var result = new Dictionary<Guid, string>();
        foreach (var entry in entries)
        {
            if (!files.TryGetValue(entry.Id, out var fileName)) fileName = LegacyDataFileName(entry.Id);
            if (!IsDataFileName(entry.Id, fileName) || !File.Exists(Path.Combine(_root, fileName)))
                throw new InvalidDataException("The bank index refers to missing or invalid entity data.");
            result.Add(entry.Id, fileName);
        }
        return result;
    }

    private static bool IsDataFileName(Guid id, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)) return false;
        if (string.Equals(fileName, LegacyDataFileName(id), StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = id.ToString("N") + ".";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return false;
        return Guid.TryParseExact(fileName[prefix.Length..^4], "N", out _);
    }

    private void TryDeleteUnreferencedDataFiles()
    {
        try
        {
            var retained = _entries.Select(e => DataFileName(e.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var backupPath = IndexPath + ".bak";
            if (File.Exists(backupPath))
            {
                var backup = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(backupPath));
                if (backup is null) return;
                foreach (var entry in backup.Entries ?? [])
                {
                    var fileName = backup.DataFiles?.GetValueOrDefault(entry.Id) ?? LegacyDataFileName(entry.Id);
                    if (!IsDataFileName(entry.Id, fileName)) return;
                    retained.Add(fileName);
                }
            }

            foreach (var path in Directory.EnumerateFiles(_root, "*.bin"))
            {
                if (!retained.Contains(Path.GetFileName(path))) File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is optional. Keeping an old or orphaned revision is safer than making
            // an otherwise successful bank operation fail after its index was committed.
        }
    }
}
