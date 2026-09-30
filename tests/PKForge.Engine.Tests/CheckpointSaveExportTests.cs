using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class CheckpointSaveExportTests
{
    [Theory]
    [InlineData(GameVersion.X, "0004000000055D00", "Pokémon X")]
    [InlineData(GameVersion.Y, "0004000000055E00", "Pokémon Y")]
    public void PrepareReturnsValidatedNativeSaveAndDigitalTitleMetadata(
        GameVersion version, string titleId, string titleName)
    {
        using var session = Open(version);

        var export = CheckpointSaveExport.Prepare(session);

        Assert.Equal(titleId, export.TitleId);
        Assert.Equal(titleName, export.TitleName);
        Assert.Equal(SaveUtil.SIZE_G6XY, export.Data.Length);
        var reopened = new SAV6XY(export.Data);
        Assert.Equal(version, reopened.Version);
        Assert.True(reopened.ChecksumsValid, reopened.ChecksumInfo);
    }

    [Fact]
    public void DifferentGameIsRejected()
    {
        using var session = Open(GameVersion.OR);

        Assert.False(CheckpointSaveExport.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => CheckpointSaveExport.Prepare(session));
    }

    [Fact]
    public void CorruptSourceIsRefusedBeforeSerializationCanRepairItsChecksums()
    {
        var bytes = Create(GameVersion.X);
        bytes[0x200] ^= 0xFF;
        using var session = new SaveEngineSession(bytes, "main");

        Assert.Throws<InvalidDataException>(() => CheckpointSaveExport.Prepare(session));
    }

    [Fact]
    public void PrepareDoesNotMutateTheLiveSaveWhileRefreshingExportChecksums()
    {
        using var session = Open(GameVersion.Y);
        var trainer = session.GetTrainer();
        session.SetTrainer(trainer with { Money = trainer.Money + 1 });
        var liveBefore = session.SaveFile.Data.ToArray();
        Assert.False(session.SaveFile.ChecksumsValid);

        var export = CheckpointSaveExport.Prepare(session);

        Assert.Equal(liveBefore, session.SaveFile.Data.ToArray());
        Assert.False(session.SaveFile.ChecksumsValid);
        Assert.True(new SAV6XY(export.Data).ChecksumsValid);
    }

    private static SaveEngineSession Open(GameVersion version) =>
        new(Create(version), "main");

    private static byte[] Create(GameVersion version)
    {
        var save = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        // Blank Gen 6 saves omit the retail BEEF footer marker used by the generic detector.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(save.Data[^0x1F0..], 0x42454546);
        return save.Write().ToArray();
    }
}
