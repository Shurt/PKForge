using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class PokedexObservationReaderTests
{
    [Fact]
    public void BankReadPreservesRawAlcremieBytesAndReportsStoredTraits()
    {
        var mon = new PK8
        {
            Species = 869,
            Form = 8,
            FormArgument = 6,
            Gender = 1,
            CanGigantamax = true,
            Version = GameVersion.SW,
            CurrentLevel = 50,
            Nickname = "Alcremie",
        };
        var bytes = StoredBytes(mon);
        var before = bytes.ToArray();
        var entry = Entry(mon, format: nameof(PK8));

        var observation = PokedexObservationReader.ReadBank(entry, bytes);

        Assert.NotNull(observation);
        Assert.Equal(869, observation.Species);
        Assert.Equal(8, observation.Form);
        Assert.Equal(6, observation.FormArgument);
        Assert.Equal(1, observation.Gender);
        Assert.True(observation.CanGigantamax);
        Assert.Equal(mon.IsShiny, observation.Shiny);
        Assert.Equal(nameof(PK8), observation.Format);
        Assert.Equal(entry.Id.ToString(), observation.SlotId);
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void SaveReadExcludesEggsAndDoesNotChangeTheSave()
    {
        var save = BlankSaveFile.Get(GameVersion.SW, "Kyle", LanguageID.English);
        var caught = new PK8
        {
            Species = 25,
            Version = GameVersion.SW,
            CurrentLevel = 20,
            Nickname = "Sparky",
        };
        var egg = new PK8
        {
            Species = 133,
            Version = GameVersion.SW,
            CurrentLevel = 1,
            IsEgg = true,
            Nickname = "Egg",
        };
        caught.RefreshChecksum();
        egg.RefreshChecksum();
        save.SetBoxSlotAtIndex(caught, 0, 0, EntityImportSettings.None);
        save.SetBoxSlotAtIndex(egg, 0, 1, EntityImportSettings.None);
        using var session = new SaveEngineSession(save, null);
        var before = session.Serialize().ToArray();

        var observations = PokedexObservationReader.ReadSave(session);

        var observation = Assert.Single(observations);
        Assert.Equal("0:0", observation.SlotId);
        Assert.Equal(25, observation.Species);
        Assert.Equal(nameof(PK8), observation.Format);
        Assert.Equal(before, session.Serialize().ToArray());
    }

    [Fact]
    public void AmbiguousLegacyBankFormatStaysUnknownAndBytesAreNotParsedAsDefault()
    {
        var mon = new PB8
        {
            Species = 25,
            Form = 0,
            Gender = 1,
            Version = GameVersion.BD,
            CurrentLevel = 30,
            Nickname = "Pika",
        };
        var bytes = StoredBytes(mon);
        var before = bytes.ToArray();
        var entry = Entry(mon, format: null, source: "old bank");
        Assert.Null(EntityBytes.InferFormat(entry.Info, bytes));

        var observation = PokedexObservationReader.ReadBank(entry, bytes);

        Assert.NotNull(observation);
        Assert.Equal("unknown", observation.Format);
        Assert.Null(observation.Gender);
        Assert.Null(observation.FormArgument);
        Assert.Null(observation.CanGigantamax);
        Assert.Equal(entry.Info.Species, observation.Species);
        Assert.Equal(entry.Info.Form, observation.Form);
        Assert.Equal(before, bytes);
    }

    private static byte[] StoredBytes(PKM mon)
    {
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        return bytes;
    }

    private static BankEntry Entry(PKM mon, string? format, string source = "test") =>
        new(Guid.NewGuid(), 2, 3,
            new(mon.Species, mon.Form, mon.IsShiny, mon.Nickname, mon.CurrentLevel, mon.Format, source, format),
            DateTimeOffset.UtcNow);
}
