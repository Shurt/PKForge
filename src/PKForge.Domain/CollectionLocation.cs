namespace PKForge.Domain;

/// <summary>One owned Pokemon and the save, bank, or party slot where it is stored.</summary>
public sealed record CollectionLocation(
    int Species,
    int Form,
    bool Shiny,
    string SourceLabel,
    string SourceDetail,
    int Box,
    int Slot,
    string? Nickname = null)
{
    /// <summary>The storage position shown to the user. Stored indexes are zero based.</summary>
    public string PositionLabel => Box == -1
        ? $"Party / slot {Slot + 1}"
        : $"Box {Box + 1} / slot {Slot + 1}";
}

/// <summary>Queries over the individual copies found across saves and the bank.</summary>
public static class CollectionLocations
{
    public static List<CollectionLocation> Find(
        IEnumerable<CollectionLocation> locations,
        int species,
        int? form = null,
        bool shinyOnly = false) =>
        [.. locations
            .Where(location => location.Species == species)
            .Where(location => form is null || location.Form == form.Value)
            .Where(location => !shinyOnly || location.Shiny)
            .OrderBy(location => location.SourceLabel, StringComparer.Ordinal)
            .ThenBy(location => location.SourceDetail, StringComparer.Ordinal)
            .ThenBy(location => location.Box)
            .ThenBy(location => location.Slot)];
}
