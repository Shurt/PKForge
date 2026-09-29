using System.Text.Json;
using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>Opt-in manual sync. Persist the exact pending request before networking so a
/// timeout or restart retries the same sequence rather than duplicating observations.</summary>
public sealed class PokedexConnectionService
{
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "pokedex-connection.json");
    private readonly PokedexConnectionClient _client = new(PokedexConnectionClient.CreateHttpClient());
    private bool _syncing;

    public PokedexConnectionState State { get; private set; } = new();
    public bool StorageError { get; private set; }

    public PokedexConnectionService()
    {
        if (!File.Exists(_path)) return;
        try
        {
            State = JsonSerializer.Deserialize<PokedexConnectionState>(File.ReadAllText(_path), PokedexConnectionJson.Options)
                ?? throw new InvalidDataException("Empty connection state.");
            _ = PokedexConnectionClient.NormalizeServerUrl(State.ServerUrl);
            if (!Guid.TryParse(State.DeviceId, out _) || State.LastSequence < 0)
                throw new InvalidDataException("Invalid connection identity.");
            if (State.Pending is { } pending)
            {
                PokedexConnectionProtocol.ValidateRequest(pending);
                if (pending.Sequence != State.LastSequence || pending.DeviceId != State.DeviceId)
                    throw new InvalidDataException("Pending sync does not match the saved identity.");
            }
            if (State.Cache is { } cache) PokedexConnectionProtocol.ValidateResponse(cache);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or PokedexProtocolException)
        {
            StorageError = true;
            State = new() { LastError = "The saved connection could not be loaded. Reset the local connection to configure it again." };
        }
    }

    public void SetEnabled(bool enabled) => Save(State with { Enabled = enabled });
    public void SetServerUrl(string url) => Save(State.ChangeServerUrl(url));

    public void Reset()
    {
        Persist(new PokedexConnectionState());
        StorageError = false;
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken)
    {
        if (!State.Enabled) throw new InvalidOperationException("Enable the connection before syncing.");
        if (_syncing) throw new InvalidOperationException("A sync is already running.");
        _syncing = true;
        try
        {
            if (State.Pending is null)
            {
                var observedAt = DateTimeOffset.UtcNow;
                var sources = await PokedexCollectionScanner.ScanAsync(cancellationToken);
                Save(State.QueueSnapshot(DeviceInfo.Name, observedAt, sources));
            }
            var response = await _client.SyncAsync(State.ServerUrl, State.Enabled, State.Pending!, cancellationToken);
            Save(State.Accept(response));
        }
        catch (Exception error)
        {
            Save(State.RecordFailure(error is OperationCanceledException
                ? "Sync cancelled. Any pending snapshot is kept for retry."
                : "Sync failed. Check the service and LAN/Tailscale connection. " + error.Message));
            throw;
        }
        finally { _syncing = false; }
    }

    private void Save(PokedexConnectionState next)
    {
        if (StorageError) throw new InvalidOperationException(State.LastError);
        Persist(next);
    }

    private void Persist(PokedexConnectionState next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, PokedexConnectionJson.Options);
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
        State = next;
    }
}
