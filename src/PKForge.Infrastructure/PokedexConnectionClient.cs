using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

public sealed class PokedexConnectionClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly Uri SyncEndpoint = new("api/integrations/pkforge/sync", UriKind.Relative);
    private static readonly JsonSerializerOptions HttpJsonOptions = CreateHttpJsonOptions();
    private readonly HttpClient _httpClient;

    public PokedexConnectionClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = RequestTimeout,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static string NormalizeServerUrl(string serverUrl) =>
        PokedexConnectionProtocol.NormalizeServerUrl(serverUrl);

    public async Task<PokedexSyncResponse> SyncAsync(
        string serverUrl,
        bool enabled,
        PokedexSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
            throw new PokedexConnectionDisabledException();

        var normalizedUrl = NormalizeServerUrl(serverUrl);
        PokedexConnectionProtocol.ValidateRequest(request);
        var endpoint = new Uri(new Uri(normalizedUrl, UriKind.Absolute), SyncEndpoint);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request, options: HttpJsonOptions),
            };
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new PokedexConnectionException(
                    "The Pokédex server does not support the PKForge sync endpoint. Update the server and try again.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new PokedexConnectionException(
                    $"The Pokédex server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "Unknown"}).");
            }

            PokedexSyncResponse? result;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                result = await JsonSerializer.DeserializeAsync<PokedexSyncResponse>(
                    stream,
                    PokedexConnectionJson.Options,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new PokedexProtocolException("The Pokédex server returned invalid JSON.", ex);
            }

            if (result is null)
                throw new PokedexProtocolException("The Pokédex server returned an empty response.");
            PokedexConnectionProtocol.ValidateResponse(result);
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new PokedexConnectionException("The Pokédex sync timed out after 15 seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PokedexConnectionException("PKForge could not reach the Pokédex server.", ex);
        }
    }

    private static JsonSerializerOptions CreateHttpJsonOptions()
    {
        var options = new JsonSerializerOptions(PokedexConnectionJson.Options)
        {
            WriteIndented = false,
        };
        options.MakeReadOnly();
        return options;
    }
}

public class PokedexConnectionException : Exception
{
    public PokedexConnectionException(string message) : base(message)
    {
    }

    public PokedexConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class PokedexConnectionDisabledException : PokedexConnectionException
{
    public PokedexConnectionDisabledException() : base("The Pokédex connection is disabled.")
    {
    }
}
