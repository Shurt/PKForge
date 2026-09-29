using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Services;

internal sealed record CollectionLocationScan(
    IReadOnlyList<CollectionLocation> Locations, IReadOnlyList<string> UnreadableSources);

/// <summary>Read-only inventory for the dex and location finder. Capture live state before
/// background I/O so an open save's unsaved edits take precedence over its disk copy.</summary>
internal static class CollectionLocationScanner
{
    public static Task<CollectionLocationScan> ScanAsync(ISaveEngineSession? open,
        CancellationToken cancellationToken = default)
    {
        var services = IPlatformApplication.Current?.Services;
        var sessions = services?.GetService<ISaveSessionService>();
        var saves = services?.GetService<SavePickerViewModel>()?.Saves.ToArray() ?? [];
        var bank = services?.GetService<IBankService>()?.GetAll().ToArray() ?? [];
        var access = services?.GetService<ISaveFileAccess>();
        var engine = services?.GetService<ISaveEngine>();
        var openId = open is null ? null : sessions?.Current?.Document.DocumentId;
        var openSave = saves.FirstOrDefault(s => s.DocumentId == openId);
        var openLabel = openSave?.GameLabel ?? open?.Snapshot.DisplayName ?? "Open save";
        var openDetail = openSave is null
            ? sessions?.Current?.Document.DisplayName ?? "Current session"
            : Describe(openSave);
        var liveSlots = open?.Snapshot.Slots.ToArray() ?? [];

        return Task.Run(async () =>
        {
            var locations = new List<CollectionLocation>();
            var unreadable = new List<string>();
            foreach (var entry in bank)
                if (entry.Info.Species > 0)
                    locations.Add(new(entry.Info.Species, entry.Info.Form, entry.Info.Shiny,
                        "PKForge Bank", "Stored locally", entry.Box, entry.Slot, entry.Info.Nickname));
            AddSlots(locations, liveSlots, openLabel, openDetail + " · open session");

            foreach (var save in saves.DistinctBy(s => s.DocumentId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (save.DocumentId == openId) continue;
                if (access is null || engine is null)
                {
                    unreadable.Add(save.GameLabel + " · " + Describe(save));
                    continue;
                }
                try
                {
                    var bytes = await access.ReadAsync(save.DocumentId, cancellationToken);
                    using var session = engine.OpenSession(bytes, save.EngineHint, save.Format);
                    AddSlots(locations, session.Snapshot.Slots, save.GameLabel, Describe(save));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // One revoked grant or unreadable save must not hide the other holdings.
                    unreadable.Add(save.GameLabel + " · " + Describe(save));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new CollectionLocationScan(locations, unreadable);
        }, cancellationToken);
    }

    private static string Describe(DetectedSave save) => string.Join(" · ",
        new[] { save.TrainerName, save.FileName, SaveDescriptions.Detail(save) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static void AddSlots(List<CollectionLocation> locations, IEnumerable<SlotSummary> slots,
        string label, string detail)
    {
        foreach (var slot in slots)
            if (slot.Species is > 0 && !slot.IsEgg)
                locations.Add(new(slot.Species.Value, slot.Form, slot.IsShiny,
                    label, detail, slot.Box, slot.Slot, slot.Nickname));
    }
}
