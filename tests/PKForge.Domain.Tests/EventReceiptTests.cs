using System.Text.Json;
using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class EventReceiptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-event-receipts", Guid.NewGuid().ToString("N"));
    private const string SourceA = "Released/Gen 8/2021/0521 SWSH - Example (ENG).wc8";
    private const string SourceB = "Released/Gen 8/2022/0522 SWSH - Example (ENG).wc8";
    private static readonly string Fingerprint = new('a', 64);

    [Fact]
    public void ReceiptsPersistAndConflictingSourcesCannotOverwriteTheOriginal()
    {
        var path = Path.Combine(_root, "receipts.json");
        var store = new JsonEventReceiptStore(path);
        store.Record(Fingerprint, [SourceA]);
        Assert.False(new JsonEventReceiptStore(path).Find(Fingerprint)!.Ambiguous);
        store.Record(Fingerprint, [SourceB]);

        var receipt = new JsonEventReceiptStore(path).Find(Fingerprint)!;

        Assert.True(receipt.Ambiguous);
        Assert.Equal(new[] { SourceA, SourceB }, receipt.SourcePaths);
        Assert.Null(store.Find(new string('b', 64)));
    }

    [Fact]
    public void CorruptLedgerRemainsUntouchedAndUnavailable()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "receipts.json");
        File.WriteAllText(path, "{ broken");

        var store = new JsonEventReceiptStore(path);

        Assert.False(store.IsAvailable);
        Assert.Throws<IOException>(() => store.Record(Fingerprint, [SourceA]));
        Assert.Equal("{ broken", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("Released/Gen 8/../outside.wc8")]
    [InlineData("g8/2021/test.wc8")]
    [InlineData("Released/Gen 8//test.wc8")]
    [InlineData("Released/Gen 8/./test.wc8")]
    [InlineData("Unreleased/Gen 8/test.wc8")]
    public void ReceiptRequiresExactReleasedGalleryPaths(string source)
    {
        Assert.Throws<ArgumentException>(() => new JsonEventReceiptStore(null).Record(Fingerprint, [source]));
    }

    [Fact]
    public void OldPendingRequestRemainsByteEquivalentAfterReload()
    {
        var request = Request(new PokedexObservation("0:0", 25, 0, 0, null, false, "PK8", false, "Pikachu", 0, 0));
        var before = JsonSerializer.Serialize(request, PokedexConnectionJson.Options);

        var restored = JsonSerializer.Deserialize<PokedexSyncRequest>(before, PokedexConnectionJson.Options)!;
        PokedexConnectionProtocol.ValidateRequest(restored);

        Assert.Equal(before, JsonSerializer.Serialize(restored, PokedexConnectionJson.Options));
        Assert.DoesNotContain("entityFingerprint", before);
        Assert.DoesNotContain("eventMatches", before);
        Assert.DoesNotContain("eventScanComplete", before);
    }

    [Fact]
    public void EventObservationFieldsAreOptionalAndRemainProtocolOne()
    {
        var observation = new PokedexObservation("0:0", 25, 0, 0, null, false, "PK8", false, "Pikachu", 0, 0)
        {
            EntityFingerprint = Fingerprint,
            EventMatches = [new(SourceA, "recorded"), new(SourceB, "possible")],
            EventScanComplete = true,
        };
        var request = Request(observation) with { Sources = [Request(observation).Sources[0] with { EventScanComplete = true }] };
        PokedexConnectionProtocol.ValidateRequest(request);

        var json = JsonSerializer.Serialize(request, PokedexConnectionJson.Options);
        var restored = JsonSerializer.Deserialize<PokedexSyncRequest>(json, PokedexConnectionJson.Options)!;

        Assert.Equal(1, restored.ProtocolVersion);
        Assert.Equal(Fingerprint, restored.Sources[0].Observations[0].EntityFingerprint);
        Assert.Equal("recorded", restored.Sources[0].Observations[0].EventMatches![0].Status);
        Assert.True(restored.Sources[0].EventScanComplete);
    }

    [Fact]
    public void QueuingFreezesTheEventMatchList()
    {
        var matches = new List<PokedexEventMatch> { new(SourceA, "possible") };
        var observation = new PokedexObservation("0:0", 25, 0, 0, null, false, "PK8", false, "Pikachu", 0, 0)
        { EntityFingerprint = Fingerprint, EventMatches = matches };
        var state = new PokedexConnectionState().QueueSnapshot("device", DateTimeOffset.UtcNow, Request(observation).Sources);
        matches.Add(new(SourceB, "possible"));

        Assert.Single(state.Pending!.Sources[0].Observations[0].EventMatches!);
    }

    [Theory]
    [InlineData("confidence:high")]
    [InlineData("inferred")]
    [InlineData("confirmed")]
    public void MatchStatusCannotInventConfidenceOrConfirmation(string status)
    {
        var observation = new PokedexObservation("0:0", 25, 0, 0, null, false, "PK8", false, "Pikachu", 0, 0)
        { EntityFingerprint = Fingerprint, EventMatches = [new(SourceA, status)] };
        Assert.Throws<ArgumentException>(() => PokedexConnectionProtocol.ValidateRequest(Request(observation)));
    }

    private static PokedexSyncRequest Request(PokedexObservation observation) =>
        new(1, Guid.NewGuid().ToString("D"), "device", 1, DateTimeOffset.UtcNow,
            [new("save-one", "Save one", "save", true, [observation])]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
