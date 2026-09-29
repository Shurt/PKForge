using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>All integration entry points are explicit actions in this optional settings page.</summary>
internal static class PokedexConnectionPage
{
    public static async Task ShowAsync(Grid host)
    {
        var service = IPlatformApplication.Current!.Services.GetRequiredService<PokedexConnectionService>();
        while (true)
        {
            var state = service.State;
            var options = new List<PadOption>();
            if (!service.StorageError)
            {
                options.Add(new(state.Enabled ? "Disable connection" : "Enable connection"));
                options.Add(new("Server URL"));
                if (state.Enabled) options.Add(new(state.Pending is null ? "Sync now" : "Retry pending sync"));
                if (state.Cache is not null)
                {
                    options.Add(new("Cached HOME checklist"));
                    options.Add(new("Cached transfer plan"));
                    options.Add(new("Unmatched observations"));
                }
            }
            options.Add(new("Reset local connection"));
            options.Add(new("Done"));
            var message = $"{(state.Enabled ? "Enabled" : "Disabled")} · {state.ServerUrl}\n"
                + "Sends collection metadata only. HOME ownership and transfer confirmations stay under your control.\n"
                + (state.LastSyncedAt is { } last ? $"Last synced: {last.ToLocalTime():g}. Cached details may be out of date."
                    : "No successful sync yet.");
            if (state.Pending is { } pending)
                message += $"\nPending snapshot from {pending.ObservedAt.ToLocalTime():g}. Retry sends this same snapshot; sync again afterward for a fresh scan.";
            if (!string.IsNullOrWhiteSpace(state.LastError)) message += "\n" + state.LastError;
            var choice = await PadMenu.ShowAsync(host, "Pokedex connection", message, options.ToArray());
            if (choice is null or "Done") return;
            try
            {
                switch (choice)
                {
                    case "Enable connection": service.SetEnabled(true); break;
                    case "Disable connection": service.SetEnabled(false); break;
                    case "Server URL":
                        var url = await TextPopup.ShowLineAsync(host, "Pokedex server", "HTTPS URL reachable over LAN or Tailscale", state.ServerUrl);
                        if (!string.IsNullOrWhiteSpace(url) && url != state.ServerUrl
                            && await PadMenu.ConfirmAsync(host, "Change server?",
                                "This clears the local cache and pending sync, and starts a new connection identity. Remote observations remain unchanged.", "Change server"))
                            service.SetServerUrl(url);
                        break;
                    case "Reset local connection":
                        if (await PadMenu.ConfirmAsync(host, "Reset local connection?",
                            "Clear this connection's local cache and pending sync, then disable it. Existing observations in pokedex are retained with their last reported timestamps.", "Reset connection"))
                            service.Reset();
                        break;
                    case "Sync now":
                    case "Retry pending sync":
                        await SyncAsync(host, service);
                        break;
                    case "Cached HOME checklist": await ShowChecklistAsync(host, state.Cache!); break;
                    case "Cached transfer plan": await ShowPlanAsync(host, state.Cache!); break;
                    case "Unmatched observations":
                        await BrowseAsync(host, "Unmatched observations", state.Cache!.Mapping
                            .Where(m => m.MappingStatus != "matched")
                            .Select(m => (m.SourceLabel ?? "Collection observation",
                                $"{m.MappingReason ?? "No verified catalog match."}\nCheck the PKForge tab in pokedex for this copy's location.")).ToList());
                        break;
                }
            }
            catch (Exception error)
            {
                await PadMenu.ShowAsync(host, "Pokedex connection", error.Message, "Back");
            }
        }
    }

    private static async Task SyncAsync(Grid host, PokedexConnectionService service)
    {
        var loader = LoadingOverlay.Show(host, "Syncing pokedex…", "Reading local holdings and exchanging collection metadata.");
        string message;
        try
        {
            await service.SyncNowAsync(loader.Cancellation.Token);
            var unresolved = service.State.Cache!.Mapping.Count(m => m.MappingStatus != "matched");
            message = $"Sync complete. {unresolved} observations need a catalog match.\n"
                + "Unreadable and omitted saves retain their previous observations. Check the PKForge tab in pokedex for source timestamps.\n"
                + "Your HOME checklist and transfer confirmations were not changed.";
        }
        catch (Exception error)
        {
            message = service.State.LastError ?? error.Message;
        }
        finally { loader.Close(); }
        await PadMenu.ShowAsync(host, "Pokedex sync", message, "Done");
    }

    private static async Task ShowChecklistAsync(Grid host, PokedexSyncResponse cache)
    {
        var mode = await PadMenu.ShowAsync(host, "Cached HOME checklist", "Read-only copy from your last sync.",
            "Missing normal", "Missing shiny", "Entries with notes", "Search", "Back");
        if (mode is null or "Back") return;
        var entries = cache.Checklist.AsEnumerable();
        if (mode == "Missing normal") entries = entries.Where(e => !e.NormalOwned);
        if (mode == "Missing shiny") entries = entries.Where(e => !e.ShinyOwned);
        if (mode == "Entries with notes") entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Notes));
        if (mode == "Search")
        {
            var query = await TextPopup.ShowLineAsync(host, "Search checklist", "Species, form or entry ID");
            if (query is null) return;
            entries = entries.Where(e => e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.DexEntryId.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        await BrowseAsync(host, "Cached HOME checklist", entries.Select(e => (e.DisplayName,
            $"{e.DexEntryId}\nNormal: {(e.NormalOwned ? "owned" : "missing")} · Shiny: {(e.ShinyOwned ? "owned" : "missing")}\n\n{e.Notes}")).ToList());
    }

    private static Task ShowPlanAsync(Grid host, PokedexSyncResponse cache) =>
        BrowseAsync(host, "Cached transfer plan", cache.BankPlan.Targets.Select(t => (t.Title,
            $"{t.Source}\n{t.GameProgress}\n\nAcquired: {Yes(t.Progress.Acquired)}\nArchived: {Yes(t.Progress.Archived)}\n"
            + $"Official Pokémon Bank: {Yes(t.Progress.InBank)}\nHOME verified: {Yes(t.Progress.HomeVerified)}\n\n{t.Progress.Notes}")).ToList());

    private static string Yes(bool value) => value ? "yes" : "no";

    private static async Task BrowseAsync(Grid host, string title, IReadOnlyList<(string Label, string Details)> rows)
    {
        const int pageSize = 6;
        var page = 0;
        while (true)
        {
            var visible = rows.Skip(page * pageSize).Take(pageSize).ToArray();
            var options = visible.Select((r, i) => new PadOption($"{page * pageSize + i + 1}. {r.Label}")).ToList();
            if (page > 0) options.Add(new("Previous page"));
            if ((page + 1) * pageSize < rows.Count) options.Add(new("Next page"));
            options.Add(new("Back"));
            var choice = await PadMenu.ShowAsync(host, title, $"{rows.Count} entries · cached at last sync", options.ToArray());
            if (choice is null or "Back") return;
            if (choice == "Previous page") { page--; continue; }
            if (choice == "Next page") { page++; continue; }
            var index = options.FindIndex(o => o.Label == choice);
            if (index >= 0 && index < visible.Length)
                await PadMenu.ShowAsync(host, visible[index].Label, visible[index].Details, "Back");
        }
    }
}
