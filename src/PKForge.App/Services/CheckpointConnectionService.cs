using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>The console address survives app restarts; the receive PIN is never stored.</summary>
public sealed class CheckpointConnectionService : IDisposable
{
    private const string AddressKey = "checkpoint_3ds_address";
    private readonly HttpClient _http = CheckpointTransferClient.CreateHttpClient();

    public string Address => Preferences.Default.Get(AddressKey, "");

    public void SetAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) Preferences.Default.Remove(AddressKey);
        else Preferences.Default.Set(AddressKey, CheckpointTransferClient.NormalizeAddress(address));
    }

    public Task<CheckpointTransferResult> SendAsync(string address, string pin,
        ReadOnlyMemory<byte> data, string titleId, string titleName, CancellationToken cancellationToken) =>
        new CheckpointTransferClient(_http).SendAsync(address, pin, data, titleId, titleName, cancellationToken);

    public void Dispose() => _http.Dispose();
}
