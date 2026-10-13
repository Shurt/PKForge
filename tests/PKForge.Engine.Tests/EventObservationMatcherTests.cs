using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

[Collection(EventDatabaseCollection.Name)]
public sealed class EventObservationMatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-event-matches", Guid.NewGuid().ToString("N"));
    private const string SourceA = "Released/Gen 5/Wondercards/ENG/3990 BW - Test Pikachu (ENG).pgf";
    private const string SourceB = "Released/Gen 5/Wondercards/ENG/3991 BW - Other Pikachu (ENG).pgf";

    [Fact]
    public void OrdinaryPokeBallGiftIsPossibleWithoutAnyReceipt()
    {
        var card = Card();
        Load((SourceA, card));
        var mon = card.ConvertToPKM(Trainer());
        Assert.Equal((byte)Ball.Poke, mon.Ball);

        var observation = Read(mon, new EventObservationMatcher());

        Assert.True(observation.EventScanComplete);
        Assert.True(EventObservationRules.IsFingerprint(observation.EntityFingerprint!));
        var match = Assert.Single(observation.EventMatches!);
        Assert.Equal(SourceA, match.SourcePath);
        Assert.Equal("possible", match.Status);
    }

    [Fact]
    public void ProgressedAndEvolvedGiftKeepsItsIdentityAndOriginMatch()
    {
        var card = Card();
        Load((SourceA, card));
        var mon = card.ConvertToPKM(Trainer());
        var fingerprint = EventEntityFingerprint.Create(mon);
        var store = new JsonEventReceiptStore(null);
        store.Record(fingerprint!, [SourceA]);
        mon.Species = (ushort)Species.Raichu;
        mon.CurrentLevel = 70;
        mon.EV_ATK = 252;
        mon.Move1 = (ushort)Move.Thunderbolt;
        mon.Nickname = "Thunder";
        mon.CurrentFriendship = 200;

        var observation = Read(mon, new EventObservationMatcher(store));

        Assert.Equal(fingerprint, observation.EntityFingerprint);
        Assert.Equal("recorded", Assert.Single(observation.EventMatches!).Status);
        Assert.True(observation.EventScanComplete);
    }

    [Fact]
    public void EveryCompatibleTemplateIsReturnedRatherThanTheFirstLegalityEncounter()
    {
        var card = Card();
        var alternative = Card(3991, "Other test distribution");
        Load((SourceA, card), (SourceB, alternative));

        var observation = Read(card.ConvertToPKM(Trainer()), new EventObservationMatcher());

        Assert.Equal(new[] { SourceA, SourceB }, observation.EventMatches!.Select(match => match.SourcePath).Order());
        Assert.All(observation.EventMatches!, match => Assert.Equal("possible", match.Status));
        Assert.True(observation.EventScanComplete);
    }

    [Fact]
    public void ReceiptDoesNotPromoteAnotherCompatibleCardToRecorded()
    {
        var card = Card();
        Load((SourceA, card), (SourceB, Card(3991, "Other test distribution")));
        var mon = card.ConvertToPKM(Trainer());
        var store = new JsonEventReceiptStore(null);
        store.Record(EventEntityFingerprint.Create(mon)!, [SourceA]);

        var observation = Read(mon, new EventObservationMatcher(store));

        Assert.Equal("recorded", observation.EventMatches!.Single(match => match.SourcePath == SourceA).Status);
        Assert.Equal("possible", observation.EventMatches!.Single(match => match.SourcePath == SourceB).Status);
    }

    [Fact]
    public void ConflictingReceiptsForIdenticalEntitiesRemainAmbiguous()
    {
        var card = Card();
        Load((SourceA, card), (SourceB, Card(3991, "Other test distribution")));
        var mon = card.ConvertToPKM(Trainer());
        var store = new JsonEventReceiptStore(null);
        var fingerprint = EventEntityFingerprint.Create(mon)!;
        store.Record(fingerprint, [SourceA]);
        store.Record(fingerprint, [SourceB]);

        var observation = Read(mon, new EventObservationMatcher(store));

        Assert.Equal(2, observation.EventMatches!.Count);
        Assert.All(observation.EventMatches!, match => Assert.Equal("possible", match.Status));
    }

    [Fact]
    public void InjectionReceiptSurvivesMovingAndReopeningTheSave()
    {
        Load((SourceA, Card()));
        var path = Path.Combine(_root, "receipts.json");
        var store = new JsonEventReceiptStore(path);
        var save = Trainer();
        using var session = new SaveEngineSession(save, "test");
        var service = new EventDatabaseService(store);
        var gift = service.GetGifts(session).Single(gift => gift.Title == "Test distribution");
        Assert.True(service.Receive(session, gift.Id, 0, 0).Success);
        var received = save.GetBoxSlotAtIndex(0, 0);
        save.SetBoxSlotAtIndex(save.BlankPKM, 0, 0, EntityImportSettings.None);
        save.SetBoxSlotAtIndex(received, 1, 3, EntityImportSettings.None);
        var bytes = session.Serialize();
        using var reopened = new SaveEngine().OpenSession(bytes);
        var before = reopened.Serialize().ToArray();

        var observations = PokedexObservationReader.ReadSave(reopened, eventMatcher: new(new JsonEventReceiptStore(path)));

        var observation = Assert.Single(observations);
        Assert.Equal("1:3", observation.SlotId);
        Assert.Equal("recorded", Assert.Single(observation.EventMatches!).Status);
        Assert.Equal(before, reopened.Serialize().ToArray());
    }

    [Fact]
    public void UnrelatedReplacementDoesNotInheritThePriorSlotsReceipt()
    {
        var card = Card();
        Load((SourceA, card));
        var mon = card.ConvertToPKM(Trainer());
        var store = new JsonEventReceiptStore(null);
        store.Record(EventEntityFingerprint.Create(mon)!, [SourceA]);
        var replacement = mon.Clone();
        replacement.Species = (ushort)Species.Bulbasaur;
        // Even if all hashed origin bytes are copied, an unrelated species cannot
        // inherit a receipt. An ordinary replacement also has a different PID/IVs.
        Assert.Equal(EventEntityFingerprint.Create(mon), EventEntityFingerprint.Create(replacement));

        var observation = Read(replacement, new EventObservationMatcher(store));

        Assert.Empty(observation.EventMatches!);
    }

    [Fact]
    public void AlteredOriginDataInvalidatesRecordedIdentity()
    {
        var card = Card();
        Load((SourceA, card));
        var mon = card.ConvertToPKM(Trainer());
        var fingerprint = EventEntityFingerprint.Create(mon)!;
        var store = new JsonEventReceiptStore(null);
        store.Record(fingerprint, [SourceA]);
        mon.TID16++;

        var observation = Read(mon, new EventObservationMatcher(store));

        Assert.NotEqual(fingerprint, observation.EntityFingerprint);
        Assert.Empty(observation.EventMatches!);
    }

    [Fact]
    public void NonEventAndInvalidFallbackDoNotBecomeDistributions()
    {
        var card = Card();
        Load((SourceA, card));
        var mon = card.ConvertToPKM(Trainer());
        mon.MetLocation = 1;
        Assert.Empty(Read(mon, new EventObservationMatcher()).EventMatches!);
        mon.FatefulEncounter = false;
        Assert.Empty(Read(mon, new EventObservationMatcher()).EventMatches!);
    }

    [Fact]
    public void IdenticalCardsAtMultipleGalleryPathsStayPossibleWhenReceived()
    {
        var card = Card();
        Load((SourceA, card), (SourceB, card));
        var store = new JsonEventReceiptStore(null);
        using var session = new SaveEngineSession(Trainer(), "test");
        var service = new EventDatabaseService(store);
        var gift = service.GetGifts(session).Single(gift => gift.Title == "Test distribution");
        Assert.True(service.Receive(session, gift.Id, 0, 0).Success);

        var observation = Assert.Single(PokedexObservationReader.ReadSave(session, eventMatcher: new(store)));

        Assert.Equal(2, observation.EventMatches!.Count);
        Assert.All(observation.EventMatches!, match => Assert.Equal("possible", match.Status));
    }

    [Fact]
    public void CandidateLimitMarksTheEventScanIncomplete()
    {
        var cards = Enumerable.Range(0, 101).Select(index =>
            ($"Released/Gen 5/Wondercards/ENG/{index:0000}.pgf", Card(4000 + index, $"Candidate {index}"))).ToArray();
        Load(cards);

        var observation = Read(cards[0].Item2.ConvertToPKM(Trainer()), new EventObservationMatcher());

        Assert.Equal(EventObservationRules.MaxMatches, observation.EventMatches!.Count);
        Assert.False(observation.EventScanComplete);
    }

    [Fact]
    public void MissingArchiveAndCancellationRemainExplicit()
    {
        EventDatabaseService.IndexArchive(_root + "-missing");
        var mon = Card().ConvertToPKM(Trainer());
        Assert.False(Read(mon, new EventObservationMatcher()).EventScanComplete);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PokedexObservationReader.ReadBank(Entry(mon), Bytes(mon), new(), cancellation.Token));
    }

    [Fact]
    public void SwitchGiftHasACanonicalCardMatch()
    {
        var card = EncounterEvent.MGDB_G8.First(gift => gift.IsEntity && gift.Species == (ushort)Species.Pikachu
            && !gift.IsEgg && !gift.IsHOMEGift && gift.CanBeReceivedByVersion(GameVersion.SW));
        const string path = "Released/Gen 8/SwSh/Test Switch Pikachu.wc8";
        var file = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, card.Write());
        EncounterEvent.RefreshMGDB(_root);
        EventDatabaseService.IndexArchive(_root);
        var mon = card.ConvertToPKM(BlankSaveFile.Get(GameVersion.SW, "Kyle", LanguageID.English));

        var observation = Read(mon, new EventObservationMatcher());

        Assert.Equal(path, Assert.Single(observation.EventMatches!).SourcePath);
        Assert.True(observation.EventScanComplete);
    }

    [Fact]
    public void TransferredEvolvedGiftStillMatchesItsOriginalCardAsPossible()
    {
        var card = Card();
        Load((SourceA, card));
        var original = card.ConvertToPKM(Trainer());
        var transferred = EntityConverter.ConvertToType(original, typeof(PK8), out _)!;
        Assert.NotNull(transferred);
        transferred.Species = (ushort)Species.Raichu;
        transferred.CurrentLevel = 70;

        var observation = Read(transferred, new EventObservationMatcher());

        Assert.Equal(SourceA, Assert.Single(observation.EventMatches!).SourcePath);
        Assert.Equal("possible", observation.EventMatches![0].Status);
    }

    [Fact]
    public void CapturedOpenSessionDoesNotReadLaterUserEdits()
    {
        var card = Card();
        Load((SourceA, card));
        var save = Trainer();
        save.SetBoxSlotAtIndex(card.ConvertToPKM(save), 0, 0, EntityImportSettings.None);
        using var session = new SaveEngineSession(save, "test");
        var captured = PokedexObservationReader.CaptureSave(session);
        save.SetBoxSlotAtIndex(save.BlankPKM, 0, 0, EntityImportSettings.None);

        var observation = Assert.Single(captured.Read(new EventObservationMatcher()));

        Assert.Equal(SourceA, Assert.Single(observation.EventMatches!).SourcePath);
        Assert.Empty(PokedexObservationReader.ReadSave(session));
    }

    [Fact]
    public void NonEnglishPokemonRemainInInventoryWithoutEnglishEventCandidates()
    {
        var card = Card();
        Load((SourceA, card));
        var mon = card.ConvertToPKM(Trainer());
        mon.Language = (int)LanguageID.Japanese;

        var observation = Read(mon, new EventObservationMatcher());

        Assert.Equal((int)Species.Pikachu, observation.Species);
        Assert.Empty(observation.EventMatches!);
        Assert.True(observation.EventScanComplete);
    }

    [Fact]
    public void RawGalleryCardCanCompleteMatchingWhenEmbeddedMetadataIsMoreRestrictive()
    {
        var embedded = EncounterEvent.MGDB_G5.First(card => card.IsEntity && card.Species > 0 && !card.IsEgg
            && card.RestrictLanguage == (int)LanguageID.English && card.CanBeReceivedByVersion(GameVersion.B));
        Load((SourceA, embedded)); // Raw PGF serialization omits embedded language/version metadata.

        var observation = Read(embedded.ConvertToPKM(Trainer()), new EventObservationMatcher());

        Assert.Equal(SourceA, Assert.Single(observation.EventMatches!).SourcePath);
        Assert.Equal("possible", observation.EventMatches![0].Status);
        Assert.True(observation.EventScanComplete);
    }

    private void Load(params (string Path, PGF Card)[] cards)
    {
        foreach (var (path, card) in cards)
        {
            var file = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, card.Write());
        }
        EncounterEvent.RefreshMGDB(_root);
        EventDatabaseService.IndexArchive(_root);
    }

    private static PGF Card(int id = 3990, string title = "Test distribution")
    {
        var card = new PGF
        {
            CardTitle = title, CardID = id, Species = (ushort)Species.Pikachu, IsEntity = true,
            OriginalTrainerName = "PKForge", TID16 = 54321, SID16 = 1234, OTGender = 0,
            Ball = (byte)Ball.Poke, Nature = Nature.Hardy, Gender = 2, Language = 2,
            Location = 40001, PIDType = 1, Move1 = (ushort)Move.ThunderShock,
            Date = new DateOnly(2012, 5, 6),
        };
        // The pinned PKHeX PGF.Level setter writes the egg byte; set the real level byte.
        card.Data[0x5B] = 10;
        return card;
    }

    private static SaveFile Trainer() => BlankSaveFile.Get(GameVersion.B, "Kyle", LanguageID.English);
    private static PokedexObservation Read(PKM mon, EventObservationMatcher matcher) =>
        PokedexObservationReader.ReadBank(Entry(mon), Bytes(mon), matcher)!;
    private static BankEntry Entry(PKM mon) => new(Guid.NewGuid(), 0, 0,
        new(mon.Species, mon.Form, mon.IsShiny, mon.Nickname, mon.CurrentLevel, mon.Format, "test", EntityBytes.FormatOf(mon)), DateTimeOffset.UtcNow);
    private static byte[] Bytes(PKM mon)
    {
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        return bytes;
    }

    public void Dispose()
    {
        EncounterEvent.RefreshMGDB();
        EventDatabaseService.IndexArchive(_root + "-missing");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
