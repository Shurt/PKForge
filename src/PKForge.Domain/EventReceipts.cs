namespace PKForge.Domain;

/// <summary>Local provenance of a generated entity, independent of its save or box slot.</summary>
public sealed record EventReceipt(IReadOnlyList<string> SourcePaths, bool Ambiguous = false);

public interface IEventReceiptStore
{
    bool IsAvailable { get; }
    EventReceipt? Find(string entityFingerprint);
    void Record(string entityFingerprint, IReadOnlyList<string> sourcePaths);
}

public static class EventObservationRules
{
    public const int MaxMatches = 100;

    public static bool IsFingerprint(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsSourcePath(string? value)
    {
        if (value is null || value.Length > 1_000 || !value.StartsWith("Released/Gen ", StringComparison.Ordinal)
            || value.Length < 16 || value[14] != '/' || value[13] is < '1' or > '9'
            || value.Contains('\\') || value.Contains('\0')) return false;
        return value.Split('/').All(part => part.Length > 0 && part is not "." and not "..");
    }
}
