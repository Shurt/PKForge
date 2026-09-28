using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

public partial class BackupHistoryViewModel : ObservableObject
{
    private readonly IBackupService _backups;
    private readonly ISaveSessionService _sessions;
    private readonly ISafeSaveWriter _writer;
    private readonly ISaveFileAccess _access;
    private readonly BoxBrowserViewModel _boxBrowser;
    private readonly ISaveEngine _engine;
    private readonly IBankService _bank;

    public BackupHistoryViewModel(IBackupService backups, ISaveSessionService sessions, ISafeSaveWriter writer,
        ISaveFileAccess access, BoxBrowserViewModel boxBrowser, ISaveEngine engine, IBankService bank)
    {
        _engine = engine;
        _bank = bank;
        _backups = backups;
        _sessions = sessions;
        _writer = writer;
        _access = access;
        _boxBrowser = boxBrowser;
    }

    public ObservableCollection<BackupInfo> Backups { get; } = [];

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool CanRestore => _sessions.Current is not null || Backups.Any(x => !x.IsLegacy);

    [RelayCommand]
    private async Task LoadAsync()
    {
        Backups.Clear();
        foreach (var info in await _backups.ListAsync())
            Backups.Add(info);
        Status = Backups.Count == 0
            ? "No backups yet. One is created automatically before every write."
            : _sessions.Current is not null
                ? $"{Backups.Count} backup(s). Restore points only write back to their original save."
                : Backups.Any(x => !x.IsLegacy)
                    ? $"{Backups.Count} backup(s). Identified restore points can recover their original save without opening it first."
                    : $"{Backups.Count} legacy backup(s). Open the matching save to restore one.";
    }

    public string RestoreRoute(BackupInfo backup)
    {
        var source = backup.DisplayName ?? "Unnamed backup";
        var destination = _sessions.Current?.Document.DisplayName ?? backup.DisplayName ?? "Recorded source document";
        return $"Source: {source}\nDestination: {destination}\nRestore point: {backup.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC";
    }

    /// <summary>Returns why a restore cannot target the active document, or null when it is safe to offer confirmation.</summary>
    public string? RestoreRefusal(BackupInfo backup)
        => RestoreRefusal(backup, _sessions.Current);

    private static string? RestoreRefusal(BackupInfo backup, SaveSession? session)
    {
        var source = backup.DisplayName ?? "Unnamed backup";
        return backup.MatchRestoreTarget(session?.Document.DocumentId, session?.Snapshot) switch
        {
            BackupRestoreMatch.Allowed => null,
            BackupRestoreMatch.LegacyNeedsOpenDocument =>
                $"'{source}' is a legacy restore point and does not record its original file. Open the matching save first.",
            BackupRestoreMatch.DifferentDocument =>
                $"Cannot restore '{source}' into '{session!.Document.DisplayName}'. This restore point belongs to a different file.",
            BackupRestoreMatch.IncompatibleFormat =>
                $"Cannot restore '{source}' ({backup.Format}, generation {backup.Generation}) into " +
                $"'{session!.Document.DisplayName}' ({session!.Snapshot.Format}, generation {session!.Snapshot.Generation}).",
            _ => "This restore point cannot target the selected save.",
        };
    }

    public string LegacyRestoreWarning(BackupInfo backup) => backup.IsLegacy
        ? " This legacy restore point predates file identity tracking, so PKForge cannot prove it came from this exact file."
        : string.Empty;

    /// <summary>
    /// Hardcore mode's duplication check before a restore: the Pokémon the restore point
    /// holds that the open save no longer does (moved to the Bank, sent to another game or
    /// released since). Null when Hardcore is off, no save is open, or the restore point
    /// cannot be read - the caller then falls back to the generic warning.
    /// </summary>
    public async Task<RestoreResurrection?> CheckResurrectionAsync(BackupInfo backup)
    {
        if (!HardcoreMode.IsOn || _sessions.Current is not { } session) return null;
        try
        {
            var bytes = await _backups.ReadAsync(backup.BackupId);
            var then = await Task.Run(() => _engine.Open(bytes).Slots);
            var bank = _bank.GetAll().Select(entry => entry.Info).ToList();
            return RestoreResurrection.Detect(then, session.Snapshot.Slots, bank);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes the backup into its recorded document; the current bytes are backed up first.</summary>
    public async Task RestoreAsync(BackupInfo backup)
    {
        var openSession = _sessions.Current;
        if (RestoreRefusal(backup, openSession) is { } refusal)
        {
            Status = $"Restore refused: {refusal}";
            return;
        }

        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = $"Restoring {backup.BackupId[..13]}…";
            var bytes = await _backups.ReadAsync(backup.BackupId);
            var document = openSession?.Document ?? new PickedDocument(backup.DocumentId!, backup.DisplayName ?? "Recovered save");
            SaveSnapshot current;
            if (openSession is not null)
            {
                current = openSession.Snapshot;
            }
            else
            {
                // Recovery must also work when an interrupted provider write left the file
                // too damaged to open. The writer still preserves these exact current bytes.
                var currentBytes = await _access.ReadAsync(document.DocumentId);
                var parsedCurrent = true;
                try
                {
                    current = _engine.Open(currentBytes, document.DisplayName);
                }
                catch (InvalidDataException)
                {
                    // A damaged save has no trustworthy format to compare. Keep its raw bytes
                    // as the safety point and use the identified backup's recorded format.
                    parsedCurrent = false;
                    current = new SaveSnapshot(backup.Format, backup.Generation, currentBytes, [], document.DisplayName);
                }
                if (parsedCurrent && backup.MatchRestoreTarget(document.DocumentId, current) == BackupRestoreMatch.IncompatibleFormat)
                    throw new InvalidDataException(
                        $"The current '{document.DisplayName}' is {current.Format}, generation {current.Generation}; " +
                        $"this restore point is {backup.Format}, generation {backup.Generation}.");
            }

            if (!ReferenceEquals(_sessions.Current, openSession))
                throw new InvalidOperationException("The open save changed while the restore point was being prepared. Try again.");

            // A restore is the recovery path: a deliberate whole-file write, never slot-scoped.
            var receipt = await _writer.WriteScopedAsync(document.DocumentId, current, bytes,
                WriteScope.Everything,
                "Safety copy: the state right before this restore");
            if (receipt.Changed)
                _sessions.MarkWritten(document.DocumentId, bytes);

            try
            {
                await _sessions.OpenAsync(document);
                _boxBrowser.RefreshFromCurrentSession();
            }
            catch (Exception error) when (receipt.Changed)
            {
                _sessions.Close();
                await LoadAsync();
                Status = $"Restored '{document.DisplayName}', but PKForge could not reopen it: {error.Message}";
                return;
            }

            var completedStatus = receipt.Changed
                ? $"Restored. Previous state kept as restore point {receipt.BackupId[..13]}…"
                : "This restore point matches the current state - nothing was written.";
            await LoadAsync();
            Status = completedStatus;
        }
        catch (Exception error)
        {
            if (_sessions.Current is null) _boxBrowser.Disconnect();
            Status = $"Restore aborted: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
