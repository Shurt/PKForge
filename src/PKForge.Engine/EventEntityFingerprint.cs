using System.Security.Cryptography;
using System.Text;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Identity excludes training, nickname, current species/form, handler and storage position.
/// Origin edits and transfers which rewrite these fields deliberately require a new attribution.</summary>
public static class EventEntityFingerprint
{
    public static string? Create(PKM entity)
    {
        if (entity.Generation < 3 || entity.Species == 0) return null;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("pkforge-event-origin-v1");
            writer.Write((int)entity.Version);
            writer.Write(entity.PID);
            // Pre-Gen6 transfers introduce an EC derived from the PID, so do not use it there.
            writer.Write(entity.Generation >= 6 ? entity.EncryptionConstant : 0U);
            writer.Write(entity.ID32);
            writer.Write(entity.OriginalTrainerName);
            writer.Write(entity.OriginalTrainerGender);
            writer.Write(entity.Language);
            writer.Write(entity.Ball);
            writer.Write(entity.FatefulEncounter);
            writer.Write((int)entity.Nature);
            writer.Write(entity.MetLocation);
            writer.Write(entity.EggLocation);
            writer.Write(entity.MetLevel);
            writer.Write(entity.MetDate?.DayNumber ?? -1);
            writer.Write(entity.EggMetDate?.DayNumber ?? -1);
            writer.Write(entity.IV_HP);
            writer.Write(entity.IV_ATK);
            writer.Write(entity.IV_DEF);
            writer.Write(entity.IV_SPE);
            writer.Write(entity.IV_SPA);
            writer.Write(entity.IV_SPD);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
