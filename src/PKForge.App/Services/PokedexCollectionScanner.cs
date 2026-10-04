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
        var writer = services.GetRequiredService<ISafeSaveWriter>();
        var picker = services.GetRequiredService<SavePickerViewModel>();
        // Hiding a save only hides its picker row; it still belongs to the collection.
        var saves = picker.Saves.Concat(picker.HiddenSaves).ToArray();
        var entries = bank.GetAll().ToArray();
        var sources = new List<PokedexSource>();
        var openId = sessions.Current?.Document.DocumentId;
        if (sessions.CurrentSession is { } live && openId is not null)
        {
            var save = saves.FirstOrDefault(s => s.DocumentId == openId);
            var label = save is null ? sessions.Current!.Document.DisplayName : Label(save);
            try
            {
                // Snapshot the UI-owned live session before starting background reads.
                sources.Add(new(SourceId(openId), label + " · open session", "save", true,
                    PokedexObservationReader.ReadSave(live, Unsupported(save, live, openId, writer))));
            }
            catch (Exception)
            {
                sources.Add(new(SourceId(openId), label, "save", false, []));
            }
        }
        return Task.Run<IReadOnlyList<PokedexSource>>(async () =>
        {
            foreach (var save in saves.DistinctBy(s => s.DocumentId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (save.DocumentId == openId) continue;
                try
                {
                    var bytes = await access.ReadAsync(save.DocumentId, cancellationToken);
                    using var session = engine.OpenSession(bytes, save.EngineHint, save.Format);
                    sources.Add(new(SourceId(save.DocumentId), Label(save), "save", true,
                        PokedexObservationReader.ReadSave(session, Unsupported(save, session, save.DocumentId, writer))));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    sources.Add(new(SourceId(save.DocumentId), Label(save), "save", false, []));
                }
            }
            var bankObservations = new List<PokedexObservation>();
            var complete = true;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (PokedexObservationReader.ReadBank(entry, bank.GetData(entry.Id)) is { } observation)
                        bankObservations.Add(observation);
                }
                catch (Exception) { complete = false; }
            }
            // An incomplete Bank read must never remove previously reported holdings.
            sources.Add(new("bank", "PKForge Bank", "bank", complete, complete ? bankObservations : []));
            cancellationToken.ThrowIfCancellationRequested();
            return sources;
        }, cancellationToken);
    }

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
