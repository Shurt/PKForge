using System.Security.Cryptography;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class CheckpointSaveExportTests
{
    [Theory]
    [InlineData(GameVersion.X, 0x65600, "0004000000055D00", "Pokémon X")]
    [InlineData(GameVersion.Y, 0x65600, "0004000000055E00", "Pokémon Y")]
    [InlineData(GameVersion.OR, 0x76000, "000400000011C400", "Pokémon Omega Ruby")]
    [InlineData(GameVersion.AS, 0x76000, "000400000011C500", "Pokémon Alpha Sapphire")]
    [InlineData(GameVersion.SN, 0x6BE00, "0004000000164800", "Pokémon Sun")]
    [InlineData(GameVersion.MN, 0x6BE00, "0004000000175E00", "Pokémon Moon")]
    [InlineData(GameVersion.US, 0x6CC00, "00040000001B5000", "Pokémon Ultra Sun")]
    [InlineData(GameVersion.UM, 0x6CC00, "00040000001B5100", "Pokémon Ultra Moon")]
    public void PrepareReturnsValidatedNativeSaveAndDigitalTitleMetadata(
        GameVersion version, int size, string titleId, string titleName)
    {
        using var session = Open(version);

        Assert.True(CheckpointSaveExport.IsSupported(session));
        var export = CheckpointSaveExport.Prepare(session);

        Assert.Equal(titleId, export.TitleId);
        Assert.Equal(titleName, export.TitleName);
        Assert.Equal(size, export.Data.Length);
        if (version is GameVersion.SN or GameVersion.MN or GameVersion.US or GameVersion.UM)
            AssertValidGen7Signature(export.Data);

        var reopened = Reopen(version, export.Data);
        Assert.Equal(version, reopened.Version);
        Assert.True(reopened.ChecksumsValid, reopened.ChecksumInfo);
    }

    [Theory]
    [InlineData(GameVersion.X)]
    [InlineData(GameVersion.OR)]
    [InlineData(GameVersion.SN)]
    [InlineData(GameVersion.US)]
    public void CorruptSourceIsRefusedBeforeSerializationCanRepairItsChecksums(GameVersion version)
    {
        var bytes = Create(version);
        bytes[0x200] ^= 0xFF;
        using var session = new SaveEngineSession(bytes, "main");

        Assert.Throws<InvalidDataException>(() => CheckpointSaveExport.Prepare(session));
    }

    [Theory]
    [InlineData(GameVersion.Y)]
    [InlineData(GameVersion.AS)]
    [InlineData(GameVersion.MN)]
    [InlineData(GameVersion.UM)]
    public void PrepareDoesNotMutateTheLiveSaveOrOriginalBytes(GameVersion version)
    {
        using var session = Open(version);
        var trainer = session.GetTrainer();
        session.SetTrainer(trainer with { Money = trainer.Money + 1 });
        var liveBefore = session.SaveFile.Data.ToArray();
        var originalBefore = session.OriginalBytes.ToArray();
        Assert.False(session.SaveFile.ChecksumsValid);

        var export = CheckpointSaveExport.Prepare(session);

        Assert.Equal(liveBefore, session.SaveFile.Data.ToArray());
        Assert.Equal(originalBefore, session.OriginalBytes.ToArray());
        Assert.False(session.SaveFile.ChecksumsValid);
        if (version is GameVersion.MN or GameVersion.UM)
            AssertValidGen7Signature(export.Data);
        var reopened = Reopen(version, export.Data);
        Assert.True(reopened.ChecksumsValid);
        Assert.Equal(session.SaveFile.Money, reopened.Money);
    }

    [Theory]
    [InlineData(GameVersion.X)]
    [InlineData(GameVersion.OR)]
    [InlineData(GameVersion.SN)]
    [InlineData(GameVersion.US)]
    public void UnrecognizableEditedSaveIsRejected(GameVersion version)
    {
        using var session = Open(version);
        session.SaveFile.Data.Slice(session.SaveFile.Data.Length - 0x1F0, 4).Clear();

        Assert.Throws<InvalidDataException>(() => CheckpointSaveExport.Prepare(session));
    }

    [Theory]
    [InlineData(GameVersion.SN)]
    [InlineData(GameVersion.US)]
    public void UnsignedButOtherwiseValidGen7SourceIsAcceptedAndExportedSigned(GameVersion version)
    {
        var bytes = Create(version);
        Gen7Signature(bytes).Clear();
        using var session = new SaveEngineSession(bytes, "main");

        Assert.True(CheckpointSaveExport.IsSupported(session));
        var export = CheckpointSaveExport.Prepare(session);

        AssertValidGen7Signature(export.Data);
        Assert.True(Reopen(version, export.Data).ChecksumsValid);
    }

    [Fact]
    public void SunMoonDemoWithEmptyBoxLayoutIsRejected()
    {
        var save = (SAV7SM)BlankSaveFile.Get(GameVersion.SN, "PKForge", LanguageID.English);
        save.BoxLayout.Data[..0x4C4].Clear();
        AddRecognitionMarker(save);
        using var session = new SaveEngineSession(save.Write(), "main");

        Assert.False(CheckpointSaveExport.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => CheckpointSaveExport.Prepare(session));
    }

    [Fact]
    public void OmegaRubyAlphaSapphireDemoIsRejected()
    {
        var demo = new SAV6AODemo { Version = GameVersion.OR };
        using var session = new SaveEngineSession(demo, "main");

        Assert.False(CheckpointSaveExport.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => CheckpointSaveExport.Prepare(session));
    }

    [Theory]
    [InlineData(GameVersion.D)]
    [InlineData(GameVersion.C)]
    [InlineData(GameVersion.SW)]
    public void DsVirtualConsoleAndSwitchSavesAreNotSupported(GameVersion version)
    {
        using var session = new SaveEngineSession(
            BlankSaveFile.Get(version, "PKForge", LanguageID.English), "main");

        Assert.False(CheckpointSaveExport.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => CheckpointSaveExport.Prepare(session));
    }

    [Theory]
    [InlineData(GameVersion.SN, GameVersion.US)]
    [InlineData(GameVersion.US, GameVersion.SN)]
    public void VersionFromTheWrongGen7PairIsRejected(GameVersion fixtureVersion, GameVersion wrongVersion)
    {
        using var session = Open(fixtureVersion);
        session.SaveFile.Version = wrongVersion;

        Assert.False(CheckpointSaveExport.IsSupported(session));
        Assert.Throws<InvalidDataException>(() => CheckpointSaveExport.Prepare(session));
    }

    private static SaveEngineSession Open(GameVersion version) =>
        new(Create(version), "main");

    private static byte[] Create(GameVersion version)
    {
        var save = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        if (save is SAV7 gen7)
            gen7.SetBoxName(0, "Box 1");
        AddRecognitionMarker(save);
        return save.Write().ToArray();
    }

    private static void AddRecognitionMarker(SaveFile save) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(save.Data[^0x1F0..], 0x42454546);

    private static SaveFile Reopen(GameVersion version, byte[] data) => version switch
    {
        GameVersion.X or GameVersion.Y => new SAV6XY(data.ToArray()),
        GameVersion.OR or GameVersion.AS => new SAV6AO(data.ToArray()),
        GameVersion.SN or GameVersion.MN => new SAV7SM(data.ToArray()),
        GameVersion.US or GameVersion.UM => new SAV7USUM(data.ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static Span<byte> Gen7Signature(byte[] data)
    {
        var offset = data.Length == SaveUtil.SIZE_G7USUM ? 0x6C100 : 0x6BB00;
        return data.AsSpan(offset, MemeCrypto.SaveFileSignatureLength);
    }

    private static void AssertValidGen7Signature(byte[] data)
    {
        var signature = Gen7Signature(data);
        Assert.True(MemeCrypto.VerifyMemeData(
            signature, out var decoded, MemeKeyIndex.PokedexAndSaveFile));

        var checksumLength = data.Length == SaveUtil.SIZE_G7USUM ? 0x150 : 0x140;
        var expectedHash = SHA256.HashData(data.AsSpan(data.Length - 0x200, checksumLength));
        Assert.Equal(expectedHash, decoded[..expectedHash.Length]);
    }
}
