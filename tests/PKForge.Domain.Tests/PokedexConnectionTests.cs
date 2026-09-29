using System.Net;
using System.Text;
using System.Text.Json;
using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class PokedexConnectionTests
{
    [Fact]
    public void RequestUsesVersionedCamelCaseProtocol()
    {
        var request = Request();

        var json = JsonSerializer.Serialize(request, PokedexConnectionJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("thor", root.GetProperty("deviceName").GetString());
        Assert.False(root.TryGetProperty("ProtocolVersion", out _));
        var observation = root.GetProperty("sources")[0].GetProperty("observations")[0];
        Assert.Equal(25, observation.GetProperty("species").GetInt32());
        Assert.True(observation.GetProperty("canGigantamax").GetBoolean());
        Assert.Equal("Sparky", observation.GetProperty("nickname").GetString());
    }

    [Fact]
    public void ResponseRoundTripsKnownFieldsAndIgnoresMetadata()
    {
        const string json = """
            {
              "protocolVersion": 1,
              "status": "applied",
              "receivedAt": "2026-09-28T12:00:00Z",
              "mapping": [{
                "sourceId": "bank",
                "slotId": "box:0:slot:0",
                "dexEntryIds": ["pikachu"],
                "mappingStatus": "matched",
                "mappingReason": null,
                "sourceLabel": "PKForge Bank"
              }],
              "checklist": [{
                "dexEntryId": "pikachu",
                "displayName": "Pikachu",
                "normalOwned": true,
                "shinyOwned": false,
                "notes": "Keep"
              }],
              "bankPlan": {"targets": [{
                "id": "pikachu",
                "title": "Pikachu",
                "source": "Yellow",
                "gameProgress": "Ready",
                "progress": {
                  "acquired": true,
                  "archived": true,
                  "inBank": false,
                  "homeVerified": false,
                  "notes": null,
                  "updatedAt": "2026-09-28T12:00:00Z"
                },
                "priority": "P1"
              }]},
              "metadata": {"catalogVersion": "2026-09"}
            }
            """;

        var response = JsonSerializer.Deserialize<PokedexSyncResponse>(json, PokedexConnectionJson.Options);

        Assert.NotNull(response);
        PokedexConnectionProtocol.ValidateResponse(response);
        Assert.Equal("pikachu", response.Mapping[0].DexEntryIds[0]);
        Assert.Equal("PKForge Bank", response.Mapping[0].SourceLabel);
        Assert.Equal("Keep", response.Checklist[0].Notes);
        Assert.True(response.BankPlan.Targets[0].Progress.Archived);
    }

    [Fact]
    public void QueueKeepsFailedSnapshotAndSequenceUntilAccepted()
    {
        var state = new PokedexConnectionState { Enabled = true };
        var queued = state.QueueSnapshot("thor", DateTimeOffset.Parse("2026-09-28T12:00:00Z"), Request().Sources);
        var retry = queued.RecordFailure("offline").QueueSnapshot(
            "changed name",
            DateTimeOffset.Parse("2026-09-29T12:00:00Z"),
            []);

        Assert.Equal(1, retry.LastSequence);
        Assert.Same(queued.Pending, retry.Pending);
        Assert.Equal("offline", retry.LastError);

        var accepted = retry.Accept(Response(), Response().ReceivedAt);
        Assert.Null(accepted.Pending);
        Assert.NotNull(accepted.Cache);
        Assert.Null(accepted.LastError);
        Assert.Equal(Response().ReceivedAt, accepted.LastSyncedAt);
    }

    [Fact]
    public void QueueConvertsObservationTimeToUtc()
    {
        var observedAt = new DateTimeOffset(2026, 9, 28, 7, 0, 0, TimeSpan.FromHours(-5));

        var queued = new PokedexConnectionState().QueueSnapshot("thor", observedAt, Request().Sources);

        Assert.Equal(TimeSpan.Zero, queued.Pending!.ObservedAt.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z"), queued.Pending.ObservedAt);
    }

    [Fact]
    public void DuplicateResponseCompletesPendingSnapshot()
    {
        var queued = new PokedexConnectionState { Enabled = true }
            .QueueSnapshot("thor", DateTimeOffset.UtcNow, Request().Sources);

        var accepted = queued.Accept(Response() with { Status = PokedexConnectionProtocol.DuplicateStatus });

        Assert.Null(accepted.Pending);
        Assert.Equal(PokedexConnectionProtocol.DuplicateStatus, accepted.Cache!.Status);
    }

    [Fact]
    public void ServerChangeResetsServerBoundState()
    {
        var queued = new PokedexConnectionState { Enabled = true }
            .QueueSnapshot("thor", DateTimeOffset.UtcNow, Request().Sources)
            .RecordFailure("offline") with
        {
            Cache = Response(),
            LastSyncedAt = Response().ReceivedAt,
        };
        var oldDeviceId = queued.DeviceId;

        var changed = queued.ChangeServerUrl("https://pokedex.example.test/subpath");

        Assert.True(changed.Enabled);
        Assert.Equal("https://pokedex.example.test/subpath/", changed.ServerUrl);
        Assert.NotEqual(oldDeviceId, changed.DeviceId);
        Assert.Equal(0, changed.LastSequence);
        Assert.Null(changed.Pending);
        Assert.Null(changed.Cache);
        Assert.Null(changed.LastSyncedAt);
        Assert.Null(changed.LastError);
    }

    [Fact]
    public void ServerPathCaseChangeResetsServerBoundState()
    {
        var state = new PokedexConnectionState
        {
            ServerUrl = "https://pokedex.example.test/Service/",
            LastSequence = 4,
            Cache = Response(),
        };

        var changed = state.ChangeServerUrl("https://pokedex.example.test/service");

        Assert.Equal(0, changed.LastSequence);
        Assert.Null(changed.Cache);
        Assert.NotEqual(state.DeviceId, changed.DeviceId);
    }

    [Theory]
    [InlineData("http://pokedex.example.test")]
    [InlineData("https://user:secret@pokedex.example.test")]
    [InlineData("https://pokedex.example.test?token=nope")]
    [InlineData("https://pokedex.example.test/#section")]
    [InlineData("relative/path")]
    public void ServerUrlRejectsUnsafeValues(string value) =>
        Assert.Throws<ArgumentException>(() => PokedexConnectionClient.NormalizeServerUrl(value));

    [Fact]
    public async Task DisabledConnectionMakesNoRequest()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Handler must not run."));
        var client = new PokedexConnectionClient(new HttpClient(handler));

        await Assert.ThrowsAsync<PokedexConnectionDisabledException>(
            () => client.SyncAsync("not even a URL", false, Request(), CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PostsToBasePathAndReturnsValidatedResponse()
    {
        string? body = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(new Uri("https://pokedex.example.test/service/api/integrations/pkforge/sync"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(Response());
        });
        var client = new PokedexConnectionClient(new HttpClient(handler));

        var response = await client.SyncAsync("https://pokedex.example.test/service", true, Request(), CancellationToken.None);

        Assert.Equal("applied", response.Status);
        Assert.NotNull(body);
        Assert.DoesNotContain('\n', body);
        using var sent = JsonDocument.Parse(body);
        Assert.Equal(1, sent.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NotFoundReportsIncompatibleBackend()
    {
        var client = new PokedexConnectionClient(new HttpClient(new StubHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));

        var error = await Assert.ThrowsAsync<PokedexConnectionException>(
            () => client.SyncAsync(PokedexConnectionProtocol.DefaultServerUrl, true, Request(), CancellationToken.None));

        Assert.Contains("does not support", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureDoesNotBecomeAProtocolResponse()
    {
        var client = new PokedexConnectionClient(new HttpClient(new StubHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                ReasonPhrase = "Maintenance",
            }))));

        var error = await Assert.ThrowsAsync<PokedexConnectionException>(
            () => client.SyncAsync(PokedexConnectionProtocol.DefaultServerUrl, true, Request(), CancellationToken.None));

        Assert.Contains("503", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        var client = new PokedexConnectionClient(new HttpClient(new StubHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SyncAsync(PokedexConnectionProtocol.DefaultServerUrl, true, Request(), cancellation.Token));
    }

    [Theory]
    [InlineData("{\"status\":\"applied\",\"receivedAt\":\"2026-09-28T12:00:00Z\",\"mapping\":[],\"checklist\":[],\"bankPlan\":{\"targets\":[]}}")]
    [InlineData("{\"protocolVersion\":2,\"status\":\"applied\",\"receivedAt\":\"2026-09-28T12:00:00Z\",\"mapping\":[],\"checklist\":[],\"bankPlan\":{\"targets\":[]}}")]
    [InlineData("{\"protocolVersion\":1,\"status\":\"maybe\",\"receivedAt\":\"2026-09-28T12:00:00Z\",\"mapping\":[],\"checklist\":[],\"bankPlan\":{\"targets\":[]}}")]
    [InlineData("{\"protocolVersion\":1,\"status\":\"applied\",\"receivedAt\":\"2026-09-28T12:00:00Z\"}")]
    public async Task RejectsMissingOrUnknownResponseContract(string json)
    {
        var client = new PokedexConnectionClient(new HttpClient(new StubHandler(
            (_, _) => Task.FromResult(JsonResponse(json)))));

        await Assert.ThrowsAsync<PokedexProtocolException>(
            () => client.SyncAsync(PokedexConnectionProtocol.DefaultServerUrl, true, Request(), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsNullResponseCollectionItemAsProtocolError()
    {
        const string json = """
            {"protocolVersion":1,"status":"applied","receivedAt":"2026-09-28T12:00:00Z",
             "mapping":[null],"checklist":[],"bankPlan":{"targets":[]}}
            """;
        var client = new PokedexConnectionClient(new HttpClient(new StubHandler(
            (_, _) => Task.FromResult(JsonResponse(json)))));

        await Assert.ThrowsAsync<PokedexProtocolException>(
            () => client.SyncAsync(PokedexConnectionProtocol.DefaultServerUrl, true, Request(), CancellationToken.None));
    }

    [Fact]
    public void RejectsNullRequestCollectionItem()
    {
        var request = Request() with { Sources = [null!] };

        Assert.Throws<ArgumentException>(() => PokedexConnectionProtocol.ValidateRequest(request));
    }

    [Fact]
    public void RejectsMoreThanTwentyThousandObservationsAcrossSources()
    {
        var observation = Request().Sources[0].Observations[0];
        var request = Request() with
        {
            Sources =
            [
                new PokedexSource("save-one", "Save one", "save", true,
                    Enumerable.Repeat(observation, 10_000).ToArray()),
                new PokedexSource("save-two", "Save two", "save", true,
                    Enumerable.Repeat(observation, 10_000).ToArray()),
                new PokedexSource("bank", "PKForge Bank", "bank", true, [observation]),
            ],
        };

        var error = Assert.Throws<ArgumentException>(() => PokedexConnectionProtocol.ValidateRequest(request));

        Assert.Contains("20,000 observations", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other", 1, null)]
    [InlineData("matched", 0, null)]
    [InlineData("matched", 1, "unexpected")]
    [InlineData("unmapped", 1, "reason")]
    [InlineData("unmapped", 0, null)]
    [InlineData("ambiguous", 0, null)]
    public void RejectsInvalidMappingResult(string status, int idCount, string? reason)
    {
        var ids = Enumerable.Repeat("pikachu", idCount).ToArray();
        var response = Response() with
        {
            Mapping = [new PokedexMapping("bank", "box:0:slot:0", ids, status, reason)],
        };

        Assert.Throws<PokedexProtocolException>(() => PokedexConnectionProtocol.ValidateResponse(response));
    }

    [Fact]
    public void AcceptsAmbiguousMappingWithoutCandidateIds()
    {
        var response = Response() with
        {
            Mapping =
            [
                new PokedexMapping(
                    "red-save",
                    "box:0:slot:0",
                    [],
                    "ambiguous",
                    "Species 25 requires gender to choose its living-form target.",
                    "Pokémon Red"),
            ],
        };

        PokedexConnectionProtocol.ValidateResponse(response);
    }

    private static PokedexSyncRequest Request() => new(
        PokedexConnectionProtocol.CurrentVersion,
        "60725532-5aba-4fc8-88a3-ad89e27890fd",
        "thor",
        1,
        DateTimeOffset.Parse("2026-09-28T11:00:00Z"),
        [new PokedexSource(
            "bank",
            "PKSM Bank",
            "bank",
            true,
            [new PokedexObservation("box:0:slot:0", 25, 0, 0, null, true, "PK9", false, "Sparky", 0, 0)])]);

    private static PokedexSyncResponse Response() => new(
        PokedexConnectionProtocol.CurrentVersion,
        PokedexConnectionProtocol.AppliedStatus,
        DateTimeOffset.Parse("2026-09-28T12:00:00Z"),
        [new PokedexMapping("bank", "box:0:slot:0", ["pikachu"], "matched", null)],
        [new PokedexChecklistEntry("pikachu", "Pikachu", true, false, null)],
        new PokedexBankPlan([
            new PokedexBankPlanTarget(
                "pikachu",
                "Pikachu",
                "Yellow",
                "Ready",
                new PokedexBankPlanProgress(true, true, false, false, null)),
        ]));

    private static HttpResponseMessage JsonResponse(PokedexSyncResponse response) =>
        JsonResponse(JsonSerializer.Serialize(response, PokedexConnectionJson.Options));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

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
}
