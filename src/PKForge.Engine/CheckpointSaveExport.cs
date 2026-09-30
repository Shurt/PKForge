using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>A native main save plus the title metadata Checkpoint uses for a backup.</summary>
public sealed record CheckpointSavePackage(byte[] Data, string TitleId, string TitleName);

/// <summary>Prepares whole-save exports that can be restored to digital 3DS titles through Checkpoint.</summary>
public static class CheckpointSaveExport
{
    private const string PokemonXTitleId = "0004000000055D00";
    private const string PokemonYTitleId = "0004000000055E00";

    public static bool IsSupported(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session is SaveEngineSession engineSession
            && engineSession.SaveFile is SAV6XY xy
            && xy.Version is (GameVersion.X or GameVersion.Y)
            && engineSession.OriginalBytes.Length == xy.Data.Length;
    }

    public static CheckpointSavePackage Prepare(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engineSession || engineSession.SaveFile is not SAV6XY source)
            throw new NotSupportedException("Checkpoint transfer currently supports only native Pokémon X and Y saves.");
        if (!source.IsVersionValid())
            throw new InvalidDataException("The Pokémon X/Y save does not identify a valid game version.");

        // PKHeX refreshes block checksums when Write() runs. Check the bytes that were
        // originally opened first, so serialization cannot disguise a corrupt source file.
        var original = engineSession.OriginalBytes;
        if (original.IsEmpty || original.Length != source.Data.Length)
            throw new InvalidDataException("Checkpoint transfer requires a native, file-backed Pokémon X/Y save.");

        var opened = new SAV6XY(original.ToArray());
        if (!opened.IsVersionValid() || !opened.ChecksumsValid)
            throw new InvalidDataException("The original Pokémon X/Y save is corrupt or has invalid checksums.");

        var copy = (SAV6XY)source.Clone();
        var data = copy.Write().ToArray();
        var verified = new SAV6XY(data);
        if (!verified.IsVersionValid() || verified.Version != source.Version || !verified.ChecksumsValid)
            throw new InvalidDataException("The Pokémon X/Y save could not be serialized safely for Checkpoint.");

        return source.Version switch
        {
            GameVersion.X => new CheckpointSavePackage(data, PokemonXTitleId, "Pokémon X"),
            GameVersion.Y => new CheckpointSavePackage(data, PokemonYTitleId, "Pokémon Y"),
            _ => throw new InvalidDataException("The Pokémon X/Y save does not identify a valid game version."),
        };
    }
}
