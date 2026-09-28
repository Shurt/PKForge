namespace PKForge.Domain;

/// <summary>The document changed outside this editing session. No replacement was attempted.</summary>
public sealed class SaveConflictException() : IOException(
    "This save changed since it was opened. Nothing was overwritten. Close the emulator and reopen the save before editing again.");

/// <summary>A durable restore point exists, but the replacement could not be verified.</summary>
public sealed class SaveWriteFailedException(string backupId, Exception innerException) : IOException(
    $"The save write could not be verified. Stop editing and close the emulator. " +
    $"Restore point {backupId} preserves the previous bytes. Open Restore points to recover it, even if the save no longer opens.", innerException)
{
    public string BackupId { get; } = backupId;
}
