using System.Net;
using System.Text;
using System.Text.Json;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class CheckpointTransferClientTests
{
    private static readonly byte[] SaveBytes = [0xDE, 0xAD, 0xBE, 0xEF];

    [Theory]
    [InlineData("192.168.1.42", "192.168.1.42")]
    [InlineData(" 192.168.1.42:8000 ", "192.168.1.42")]
    [InlineData("10.0.0.3:8123", "10.0.0.3:8123")]
    public void AddressIsCanonicalizedForRememberedSetting(string value, string expected) =>
        Assert.Equal(expected, CheckpointTransferClient.NormalizeAddress(value));

    [Theory]
    [InlineData("1234")]
    [InlineData("3ds.local")]
    [InlineData("192.168.1")]
    [InlineData("192.168.1.999")]
    [InlineData("http://192.168.1.42")]
    [InlineData("192.168.1.42:0")]
    [InlineData("192.168.1.42:65536")]
    public void AddressRequiresDottedIPv4WithValidOptionalPort(string value) =>
        Assert.Throws<ArgumentException>(() => CheckpointTransferClient.NormalizeAddress(value));

    [Theory]
    [InlineData("0000", true)]
    [InlineData("9876", true)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    [InlineData("12A4", false)]
    [InlineData("１２３４", false)]
    public void PinUsesExactlyFourAsciiDigits(string value, bool expected) =>
        Assert.Equal(expected, CheckpointTransferClient.IsValidPin(value));

    [Fact]
    public void PokemonXYTitleIdsMatchDigitalReleases()
    {
        Assert.Equal("0004000000055D00", CheckpointTransferClient.PokemonXTitleId);
        Assert.Equal("0004000000055E00", CheckpointTransferClient.PokemonYTitleId);
    }

    [Fact]
    public async Task SendsCheckpointPreflightThenExactRawMainUpload()
    {
        var requests = new List<CapturedRequest>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var captured = await CaptureAsync(request, cancellationToken);
            requests.Add(captured);
            if (request.Method == HttpMethod.Get)
                return InfoResponse();

            var backupName = ReadMetadata(captured.Body).GetProperty("backupName").GetString();
            return JsonResponse($$"""{"ok":true,"savedPath":"/3ds/Checkpoint/saves/0x0055D Pokémon X/{{backupName}}/"}""");
        });
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var result = await client.SendAsync(
            "192.168.1.42",
            "0420",
            SaveBytes,
            CheckpointTransferClient.PokemonXTitleId,
            "Pokémon X",
            CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("http://192.168.1.42:8000/transfer/info", requests[0].Uri.ToString());
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("http://192.168.1.42:8000/transfer/upload", requests[1].Uri.ToString());
        Assert.Equal("0420", Assert.Single(requests[1].TokenValues));
        Assert.Equal(requests[1].Body.Length, requests[1].ContentLength);
        Assert.StartsWith("multipart/form-data; boundary=----pkforge-boundary-", requests[1].ContentType, StringComparison.Ordinal);

        var metadata = ReadMetadata(requests[1].Body);
        Assert.Equal(CheckpointTransferClient.PokemonXTitleId, metadata.GetProperty("titleId").GetString());
        Assert.Equal("Pokémon X", metadata.GetProperty("titleName").GetString());
        Assert.Equal("save", metadata.GetProperty("dataType").GetString());
        Assert.Equal(result.BackupName, metadata.GetProperty("backupName").GetString());
        Assert.False(metadata.GetProperty("isZip").GetBoolean());
        Assert.Equal(SaveBytes.Length, metadata.GetProperty("fileBytesTotal").GetInt32());
        Assert.Equal("main", metadata.GetProperty("fileName").GetString());
        Assert.Matches("^PKForge_[0-9]{8}-[0-9]{6}_[0-9a-f]{32}$", result.BackupName);
        Assert.Equal("3DS", result.ReceiverDevice);
        Assert.Equal("5.2.0", result.ReceiverVersion);

        var bodyText = Encoding.Latin1.GetString(requests[1].Body);
        Assert.Contains("name=\"file\"; filename=\"main\"", bodyText, StringComparison.Ordinal);
        Assert.True(ContainsSequence(requests[1].Body, SaveBytes));
    }

    [Fact]
    public async Task EveryTransferUsesANewBackupName()
    {
        var names = new List<string>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
                return InfoResponse();
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var name = ReadMetadata(body).GetProperty("backupName").GetString()!;
            names.Add(name);
            return JsonResponse($$"""{"ok":true,"savedPath":"/backups/{{name}}/"}""");
        });
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        await client.SendAsync("10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X");
        await client.SendAsync("10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X");

        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task PreflightUploadLimitStopsBeforeSendingSave()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(InfoResponse(maxUploadBytes: 3)));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X"));

        Assert.Contains("at most 3 bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task HttpSuccessWithoutProtocolSuccessIsRejected()
    {
        var handler = SequenceHandler(InfoResponse(), JsonResponse("""{"ok":false,"error":"Failed to store file"}"""));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonYTitleId, "Pokémon Y"));

        Assert.Contains("Failed to store file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongPinResponseReportsAuthenticationFailure()
    {
        var forbidden = JsonResponse("""{"ok":false,"error":"Invalid token"}""", HttpStatusCode.Forbidden);
        var handler = SequenceHandler(InfoResponse(), forbidden);
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "9999", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X"));

        Assert.Contains("rejected the PIN", error.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtocolSuccessMustConfirmTheGeneratedBackupPath()
    {
        var handler = SequenceHandler(InfoResponse(), JsonResponse("""{"ok":true,"savedPath":"/backups/a-different-name/"}"""));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonYTitleId, "Pokémon Y"));

        Assert.Contains("did not confirm", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidPinMakesNoNetworkRequest()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Network must not run."));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(
            "10.0.0.2", "12A4", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X"));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FailedPreflightDoesNotSendSave()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            JsonResponse("""{"error":"not receiving"}""", HttpStatusCode.NotFound)));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X"));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task MalformedPreflightDoesNotSendSave()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("not json")));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X"));

        Assert.Contains("invalid transfer response", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConnectionFailureUsesTransferError()
    {
        var handler = new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("No route to host")));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X"));

        Assert.Contains("could not reach", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        using var client = new CheckpointTransferClient(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X", cancellation.Token));
    }

    [Fact]
    public async Task CallerCanCancelWhileReadingPreflightBody()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream()),
        }));
        using var client = new CheckpointTransferClient(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X", cancellation.Token));
    }

    [Fact]
    public async Task RejectsAReceiverThatIsNotA3ds()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            """{"device":"Switch","version":"5.2.0","maxUploadBytes":0,"freeSpaceBytes":0}""")));
        using var client = new CheckpointTransferClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<CheckpointTransferException>(() => client.SendAsync(
            "10.0.0.2", "1234", SaveBytes, CheckpointTransferClient.PokemonXTitleId, "Pokémon X"));

        Assert.Contains("3DS", error.Message, StringComparison.Ordinal);
    }

    private static StubHandler SequenceHandler(params HttpResponseMessage[] responses)
    {
        var index = 0;
        return new StubHandler((_, _) => Task.FromResult(responses[index++]));
    }

    private static HttpResponseMessage InfoResponse(int maxUploadBytes = 0) => JsonResponse(
        $$"""{"device":"3DS","version":"5.2.0","maxUploadBytes":{{maxUploadBytes}},"freeSpaceBytes":0}""");

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static async Task<CapturedRequest> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        return new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("X-CP-Token", out var values) ? values.ToArray() : [],
            request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
            request.Content?.Headers.ContentLength,
            body);
    }

    private static JsonElement ReadMetadata(byte[] multipartBody)
    {
        var text = Encoding.UTF8.GetString(multipartBody);
        const string marker = "Content-Type: application/json\r\n\r\n";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        Assert.True(start >= marker.Length);
        var end = text.IndexOf("\r\n--", start, StringComparison.Ordinal);
        Assert.True(end > start);
        using var document = JsonDocument.Parse(text[start..end]);
        return document.RootElement.Clone();
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string[] TokenValues,
        string ContentType,
        long? ContentLength,
        byte[] Body);

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return responder(request, cancellationToken);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
