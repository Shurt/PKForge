using System.Security.Cryptography;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>A native main save plus the title metadata Checkpoint uses for a backup.</summary>
public sealed record CheckpointSavePackage(byte[] Data, string TitleId, string TitleName);

/// <summary>Prepares whole-save exports that can be restored to digital 3DS titles through Checkpoint.</summary>
public static class CheckpointSaveExport
{
    public static bool IsSupported(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session is SaveEngineSession engineSession
            && TargetFor(engineSession.SaveFile) is not null
            && !IsSunMoonDemo(engineSession.SaveFile)
            && engineSession.OriginalBytes.Length == NativeSize(engineSession.SaveFile);
    }

    public static CheckpointSavePackage Prepare(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engineSession || NativeSize(engineSession.SaveFile) == 0)
            throw new NotSupportedException("Checkpoint transfer supports native Pokémon X/Y, OR/AS, Sun/Moon, and Ultra Sun/Ultra Moon saves.");
        var source = engineSession.SaveFile;
        var target = TargetFor(source)
            ?? throw new InvalidDataException("The save's game version does not match its 3DS save format.");
        if (IsSunMoonDemo(source))
            throw new NotSupportedException("The Pokémon Sun/Moon demo save is not supported for Checkpoint transfer.");

        // PKHeX refreshes block checksums when Write() runs. Check the bytes that were
        // originally opened first, so serialization cannot disguise corrupt save data.
        // An emulator export may lack a Gen 7 signature; valid block checksums are
        // required here, and the outgoing signature is verified after serialization.
        var original = engineSession.OriginalBytes;
        if (original.Length != NativeSize(source) || source.Data.Length != NativeSize(source))
            throw new InvalidDataException("Checkpoint transfer requires a native, file-backed 3DS save.");

        var opened = Reopen(source, original);
        if (TargetFor(opened) != target || !opened.ChecksumsValid || IsSunMoonDemo(opened))
            throw new InvalidDataException("The original save is corrupt, has invalid checksums, or does not match the supported game.");

        var copy = source.Clone();
        var data = copy.Write().ToArray();
        if (data.Length != NativeSize(source) || (copy is SAV7 gen7 && !HasValidSignature(data, gen7)))
            throw new InvalidDataException("The 3DS save could not be signed safely for Checkpoint.");
        var verified = Reopen(source, data);
        if (TargetFor(verified) != target || !verified.ChecksumsValid || IsSunMoonDemo(verified))
            throw new InvalidDataException("The 3DS save could not be serialized safely for Checkpoint.");

        return new CheckpointSavePackage(data, target.TitleId, target.TitleName);
    }

    // Match the concrete format as well as the version: SAV7.IsVersionValid accepts
    // all four Gen 7 editions, including US/UM stored in the incompatible SM layout.
    private static (string TitleId, string TitleName)? TargetFor(SaveFile save) => save switch
    {
        SAV6XY { Version: GameVersion.X } => ("0004000000055D00", "Pokémon X"),
        SAV6XY { Version: GameVersion.Y } => ("0004000000055E00", "Pokémon Y"),
        SAV6AO { Version: GameVersion.OR } => ("000400000011C400", "Pokémon Omega Ruby"),
        SAV6AO { Version: GameVersion.AS } => ("000400000011C500", "Pokémon Alpha Sapphire"),
        SAV7SM { Version: GameVersion.SN } => ("0004000000164800", "Pokémon Sun"),
        SAV7SM { Version: GameVersion.MN } => ("0004000000175E00", "Pokémon Moon"),
        SAV7USUM { Version: GameVersion.US } => ("00040000001B5000", "Pokémon Ultra Sun"),
        SAV7USUM { Version: GameVersion.UM } => ("00040000001B5100", "Pokémon Ultra Moon"),
        _ => null,
    };

    private static int NativeSize(SaveFile save) => save switch
    {
        SAV6XY => SaveUtil.SIZE_G6XY,
        SAV6AO => SaveUtil.SIZE_G6ORAS,
        SAV7SM => SaveUtil.SIZE_G7SM,
        SAV7USUM => SaveUtil.SIZE_G7USUM,
        _ => 0,
    };

    // Use PKHeX's SAV7.ReloadBattleTeams demo heuristic. SM demos share the retail
    // save class and version, but have no initialized box layout.
    private static bool IsSunMoonDemo(SaveFile save) =>
        save is SAV7SM sm && !sm.BoxLayout.Data[..0x4C4].ContainsAnyExcept<byte>(0);

    private static SaveFile Reopen(SaveFile format, ReadOnlyMemory<byte> bytes)
    {
        // Gen 7 constructors clear the signature in their input buffer before checking
        // block checksums. Always give them scratch bytes, never the outgoing payload.
        if (!SaveUtil.TryGetSaveFile(bytes.ToArray(), out var reopened) || reopened.GetType() != format.GetType())
            throw new InvalidDataException("The save is not a recognized native file for the selected 3DS game.");
        return reopened;
    }

    private static bool HasValidSignature(ReadOnlySpan<byte> data, SAV7 save)
    {
        var signatureOffset = save.AllBlocks[36].Offset + MemeCrypto.SaveFileSignatureOffset;
        var signature = data.Slice(signatureOffset, MemeCrypto.SaveFileSignatureLength);
        if (!MemeCrypto.VerifyMemeData(signature, out var decoded, MemeKeyIndex.PokedexAndSaveFile))
            return false;

        // The signed message embeds the SHA-256 of the checksum table. This checks
        // the bytes returned to the uploader, before any parser normalizes them.
        var tableLength = save is SAV7USUM ? 0x150 : 0x140;
        var hash = SHA256.HashData(data.Slice(data.Length - 0x200, tableLength));
        return decoded.AsSpan(0, hash.Length).SequenceEqual(hash);
    }
}
