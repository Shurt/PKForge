using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>One .pk file in an exported archive folder, described for humans and tools.</summary>
public sealed record BankArchiveEntry(
    string File,
    int Species,
    string Nickname,
    bool Shiny,
    int Generation,
    int Box,
    int Slot,
    string Sha256);

/// <summary>
/// The manifest.json written beside the .pk files. An interchange format, not an internal
/// index: camelCase so PKHeX users can read it in any file manager, per-file SHA-256 so a
/// transfer can be verified. (The full bank-as-ZIP model lives in docs/BANK_MODEL.md; this
/// is its loose-folder shipping subset.)
/// </summary>
public sealed record BankArchiveManifest(
    string App,
    int SchemaVersion,
    DateTimeOffset ExportedUtc,
    IReadOnlyList<BankArchiveEntry> Entries);

/// <summary>Import outcome: what joined the bank and what was left behind, and why.</summary>
public sealed record BankArchiveImportResult(int Imported, int SkippedDuplicates, int Rejected);

/// <summary>
/// Bank archive export/import over a picked folder: one .pkN file per mon (species number,
/// nickname, short id — unique even for clones) plus manifest.json. Import merges into the
/// bank, skipping exact byte copies (SHA-256) of mons already stored or already imported
/// this batch. A manifest limits import to its verified members; without one, recognized
/// loose .pk files are imported directly.
/// </summary>
public static class BankArchive
{
    public const string ManifestFileName = "manifest.json";
    public const int CurrentSchemaVersion = 1;
    private const string AppName = "PKForge";

    /// <summary>camelCase on purpose: the manifest is read outside the app too.</summary>
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task<int> ExportAsync(
        IBankService bank,
        IFolderFileAccess files,
        string treeId,
        IReadOnlyList<BankEntry>? entries = null,
        CancellationToken cancellationToken = default)
    {
        var list = entries ?? bank.GetAll();
        var payloads = new (string Name, byte[] Bytes)[list.Count];
        var manifestEntries = new BankArchiveEntry[list.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = list[i];
            var bytes = bank.GetData(entry.Id);
            var name = FileNameFor(entry);
            if (!names.Add(name))
                throw new InvalidDataException($"The export contains the file name '{name}' more than once.");
            payloads[i] = (name, bytes);
            manifestEntries[i] = new BankArchiveEntry(
                name, entry.Info.Species, entry.Info.Nickname, entry.Info.Shiny,
                entry.Info.Generation, entry.Box, entry.Slot, Sha256Hex(bytes));
        }

        // Invalidate any previous manifest first. If the export is interrupted, a later
        // import will stop instead of treating a mixture of old and new files as valid.
        await files.WriteFileAsync(
            treeId,
            ManifestFileName,
            Encoding.UTF8.GetBytes($"{{\"app\":\"{AppName}\",\"schemaVersion\":{CurrentSchemaVersion},\"exportIncomplete\":true}}"),
            cancellationToken).ConfigureAwait(false);

        foreach (var payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await files.WriteFileAsync(treeId, payload.Name, payload.Bytes, cancellationToken).ConfigureAwait(false);
        }
        var manifest = new BankArchiveManifest(AppName, CurrentSchemaVersion, DateTimeOffset.UtcNow, manifestEntries);
        var json = JsonSerializer.Serialize(manifest, Json);
        await files.WriteFileAsync(treeId, ManifestFileName, Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
        return list.Count;
    }

    /// <summary>
    /// Imports the verified manifest members, or every recognized .pk file when the folder has
    /// no manifest. <paramref name="describe"/> is the engine's loose-entity probe (passed as a
    /// delegate so the archive stays engine-free).
    /// </summary>
    public static async Task<BankArchiveImportResult> ImportAsync(
        IBankService bank,
        Func<byte[], string, BankEntryInfo?> describe,
        IFolderFileAccess files,
        string treeId,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folderFiles = await files.ListFilesAsync(treeId, cancellationToken).ConfigureAwait(false);
        var manifests = folderFiles
            .Where(f => f.DisplayName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifests.Length > 1)
            throw new InvalidDataException("The archive contains more than one manifest.json file.");

        var candidates = manifests.Length == 1
            ? await ReadManifestCandidatesAsync(files, folderFiles, manifests[0], cancellationToken).ConfigureAwait(false)
            : await ReadLooseCandidatesAsync(files, folderFiles, cancellationToken).ConfigureAwait(false);

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in bank.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            known.Add(Sha256Hex(bank.GetData(entry.Id)));
        }

        var imported = 0;
        var skipped = 0;
        var rejected = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(i, candidates.Length);
            var candidate = candidates[i];
            if (describe(candidate.Bytes, candidate.Name) is not { } info)
            {
                rejected++;
                continue;
            }
            if (!known.Add(Sha256Hex(candidate.Bytes)))
            {
                skipped++;
                continue;
            }
            bank.Add(candidate.Bytes, info);
            imported++;
        }
        progress?.Invoke(candidates.Length, candidates.Length);
        return new BankArchiveImportResult(imported, skipped, rejected);
    }

    private static async Task<ArchiveCandidate[]> ReadLooseCandidatesAsync(
        IFolderFileAccess files,
        IReadOnlyList<PickedDocument> folderFiles,
        CancellationToken cancellationToken)
    {
        var documents = folderFiles.Where(f => IsPkFileName(f.DisplayName)).ToArray();
        var candidates = new ArchiveCandidate[documents.Length];
        for (var i = 0; i < documents.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await files.ReadFileAsync(documents[i].DocumentId, cancellationToken).ConfigureAwait(false);
            candidates[i] = new ArchiveCandidate(documents[i].DisplayName, bytes.ToArray());
        }
        return candidates;
    }

    private static async Task<ArchiveCandidate[]> ReadManifestCandidatesAsync(
        IFolderFileAccess files,
        IReadOnlyList<PickedDocument> folderFiles,
        PickedDocument manifestDocument,
        CancellationToken cancellationToken)
    {
        BankArchiveManifest manifest;
        try
        {
            var manifestBytes = await files.ReadFileAsync(manifestDocument.DocumentId, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<BankArchiveManifest>(manifestBytes.ToArray(), Json)
                ?? throw new InvalidDataException("The archive manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The archive manifest is not valid JSON.", ex);
        }

        if (!string.Equals(manifest.App, AppName, StringComparison.Ordinal)
            || manifest.SchemaVersion != CurrentSchemaVersion
            || manifest.ExportedUtc == default
            || manifest.Entries is null)
        {
            throw new InvalidDataException("The archive manifest is incomplete or uses an unsupported schema.");
        }

        var entriesByName = new Dictionary<string, BankArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (entry is null)
                throw new InvalidDataException("The archive manifest contains an empty entry.");
            if (!IsSafeArchiveFileName(entry.File) || !IsPkFileName(entry.File))
                throw new InvalidDataException($"The archive manifest contains an unsafe or unsupported file name: '{entry.File}'.");
            if (!IsSha256(entry.Sha256))
                throw new InvalidDataException($"The archive manifest contains an invalid checksum for '{entry.File}'.");
            if (!entriesByName.TryAdd(entry.File, entry))
                throw new InvalidDataException($"The archive manifest lists '{entry.File}' more than once.");
        }

        var documentsByName = folderFiles
            .Where(f => !f.DisplayName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.DisplayName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var candidates = new ArchiveCandidate[manifest.Entries.Count];
        for (var i = 0; i < manifest.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest.Entries[i];
            if (!documentsByName.TryGetValue(entry.File, out var matches) || matches.Length != 1)
                throw new InvalidDataException($"The archive member '{entry.File}' is missing or ambiguous.");

            var bytes = (await files.ReadFileAsync(matches[0].DocumentId, cancellationToken).ConfigureAwait(false)).ToArray();
            if (!Sha256Hex(bytes).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The archive member '{entry.File}' does not match its checksum.");
            candidates[i] = new ArchiveCandidate(entry.File, bytes);
        }
        return candidates;
    }

    private static bool IsSafeArchiveFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName is not "." and not ".."
        && !fileName.Contains('/')
        && !fileName.Contains('\\')
        && !Path.IsPathRooted(fileName)
        && Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal);

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record ArchiveCandidate(string Name, byte[] Bytes);

    /// <summary>File name for one entry: "025 - Sparky a1b2c3d4.pk7". The short id keeps
    /// clones and same-named siblings distinct; the generation picks the .pkN extension.</summary>
    public static string FileNameFor(BankEntry entry)
    {
        var id = entry.Id.ToString("N")[..8];
        var nickname = SanitizeFileName(entry.Info.Nickname);
        var extension = BankEntryFiles.ExtensionFor(entry.Info);
        return nickname.Length == 0
            ? $"{entry.Info.Species:000} {id}{extension}"
            : $"{entry.Info.Species:000} - {nickname} {id}{extension}";
    }

    /// <summary>True for .pk, .pk1 through .pk9 and the PKHeX side formats the bank records
    /// (.pb7, .pb8, .pa8, .pa9, .sk2, .ck3, .xk3, .bk4, .rk4), case-insensitive — the cheap
    /// prefilter that keeps an import scan from parsing a folder of arbitrary files.</summary>
    public static bool IsPkFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".pk" or ".pk1" or ".pk2" or ".pk3" or ".pk4"
            or ".pk5" or ".pk6" or ".pk7" or ".pk8" or ".pk9"
            or ".pb7" or ".pb8" or ".pa8" or ".pa9" or ".sk2" or ".ck3" or ".xk3" or ".bk4" or ".rk4";
    }

    /// <summary>Strips filesystem-hostile characters (SAF display names reject them too) and caps length.</summary>
    public static string SanitizeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Where(c => !invalid.Contains(c)).ToArray());
        return cleaned.Trim().Length > 20 ? cleaned.Trim()[..20].Trim() : cleaned.Trim();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
