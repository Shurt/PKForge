namespace PKForge.Domain;

public sealed record PokedexLocalCopy(
    PokedexInventorySource Source,
    PokedexInventoryObservation Entry);

public sealed record PokedexScannedCopy(
    PokedexSource Source,
    PokedexObservation Observation);

public sealed record PokedexMissingLocalEntry(
    string DexEntryId,
    string DisplayName,
    bool Shiny,
    IReadOnlyList<PokedexLocalCopy> Copies);

public static class PokedexCollectionQueue
{
    public static IReadOnlyList<PokedexMissingLocalEntry> FindMissing(
        IReadOnlyList<PokedexChecklistEntry>? checklist,
        IReadOnlyList<PokedexInventorySource>? sources)
    {
        if (checklist is null || sources is null)
            return [];

        var copiesByEntry = new Dictionary<(string DexEntryId, bool Shiny), List<PokedexLocalCopy>>();
        foreach (var source in sources)
        {
            if (source?.Observations is null)
                continue;

            foreach (var entry in source.Observations)
            {
                if (entry?.Observation is null || entry.DexEntryIds is null ||
                    !string.Equals(entry.MappingStatus, "matched", StringComparison.Ordinal))
                    continue;

                foreach (var dexEntryId in entry.DexEntryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
                {
                    var key = (dexEntryId, entry.Observation.Shiny);
                    if (!copiesByEntry.TryGetValue(key, out var copies))
                    {
                        copies = [];
                        copiesByEntry.Add(key, copies);
                    }
                    copies.Add(new PokedexLocalCopy(source, entry));
                }
            }
        }

        var result = new List<PokedexMissingLocalEntry>();
        foreach (var item in checklist)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.DexEntryId))
                continue;

            AddIfMissing(item, shiny: false, item.NormalOwned, copiesByEntry, result);
            AddIfMissing(item, shiny: true, item.ShinyOwned, copiesByEntry, result);
        }
        return result;
    }

    private static void AddIfMissing(
        PokedexChecklistEntry item,
        bool shiny,
        bool owned,
        IReadOnlyDictionary<(string DexEntryId, bool Shiny), List<PokedexLocalCopy>> copiesByEntry,
        ICollection<PokedexMissingLocalEntry> result)
    {
        if (owned || !copiesByEntry.TryGetValue((item.DexEntryId, shiny), out var copies) || copies.Count == 0)
            return;

        var ordered = copies
            .OrderBy(copy => copy.Source.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(copy => copy.Source.SourceId, StringComparer.Ordinal)
            .ThenBy(copy => copy.Entry.Observation.Box)
            .ThenBy(copy => copy.Entry.Observation.Slot)
            .ThenBy(copy => copy.Entry.Observation.SlotId, StringComparer.Ordinal)
            .ToArray();
        result.Add(new PokedexMissingLocalEntry(item.DexEntryId, item.DisplayName, shiny, ordered));
    }
}

public static class PokedexBankPlanCandidates
{
    public static IReadOnlyList<PokedexLocalCopy> Find(
        PokedexBankPlanTarget? target,
        IReadOnlyList<PokedexInventorySource>? sources)
    {
        if (target?.Match is null || sources is null)
            return [];

        return sources
            .Where(source => source?.Observations is not null)
            .SelectMany(source => source.Observations
                .Where(entry => entry?.Observation is not null && Matches(target, entry.Observation))
                .Select(entry => new PokedexLocalCopy(source, entry)))
            .OrderBy(copy => copy.Source.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(copy => copy.Source.SourceId, StringComparer.Ordinal)
            .ThenBy(copy => copy.Entry.Observation.Box)
            .ThenBy(copy => copy.Entry.Observation.Slot)
            .ToArray();
    }

    public static IReadOnlyList<PokedexScannedCopy> Find(
        PokedexBankPlanTarget? target,
        IReadOnlyList<PokedexSource>? sources)
    {
        if (target?.Match is null || sources is null)
            return [];

        return sources
            .Where(source => source is { Complete: true, Observations: not null })
            .SelectMany(source => source.Observations
                .Where(observation => observation is not null && Matches(target, observation))
                .Select(observation => new PokedexScannedCopy(source, observation)))
            .OrderBy(copy => copy.Source.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(copy => copy.Source.SourceId, StringComparer.Ordinal)
            .ThenBy(copy => copy.Observation.Box)
            .ThenBy(copy => copy.Observation.Slot)
            .ToArray();
    }

    public static bool Matches(PokedexBankPlanTarget? target, PokedexObservation? observation)
    {
        var match = target?.Match;
        if (match?.Species is not { Count: > 0 } || observation is null ||
            !match.Species.Contains(observation.Species))
            return false;
        var format = observation.Format?.Trim();
        if (string.IsNullOrEmpty(format) ||
            string.Equals(format, "unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "unsupported", StringComparison.OrdinalIgnoreCase))
            return false;
        if (match.Forms is { Count: > 0 } && !match.Forms.Contains(observation.Form))
            return false;
        if (match.Shiny is { } shiny && observation.Shiny != shiny)
            return false;
        if (match.Formats is { Count: > 0 } &&
            !match.Formats.Any(allowed => string.Equals(allowed?.Trim(), format, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }
}
