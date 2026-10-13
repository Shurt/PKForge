using System.Security.Cryptography;
using System.Text;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>Optional integration scan. Nothing here writes to saves or the Bank.</summary>
internal static class PokedexCollectionScanner
{
    public static Task<IReadOnlyList<PokedexSource>> ScanAsync(CancellationToken cancellationToken)
    {
        var services = IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("No app services.");
        var sessions = services.GetRequiredService<ISaveSessionService>();
        var engine = services.GetRequiredService<ISaveEngine>();
        var access = services.GetRequiredService<ISaveFileAccess>();
        var bank = services.GetRequiredService<IBankService>();
        var receipts = services.GetRequiredService<IEventReceiptStore>();
        var writer = services.GetRequiredService<ISafeSaveWriter>();
        var picker = services.GetRequiredService<SavePickerViewModel>();
        // Hiding a save only hides its picker row; it still belongs to the collection.
        var saves = picker.Saves.Concat(picker.HiddenSaves).ToArray();
        var entries = bank.GetAll().ToArray();
        var sources = new List<PokedexSource>();
        PokedexObservationReader.CapturedSave? openSnapshot = null;
        string? openLabel = null;
        var openId = sessions.Current?.Document.DocumentId;
        if (sessions.CurrentSession is { } live && openId is not null)
        {
            var save = saves.FirstOrDefault(s => s.DocumentId == openId);
            var label = save is null ? sessions.Current!.Document.DisplayName : Label(save);
            openLabel = label + " · open session";
            try
            {
                // Snapshot the UI-owned live session before starting background reads.
                openSnapshot = PokedexObservationReader.CaptureSave(live, Unsupported(save, live, openId, writer), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                sources.Add(new(SourceId(openId), label, "save", false, []) { EventScanComplete = false });
            }
        }
        return Task.Run<IReadOnlyList<PokedexSource>>(async () =>
        {
            var archiveLoaded = EventArchive.EnsureAllLoaded(cancellationToken);
            var eventMatcher = new EventObservationMatcher(receipts);
            if (openSnapshot is not null && openId is not null)
            {
                var observations = openSnapshot.Read(eventMatcher, cancellationToken);
                sources.Add(new(SourceId(openId), openLabel!, "save", true, observations)
                {
                    EventScanComplete = archiveLoaded && !openSnapshot.Unsupported && EventComplete(observations, eventMatcher),
                });
            }
            foreach (var save in saves.DistinctBy(s => s.DocumentId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (save.DocumentId == openId) continue;
                try
                {
                    var bytes = await access.ReadAsync(save.DocumentId, cancellationToken);
                    using var session = engine.OpenSession(bytes, save.EngineHint, save.Format);
                    var unsupported = Unsupported(save, session, save.DocumentId, writer);
                    var observations = PokedexObservationReader.ReadSave(session, unsupported, eventMatcher, cancellationToken);
                    sources.Add(new(SourceId(save.DocumentId), Label(save), "save", true, observations)
                    {
                        EventScanComplete = archiveLoaded && !unsupported && EventComplete(observations, eventMatcher),
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    sources.Add(new(SourceId(save.DocumentId), Label(save), "save", false, []) { EventScanComplete = false });
                }
            }
            var bankObservations = new List<PokedexObservation>();
            var complete = true;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (PokedexObservationReader.ReadBank(entry, bank.GetData(entry.Id), eventMatcher, cancellationToken) is { } observation)
                        bankObservations.Add(observation);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { complete = false; }
            }
            // An incomplete Bank read must never remove previously reported holdings.
            sources.Add(new("bank", "PKForge Bank", "bank", complete, complete ? bankObservations : [])
            {
                EventScanComplete = complete && archiveLoaded && EventComplete(bankObservations, eventMatcher),
            });
            cancellationToken.ThrowIfCancellationRequested();
            return sources;
        }, cancellationToken);
    }

    private static bool EventComplete(IReadOnlyList<PokedexObservation> observations, EventObservationMatcher matcher) =>
        matcher.IsComplete && observations.All(observation => observation.EventScanComplete == true);

    internal static string SourceId(string documentId) =>
        "save-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId))).ToLowerInvariant();

    private static string Label(DetectedSave save) => string.Join(" · ",
        new[] { save.GameLabel, save.TrainerName, save.FileName }.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static bool Unsupported(DetectedSave? save, ISaveEngineSession session, string documentId, ISafeSaveWriter writer) =>
        session is not SaveEngineSession
        || save?.Format is SaveFormat.Unbound or SaveFormat.RadicalRed or SaveFormat.GsChronicles
        || save?.Identity?.GameChoiceId?.StartsWith("hack", StringComparison.Ordinal) == true
        || writer.LayoutRiskOf(documentId, session.Snapshot) is not null
        || session.GameNames.Any(name => name.Contains("Luminescent", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Compass", StringComparison.OrdinalIgnoreCase));
}
