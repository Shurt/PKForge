using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PKForge.Infrastructure;

/// <summary>Sends a prepared 3DS save to Checkpoint's wireless receiver as a new backup.</summary>
public sealed class CheckpointTransferClient : IDisposable
{
    public const int DefaultPort = 8000;
    public const string PokemonXTitleId = "0004000000055D00";
    public const string PokemonYTitleId = "0004000000055E00";

    private const int AssumedUploadLimit = 32 * 1024 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public CheckpointTransferClient() : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    public CheckpointTransferClient(HttpClient httpClient) : this(httpClient, ownsHttpClient: false)
    {
    }

    private CheckpointTransferClient(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>Creates a client which does not redirect an authenticated upload or use a configured proxy.</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = RequestTimeout,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Validates and canonicalizes an IPv4 address with an optional port.</summary>
    public static string NormalizeAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var value = address.Trim();
        var colon = value.LastIndexOf(':');
        var host = colon >= 0 ? value[..colon] : value;
        var portText = colon >= 0 ? value[(colon + 1)..] : null;

        if (!IsDottedDecimalIPv4(host) ||
            !IPAddress.TryParse(host, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Enter the 3DS IPv4 address, such as 192.168.1.42.", nameof(address));

        var port = DefaultPort;
        if (portText is not null &&
            (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            throw new ArgumentException("The Checkpoint port must be between 1 and 65535.", nameof(address));
        }

        var canonicalHost = parsed.ToString();
        return port == DefaultPort ? canonicalHost : $"{canonicalHost}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool IsValidPin(string? pin) =>
        pin is { Length: 4 } && pin.All(character => character is >= '0' and <= '9');

    public static void ValidatePin(string pin)
    {
        if (!IsValidPin(pin))
            throw new ArgumentException("The Checkpoint PIN must be exactly four digits.", nameof(pin));
    }

    /// <summary>
    /// Sends save bytes as a raw file named <c>main</c>. Checkpoint stores the file in a new backup;
    /// restoring that backup remains an explicit action on the console.
    /// </summary>
    public async Task<CheckpointTransferResult> SendAsync(
        string address,
        string pin,
        ReadOnlyMemory<byte> saveBytes,
        string titleId,
        string titleName,
        CancellationToken cancellationToken = default)
    {
        var normalizedAddress = NormalizeAddress(address);
        ValidatePin(pin);
        ValidateTitle(titleId, titleName);
        if (saveBytes.IsEmpty)
            throw new ArgumentException("The save is empty.", nameof(saveBytes));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await SendCoreAsync(
                normalizedAddress, pin, saveBytes, titleId, titleName, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new CheckpointTransferException("The Checkpoint transfer timed out after 30 seconds.", ex);
        }
    }

    private async Task<CheckpointTransferResult> SendCoreAsync(
        string normalizedAddress,
        string pin,
        ReadOnlyMemory<byte> saveBytes,
        string titleId,
        string titleName,
        CancellationToken cancellationToken)
    {
        var baseUri = CreateBaseUri(normalizedAddress);
        var receiver = await FetchReceiverInfoAsync(baseUri, cancellationToken).ConfigureAwait(false);
        ValidateReceiver(receiver);

        var uploadLimit = receiver.MaxUploadBytes == 0 ? AssumedUploadLimit : receiver.MaxUploadBytes;
        if (saveBytes.Length > uploadLimit)
        {
            throw new CheckpointTransferException(
                $"The save is {saveBytes.Length:N0} bytes, but Checkpoint accepts at most {uploadLimit:N0} bytes.");
        }

        var backupName = CreateBackupName();
        var metadata = new UploadMetadata(
            titleId.ToUpperInvariant(),
            titleName.Trim(),
            "save",
            backupName,
            IsZip: false,
            saveBytes.Length,
            "main",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        using var request = CreateUploadRequest(baseUri, pin, metadata, saveBytes);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var upload = await ReadResponseAsync(
            response, CheckpointTransferJsonContext.Default.UploadResponse, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new CheckpointTransferException(
                $"Checkpoint rejected the PIN. {ResponseDetail(upload?.Error, response)}".TrimEnd());
        }
        if (!response.IsSuccessStatusCode || upload?.Ok != true)
        {
            throw new CheckpointTransferException(
                $"Checkpoint rejected the transfer with HTTP {(int)response.StatusCode}. {ResponseDetail(upload?.Error, response)}".TrimEnd());
        }
        if (string.IsNullOrWhiteSpace(upload.SavedPath) || !SavedAsNamedBackup(upload.SavedPath, backupName))
            throw new CheckpointTransferException("Checkpoint did not confirm where it stored the new backup.");

        return new CheckpointTransferResult(backupName, upload.SavedPath, receiver.Device, receiver.Version);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task<ReceiverInfo> FetchReceiverInfoAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "transfer/info"));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CheckpointTransferException(
                $"Checkpoint's transfer check returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "Unknown"}).");
        }

        var info = await ReadResponseAsync(
            response, CheckpointTransferJsonContext.Default.ReceiverInfo, cancellationToken).ConfigureAwait(false);
        return info ?? throw new CheckpointTransferException("Checkpoint returned an empty transfer description.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CheckpointTransferException("PKForge could not reach Checkpoint on the 3DS.", ex);
        }
    }

    private static HttpRequestMessage CreateUploadRequest(
        Uri baseUri,
        string pin,
        UploadMetadata metadata,
        ReadOnlyMemory<byte> saveBytes)
    {
        // Match Checkpoint 5.2.0's chlink sender, including the case-sensitive token header and raw
        // single-file payload: https://github.com/BernardoGiordano/Checkpoint/blob/v5.2.0/tools/chlink/send.go
        var boundary = "----pkforge-boundary-" + Guid.NewGuid().ToString("N");
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(
            metadata, CheckpointTransferJsonContext.Default.UploadMetadata);
        var firstHeader = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"meta\"\r\nContent-Type: application/json\r\n\r\n");
        var fileHeader = Encoding.ASCII.GetBytes(
            $"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"main\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        var end = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");

        var body = new byte[firstHeader.Length + metadataBytes.Length + fileHeader.Length + saveBytes.Length + end.Length];
        var offset = 0;
        firstHeader.CopyTo(body, offset);
        offset += firstHeader.Length;
        metadataBytes.CopyTo(body, offset);
        offset += metadataBytes.Length;
        fileHeader.CopyTo(body, offset);
        offset += fileHeader.Length;
        saveBytes.CopyTo(body.AsMemory(offset));
        offset += saveBytes.Length;
        end.CopyTo(body, offset);

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "transfer/upload"))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("X-CP-Token", pin);
        request.Headers.ConnectionClose = true;
        return request;
    }

    private static async Task<T?> ReadResponseAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new CheckpointTransferException("Checkpoint returned an unexpectedly large response.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new CheckpointTransferException("Checkpoint returned an unexpectedly large response.");
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
            return default;
        try
        {
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), jsonTypeInfo);
        }
        catch (JsonException ex)
        {
            throw new CheckpointTransferException("Checkpoint returned an invalid transfer response.", ex);
        }
    }

    private static Uri CreateBaseUri(string normalizedAddress)
    {
        var addressWithPort = normalizedAddress.Contains(':', StringComparison.Ordinal)
            ? normalizedAddress
            : $"{normalizedAddress}:{DefaultPort.ToString(CultureInfo.InvariantCulture)}";
        return new Uri($"http://{addressWithPort}/", UriKind.Absolute);
    }

    private static void ValidateReceiver(ReceiverInfo receiver)
    {
        if (!string.Equals(receiver.Device, "3DS", StringComparison.OrdinalIgnoreCase))
            throw new CheckpointTransferException("The receiver did not identify itself as Checkpoint on a 3DS.");
        if (string.IsNullOrWhiteSpace(receiver.Version))
            throw new CheckpointTransferException("Checkpoint did not report its version.");
        if (receiver.MaxUploadBytes < 0)
            throw new CheckpointTransferException("Checkpoint reported an invalid upload limit.");
    }

    private static bool IsDottedDecimalIPv4(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(part =>
            part.Length is >= 1 and <= 3 &&
            part.All(character => character is >= '0' and <= '9') &&
            int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet) && octet <= 255);
    }

    private static void ValidateTitle(string titleId, string titleName)
    {
        if (titleId is not { Length: 16 } || !titleId.All(Uri.IsHexDigit))
            throw new ArgumentException("The 3DS title ID must contain 16 hexadecimal characters.", nameof(titleId));
        if (string.IsNullOrWhiteSpace(titleName))
            throw new ArgumentException("The game title is required.", nameof(titleName));
    }

    private static string CreateBackupName() =>
        $"PKForge_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}_{Guid.NewGuid():N}";

    private static bool SavedAsNamedBackup(string savedPath, string backupName)
    {
        var normalized = savedPath.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        var folder = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return string.Equals(folder, backupName, StringComparison.Ordinal);
    }

    private static string ResponseDetail(string? error, HttpResponseMessage response) =>
        !string.IsNullOrWhiteSpace(error) ? error.Trim() : response.ReasonPhrase ?? string.Empty;
}

internal sealed record ReceiverInfo(
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("maxUploadBytes")] long MaxUploadBytes,
    [property: JsonPropertyName("freeSpaceBytes")] long FreeSpaceBytes);

internal sealed record UploadMetadata(
    [property: JsonPropertyName("titleId")] string TitleId,
    [property: JsonPropertyName("titleName")] string TitleName,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("backupName")] string BackupName,
    [property: JsonPropertyName("isZip")] bool IsZip,
    [property: JsonPropertyName("fileBytesTotal")] long FileBytesTotal,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("timestamp")] string Timestamp);

internal sealed record UploadResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("savedPath")] string? SavedPath,
    [property: JsonPropertyName("error")] string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReceiverInfo))]
[JsonSerializable(typeof(UploadMetadata))]
[JsonSerializable(typeof(UploadResponse))]
internal sealed partial class CheckpointTransferJsonContext : JsonSerializerContext;

public sealed record CheckpointTransferResult(
    string BackupName,
    string SavedPath,
    string ReceiverDevice,
    string ReceiverVersion);

public class CheckpointTransferException : Exception
{
    public CheckpointTransferException(string message) : base(message)
    {
    }

    public CheckpointTransferException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
