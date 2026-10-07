using System.Text.Json;
using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class PokedexLocalCopiesTests
{
    private static readonly DateTimeOffset OldObservedAt = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
    private static readonly DateTimeOffset NewObservedAt = DateTimeOffset.Parse("2026-10-03T10:00:00Z");
    private static readonly DateTimeOffset ReceivedAt = DateTimeOffset.Parse("2026-10-03T10:01:00Z");

    [Fact]
    public void AcceptReplacesCompleteSourcesAndPreservesIncompleteAndOmittedSnapshots()
    {
        var oldBankEntry = InventoryEntry(Observation("box:0:slot:0", 25, shiny: false), "pikachu");
        var oldSaveEntry = InventoryEntry(Observation("box:1:slot:2", 133, shiny: false), "eevee");
        var state = new PokedexConnectionState
        {
            InventorySources =
            [
                InventorySource("bank", "Old Bank", OldObservedAt, oldBankEntry),
                InventorySource("save-a", "Save A", OldObservedAt, oldSaveEntry),
            ],
        };
        var newBankObservation = Observation("box:2:slot:4", 719, shiny: true, box: 2, slot: 4);
        var queued = state.QueueSnapshot("thor", NewObservedAt,
        [
            new PokedexSource("bank", "PKForge Bank", "bank", true, [newBankObservation]),
            new PokedexSource("save-a", "Renamed Save", "save", false, []),
        ]);
        var response = Response([
            new PokedexMapping("bank", "box:2:slot:4", ["diancie"], "matched", null),
        ]);

        var accepted = queued.Accept(response, ReceivedAt);

        var bank = Assert.Single(accepted.InventorySources, source => source.SourceId == "bank");
        Assert.Equal("PKForge Bank", bank.Label);
        Assert.Equal(NewObservedAt, bank.ObservedAt);
        Assert.Equal(ReceivedAt, bank.ReceivedAt);
        Assert.True(bank.LastAttemptComplete);
        var bankEntry = Assert.Single(bank.Observations);
        Assert.Equal(newBankObservation, bankEntry.Observation);
        Assert.Equal(["diancie"], bankEntry.DexEntryIds);

        var save = Assert.Single(accepted.InventorySources, source => source.SourceId == "save-a");
        Assert.Equal("Renamed Save", save.Label);
        Assert.Equal(OldObservedAt, save.ObservedAt);
        Assert.Equal(oldSaveEntry, Assert.Single(save.Observations));
        Assert.False(save.LastAttemptComplete);
        Assert.Equal(NewObservedAt, save.LastAttemptAt);
    }

    [Fact]
    public void AcceptPreservesSourceOmittedFromLatestMapping()
    {
        var omitted = InventorySource(
            "save-a", "Save A", OldObservedAt,
            InventoryEntry(Observation("box:1:slot:2", 133, shiny: false), "eevee"));
        var queued = new PokedexConnectionState { InventorySources = [omitted] }
            .QueueSnapshot("thor", NewObservedAt,
            [
                new PokedexSource(
                    "bank", "PKForge Bank", "bank", true,
                    [Observation("box:0:slot:0", 25, shiny: false)]),
            ]);

        var accepted = queued.Accept(Response([
            new PokedexMapping("bank", "box:0:slot:0", ["pikachu"], "matched", null),
        ]));

        Assert.Equal(omitted, Assert.Single(accepted.InventorySources, source => source.SourceId == "save-a"));
    }

    [Fact]
    public void CompleteEmptySourceClearsItsPriorObservations()
    {
        var existing = InventorySource(
            "bank", "PKForge Bank", OldObservedAt,
            InventoryEntry(Observation("box:0:slot:0", 25, shiny: false), "pikachu"));
        var queued = new PokedexConnectionState { InventorySources = [existing] }
            .QueueSnapshot("thor", NewObservedAt,
            [new PokedexSource("bank", "PKForge Bank", "bank", true, [])]);

        var accepted = queued.Accept(Response([]));

        Assert.Empty(Assert.Single(accepted.InventorySources).Observations);
        Assert.Equal(NewObservedAt, accepted.InventorySources[0].ObservedAt);
    }

    [Fact]
    public void PersistedAcceptedSnapshotStillBuildsTheOfflineMissingQueue()
    {
        var shinyVivillon = Observation(
            "box:4:slot:7",
            666,
            shiny: true,
            form: 19,
            format: "PK6",
            box: 4,
            slot: 7);
        var queued = new PokedexConnectionState().QueueSnapshot(
            "thor",
            NewObservedAt,
            [new PokedexSource("x-save", "Pokémon X", "save", true, [shinyVivillon])]);
        var response = Response([
            new PokedexMapping("x-save", shinyVivillon.SlotId, ["vivillon-poke-ball"], "matched", null),
        ]) with
        {
            Checklist =
            [
                new PokedexChecklistEntry(
                    "vivillon-poke-ball", "Vivillon (Poké Ball Pattern)", NormalOwned: true, ShinyOwned: false, Notes: null),
            ],
        };
        var accepted = queued.Accept(response, ReceivedAt);

        var json = JsonSerializer.Serialize(accepted, PokedexConnectionJson.Options);
        var restored = JsonSerializer.Deserialize<PokedexConnectionState>(json, PokedexConnectionJson.Options)!;
        var missing = PokedexCollectionQueue.FindMissing(restored.Cache!.Checklist, restored.InventorySources);

        var queueEntry = Assert.Single(missing);
        Assert.Equal("vivillon-poke-ball", queueEntry.DexEntryId);
        Assert.True(queueEntry.Shiny);
        var copy = Assert.Single(queueEntry.Copies);
        Assert.Equal("x-save", copy.Source.SourceId);
        Assert.Equal("Pokémon X", copy.Source.Label);
        Assert.Equal(NewObservedAt, copy.Source.ObservedAt);
        Assert.Equal(666, copy.Entry.Observation.Species);
        Assert.Equal(19, copy.Entry.Observation.Form);
        Assert.True(copy.Entry.Observation.Shiny);
        Assert.Equal(4, copy.Entry.Observation.Box);
        Assert.Equal(7, copy.Entry.Observation.Slot);
        Assert.Equal("box:4:slot:7", copy.Entry.Observation.SlotId);
    }

    [Fact]
    public void AcceptedRetryKeepsTheOriginalPendingObservationTime()
    {
        var originalObservedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z");
        var queued = new PokedexConnectionState().QueueSnapshot(
            "thor",
            originalObservedAt,
            [
                new PokedexSource(
                    "bank", "PKForge Bank", "bank", true,
                    [Observation("box:0:slot:0", 25, shiny: false)]),
            ]);
        var retry = queued.RecordFailure("offline").QueueSnapshot(
            "thor",
            NewObservedAt,
            [new PokedexSource("bank", "PKForge Bank", "bank", true, [])]);

        var accepted = retry.Accept(Response([
            new PokedexMapping("bank", "box:0:slot:0", ["pikachu"], "matched", null),
        ]));

        var source = Assert.Single(accepted.InventorySources);
        Assert.Equal(originalObservedAt, source.ObservedAt);
        Assert.Equal(originalObservedAt, source.LastAttemptAt);
        Assert.Equal("box:0:slot:0", Assert.Single(source.Observations).Observation.SlotId);
    }

    [Fact]
    public void ChangingServerClearsInventorySnapshots()
    {
        var state = new PokedexConnectionState
        {
            InventorySources =
            [
                InventorySource(
                    "bank", "PKForge Bank", OldObservedAt,
                    InventoryEntry(Observation("box:0:slot:0", 25, shiny: false), "pikachu")),
            ],
        };

        var changed = state.ChangeServerUrl("https://pokedex.example.test/");

        Assert.Empty(changed.InventorySources);
    }

    [Fact]
    public void MissingQueueTracksNormalAndShinyOwnershipIndependently()
    {
        var source = InventorySource(
            "bank", "PKForge Bank", OldObservedAt,
            InventoryEntry(Observation("box:0:slot:0", 25, shiny: false), "pikachu"),
            InventoryEntry(Observation("box:0:slot:1", 25, shiny: true, slot: 1), "pikachu"),
            InventoryEntry(Observation("box:0:slot:2", 133, shiny: false, slot: 2), "eevee"));
        var checklist = new PokedexChecklistEntry[]
        {
            new("pikachu", "Pikachu", NormalOwned: true, ShinyOwned: false, Notes: null),
            new("eevee", "Eevee", NormalOwned: false, ShinyOwned: false, Notes: null),
        };

        var missing = PokedexCollectionQueue.FindMissing(checklist, [source]);

        Assert.Collection(
            missing,
            entry =>
            {
                Assert.Equal("pikachu", entry.DexEntryId);
                Assert.True(entry.Shiny);
                Assert.True(Assert.Single(entry.Copies).Entry.Observation.Shiny);
            },
            entry =>
            {
                Assert.Equal("eevee", entry.DexEntryId);
                Assert.False(entry.Shiny);
                Assert.Equal("box:0:slot:2", Assert.Single(entry.Copies).Entry.Observation.SlotId);
            });
    }

    [Theory]
    [InlineData("unmapped")]
    [InlineData("ambiguous")]
    public void MissingQueueOnlyUsesExactMatchedMappings(string mappingStatus)
    {
        var source = InventorySource(
            "bank", "PKForge Bank", OldObservedAt,
            new PokedexInventoryObservation(
                Observation("box:0:slot:0", 25, shiny: false),
                ["pikachu"],
                mappingStatus,
                "Not exact"));
        var checklist = new[] { new PokedexChecklistEntry("pikachu", "Pikachu", false, false, null) };

        Assert.Empty(PokedexCollectionQueue.FindMissing(checklist, [source]));
    }

    [Fact]
    public void PlanCandidatesApplySpeciesFormShinyAndFormatRules()
    {
        var target = Target() with
        {
            Match = new PokedexBankPlanMatch([666], [19], true, ["PK6"], "Inspect it."),
        };
        var matching = Observation("box:0:slot:0", 666, shiny: true, form: 19, format: "pk6");
        var sources = new[]
        {
            new PokedexSource("save-a", "Save A", "save", true,
            [
                matching,
                Observation("box:0:slot:1", 666, shiny: false, form: 19, format: "PK6", slot: 1),
                Observation("box:0:slot:2", 666, shiny: true, form: 18, format: "PK6", slot: 2),
                Observation("box:0:slot:3", 666, shiny: true, form: 19, format: "PK7", slot: 3),
            ]),
        };

        var candidates = PokedexBankPlanCandidates.Find(target, sources);

        Assert.Equal(matching, Assert.Single(candidates).Observation);
        Assert.True(PokedexBankPlanCandidates.Matches(target, matching));
    }

    [Fact]
    public void PlanWithoutMatchHasNoCandidates()
    {
        var source = new PokedexSource(
            "bank", "PKForge Bank", "bank", true,
            [Observation("box:0:slot:0", 25, shiny: false)]);

        Assert.Empty(PokedexBankPlanCandidates.Find(Target(), new[] { source }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData(" UNKNOWN ")]
    [InlineData("unsupported")]
    public void PlanCandidatesRejectFormatsThatCannotBeUsed(string format)
    {
        var target = Target() with { Match = new PokedexBankPlanMatch([25]) };
        var observation = Observation("box:0:slot:0", 25, shiny: false, format: format);

        Assert.False(PokedexBankPlanCandidates.Matches(target, observation));
    }

    [Fact]
    public void EmptyOptionalPlanFiltersDoNotRestrictMatches()
    {
        var target = Target() with { Match = new PokedexBankPlanMatch([25], [], null, []) };
        var observation = Observation("box:0:slot:0", 25, shiny: true, form: 8, format: "PK9");

        Assert.True(PokedexBankPlanCandidates.Matches(target, observation));
    }

    [Fact]
    public void RichBankPlanFieldsRoundTripAndLegacyFieldsRemainOptional()
    {
        const string json = """
            {
              "id":"diancie","title":"Diancie","source":"X/Y","gameProgress":"Ready",
              "preparations":[{"id":"backup","label":"Back up","detail":"Keep a copy."}],
              "match":{"species":[719],"forms":[0],"shiny":false,"formats":["PK6"],"notes":"Inspect."},
              "wonderCards":[{
                "title":"OCT2014 Diancie","cardId":"525","games":"X / Y","language":"English",
                "searchTerm":"OCT2014","kind":"pokemon","notes":"Use the set IV variant.",
                "sourcePath":"Released/Gen 6/card.wc6","sourceUrl":"https://example.test/card"
              }],
              "progress":{"acquired":false,"archived":false,"inBank":false,"homeVerified":false,
                "notes":null,"preparation":{"backup":true}}
            }
            """;

        var parsed = JsonSerializer.Deserialize<PokedexBankPlanTarget>(json, PokedexConnectionJson.Options)!;
        var serialized = JsonSerializer.Serialize(parsed, PokedexConnectionJson.Options);
        var target = JsonSerializer.Deserialize<PokedexBankPlanTarget>(serialized, PokedexConnectionJson.Options)!;

        Assert.Equal("backup", Assert.Single(target.Preparations!).Id);
        Assert.Equal([719], target.Match!.Species);
        Assert.True(target.Progress.Preparation!["backup"]);
        var card = Assert.Single(target.WonderCards!);
        Assert.Equal("OCT2014", card.SearchTerm);
        Assert.Equal("pokemon", card.Kind);
        Assert.Equal("https://example.test/card", card.SourceUrl);

        const string legacyJson = """
            {"id":"old","title":"Old","source":"Game","gameProgress":"Ready",
             "progress":{"acquired":false,"archived":false,"inBank":false,"homeVerified":false,"notes":null}}
            """;
        var legacy = JsonSerializer.Deserialize<PokedexBankPlanTarget>(legacyJson, PokedexConnectionJson.Options)!;
        Assert.Null(legacy.Preparations);
        Assert.Null(legacy.Match);
        Assert.Null(legacy.Progress.Preparation);
    }

    [Fact]
    public void LegacyConnectionCacheWithoutInventoryStillLoads()
    {
        const string json = """
            {"enabled":true,"serverUrl":"https://pokedex.example.test/",
             "deviceId":"60725532-5aba-4fc8-88a3-ad89e27890fd","lastSequence":2}
            """;

        var state = JsonSerializer.Deserialize<PokedexConnectionState>(json, PokedexConnectionJson.Options)!;

        Assert.Empty(state.InventorySources);
    }

    [Fact]
    public void ResponseValidationRejectsNullOptionalBankPlanItems()
    {
        var invalidPreparation = Response([]) with
        {
            BankPlan = new PokedexBankPlan([Target() with { Preparations = [null!] }]),
        };
        var invalidCard = Response([]) with
        {
            BankPlan = new PokedexBankPlan([Target() with { WonderCards = [null!] }]),
        };

        Assert.Throws<PokedexProtocolException>(() => PokedexConnectionProtocol.ValidateResponse(invalidPreparation));
        Assert.Throws<PokedexProtocolException>(() => PokedexConnectionProtocol.ValidateResponse(invalidCard));
    }

    private static PokedexObservation Observation(
        string slotId,
        int species,
        bool shiny,
        int form = 0,
        string format = "PK9",
        int box = 0,
        int slot = 0) =>
        new(slotId, species, form, 0, null, null, format, shiny, null, box, slot);

    private static PokedexInventoryObservation InventoryEntry(PokedexObservation observation, string dexEntryId) =>
        new(observation, [dexEntryId], "matched", null);

    private static PokedexInventorySource InventorySource(
        string sourceId,
        string label,
        DateTimeOffset observedAt,
        params PokedexInventoryObservation[] observations) =>
        new(sourceId, label, sourceId == "bank" ? "bank" : "save", observedAt, observedAt, true, observedAt, observations);

    private static PokedexSyncResponse Response(IReadOnlyList<PokedexMapping> mappings) =>
        new(
            PokedexConnectionProtocol.CurrentVersion,
            PokedexConnectionProtocol.AppliedStatus,
            ReceivedAt,
            mappings,
            [],
            new PokedexBankPlan([]));

    private static PokedexBankPlanTarget Target() =>
        new(
            "target",
            "Target",
            "Game",
            "Ready",
            new PokedexBankPlanProgress(false, false, false, false, null));
}
