using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Reads collection traits without converting, editing or exporting Pokemon data.</summary>
public static class PokedexObservationReader
{
    public static IReadOnlyList<PokedexObservation> ReadSave(ISaveEngineSession session, bool unsupported = false,
        EventObservationMatcher? eventMatcher = null, CancellationToken cancellationToken = default) =>
        CaptureSave(session, unsupported, cancellationToken).Read(eventMatcher, cancellationToken);

    /// <summary>Copy the UI-owned open session before running event matching on a worker thread.</summary>
    public static CapturedSave CaptureSave(ISaveEngineSession session, bool unsupported = false,
        CancellationToken cancellationToken = default)
    {
        var observations = new List<(PokedexObservation Observation, PKM? Entity)>();
        foreach (var slot in session.Snapshot.Slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot.Species is not > 0 || slot.IsEgg) continue;
            var id = $"{slot.Box}:{slot.Slot}";
            if (session is SaveEngineSession standard && !unsupported)
            {
                var entity = standard.GetEntity(slot.Box, slot.Slot).Clone();
                if (Describe(entity, id, slot.Box, slot.Slot, null, cancellationToken) is { } observation)
                    observations.Add((observation, entity));
            }
            else
                observations.Add((new(id, slot.Species.Value, slot.Form, null, null, null,
                    "unsupported", slot.IsShiny, slot.Nickname, slot.Box, slot.Slot), null));
        }
        return new CapturedSave(observations, unsupported || session is not SaveEngineSession);
    }

    public sealed class CapturedSave
    {
        private readonly IReadOnlyList<(PokedexObservation Observation, PKM? Entity)> _observations;
        public bool Unsupported { get; }
        internal CapturedSave(IReadOnlyList<(PokedexObservation Observation, PKM? Entity)> observations, bool unsupported)
        {
            _observations = observations;
            Unsupported = unsupported;
        }

        public IReadOnlyList<PokedexObservation> Read(EventObservationMatcher? eventMatcher = null,
            CancellationToken cancellationToken = default)
        {
            var result = new List<PokedexObservation>();
            foreach (var (observation, entity) in _observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(eventMatcher is null ? observation : entity is null
                    ? observation with { EventScanComplete = false }
                    : eventMatcher.Match(entity, observation, cancellationToken));
            }
            return result;
        }
    }

    public static PokedexObservation? ReadBank(BankEntry entry, byte[] bytes,
        EventObservationMatcher? eventMatcher = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var format = entry.Info.Format ?? EntityBytes.InferFormat(entry.Info, bytes);
        if (format is null)
            return new(entry.Id.ToString(), entry.Info.Species, entry.Info.Form, null, null, null,
                "unknown", entry.Info.Shiny, entry.Info.Nickname, entry.Box, entry.Slot)
                { EventScanComplete = eventMatcher is null ? null : false };
        var entity = EntityBytes.Parse(bytes, format)
            ?? throw new InvalidDataException("A Bank entry could not be read.");
        return Describe(entity, entry.Id.ToString(), entry.Box, entry.Slot, eventMatcher, cancellationToken);
    }

    private static PokedexObservation? Describe(PKM entity, string id, int box, int slot,
        EventObservationMatcher? eventMatcher, CancellationToken cancellationToken)
    {
        if (entity.Species == 0 || entity.IsEgg) return null;
        var observation = new PokedexObservation(id, entity.Species, entity.Form, entity.Format == 1 ? null : entity.Gender,
            entity is IFormArgument form ? checked((int)form.FormArgument) : null,
            entity is IGigantamax gigantamax ? gigantamax.CanGigantamax : false,
            EntityBytes.FormatOf(entity), entity.IsShiny, entity.Nickname, box, slot);
        return eventMatcher is null ? observation : eventMatcher.Match(entity, observation, cancellationToken);
    }
}
