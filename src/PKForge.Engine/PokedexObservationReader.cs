using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Reads collection traits without converting, editing or exporting Pokemon data.</summary>
public static class PokedexObservationReader
{
    public static IReadOnlyList<PokedexObservation> ReadSave(ISaveEngineSession session, bool unsupported = false)
    {
        var observations = new List<PokedexObservation>();
        foreach (var slot in session.Snapshot.Slots)
        {
            if (slot.Species is not > 0 || slot.IsEgg) continue;
            var id = $"{slot.Box}:{slot.Slot}";
            if (session is SaveEngineSession standard && !unsupported)
            {
                if (Describe(standard.GetEntity(slot.Box, slot.Slot), id, slot.Box, slot.Slot) is { } observation)
                    observations.Add(observation);
            }
            else
                observations.Add(new(id, slot.Species.Value, slot.Form, null, null, null,
                    "unsupported", slot.IsShiny, slot.Nickname, slot.Box, slot.Slot));
        }
        return observations;
    }

    public static PokedexObservation? ReadBank(BankEntry entry, byte[] bytes)
    {
        var format = entry.Info.Format ?? EntityBytes.InferFormat(entry.Info, bytes);
        if (format is null)
            return new(entry.Id.ToString(), entry.Info.Species, entry.Info.Form, null, null, null,
                "unknown", entry.Info.Shiny, entry.Info.Nickname, entry.Box, entry.Slot);
        var entity = EntityBytes.Parse(bytes, format)
            ?? throw new InvalidDataException("A Bank entry could not be read.");
        return Describe(entity, entry.Id.ToString(), entry.Box, entry.Slot);
    }

    private static PokedexObservation? Describe(PKM entity, string id, int box, int slot)
    {
        if (entity.Species == 0 || entity.IsEgg) return null;
        return new(id, entity.Species, entity.Form, entity.Format == 1 ? null : entity.Gender,
            entity is IFormArgument form ? checked((int)form.FormArgument) : null,
            entity is IGigantamax gigantamax ? gigantamax.CanGigantamax : false,
            EntityBytes.FormatOf(entity), entity.IsShiny, entity.Nickname, box, slot);
    }
}
