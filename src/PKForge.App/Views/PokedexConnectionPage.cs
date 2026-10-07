using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>All integration entry points are explicit actions in this optional settings page.</summary>
internal static class PokedexConnectionPage
{
    public static async Task ShowAsync(Grid host, Func<string, PokedexObservation, Task<bool>>? openCopy = null)
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
                    options.Add(new("Missing in HOME, available locally"));
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
                    case "Missing in HOME, available locally":
                        if (await ShowMissingLocalAsync(host, state, openCopy)) return;
                        break;
                    case "Cached transfer plan":
                        if (await ShowPlanAsync(host, state.Cache!, openCopy)) return;
                        break;
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

    private static async Task<bool> ShowMissingLocalAsync(Grid host, PokedexConnectionState state,
        Func<string, PokedexObservation, Task<bool>>? openCopy)
    {
        if (state.InventorySources.Count == 0)
        {
            await PadMenu.ShowAsync(host, "Missing in HOME, available locally",
                "Sync once to cache the local inventory used by this queue.", "Back");
            return false;
        }
        var rows = PokedexCollectionQueue.FindMissing(state.Cache!.Checklist, state.InventorySources);
        if (rows.Count == 0)
        {
            await PadMenu.ShowAsync(host, "Missing in HOME, available locally",
                "No missing normal or shiny HOME entries have a matching copy in the cached local inventory.", "Back");
            return false;
        }
        while (true)
        {
            var choice = await PadMenu.ShowAsync(host, "Missing in HOME, available locally",
                $"{rows.Count} missing entries have local copies. Choose a filter, then work through the copies save-by-save.",
                $"All missing ({rows.Count})", $"Normal ({rows.Count(row => !row.Shiny)})", $"Shiny ({rows.Count(row => row.Shiny)})", "Back");
            if (choice is null or "Back") return false;
            var filtered = choice.StartsWith("Normal", StringComparison.Ordinal) ? rows.Where(row => !row.Shiny)
                : choice.StartsWith("Shiny", StringComparison.Ordinal) ? rows.Where(row => row.Shiny)
                : rows;
            var copies = filtered.SelectMany(row => row.Copies.Select(copy => new CopyRow(
                copy.Source.SourceId,
                copy.Source.Label,
                copy.Entry.Observation,
                SourceFreshness(copy.Source, state.Cache.ReceivedAt),
                $"{row.DisplayName} · {(row.Shiny ? "shiny" : "normal")}"))).ToArray();
            if (await ShowCopyRowsAsync(host, "Missing in HOME, available locally", copies,
                "Cached locations from the last sync.", openCopy)) return true;
        }
    }

    private static async Task<bool> ShowPlanAsync(Grid host, PokedexSyncResponse cache,
        Func<string, PokedexObservation, Task<bool>>? openCopy)
    {
        const int pageSize = 6;
        var page = 0;
        var targets = cache.BankPlan.Targets;
        while (true)
        {
            var visible = targets.Skip(page * pageSize).Take(pageSize).ToArray();
            var options = visible.Select((target, index) =>
                new PadOption($"{page * pageSize + index + 1}. {target.Title}")).ToList();
            AddPaging(options, page, targets.Count, pageSize);
            var choice = await PadMenu.ShowAsync(host, "Cached transfer plan",
                $"{targets.Count} targets · read-only copy from the last sync", options.ToArray());
            if (choice is null or "Back") return false;
            if (MovePage(choice, ref page)) continue;
            var index = options.FindIndex(option => option.Label == choice);
            if (index >= 0 && index < visible.Length && await ShowPlanTargetAsync(host, visible[index], openCopy))
                return true;
        }
    }

    private static async Task<bool> ShowPlanTargetAsync(Grid host, PokedexBankPlanTarget target,
        Func<string, PokedexObservation, Task<bool>>? openCopy)
    {
        while (true)
        {
            var options = new List<PadOption> { new("Progress and notes") };
            if (target.Preparations is { Count: > 0 }) options.Add(new("Preparation steps"));
            if (target.Match is not null) options.Add(new("Find local copies"));
            if (target.WonderCards is { Count: > 0 }) options.Add(new("Referenced Wonder Cards"));
            options.Add(new("Back"));
            var matchNotes = string.IsNullOrWhiteSpace(target.Match?.Notes) ? "" : $"\n\n{target.Match.Notes}";
            var choice = await PadMenu.ShowAsync(host, target.Title,
                $"{target.Source}\n{target.GameProgress}{matchNotes}\n\nCached plan. Progress is read-only on this device.", options.ToArray());
            switch (choice)
            {
                case null or "Back": return false;
                case "Progress and notes":
                    await PadMenu.ShowAsync(host, target.Title,
                        $"Acquired: {Yes(target.Progress.Acquired)}\nArchived: {Yes(target.Progress.Archived)}\n"
                        + $"Official Pokémon Bank: {Yes(target.Progress.InBank)}\nHOME verified: {Yes(target.Progress.HomeVerified)}"
                        + (string.IsNullOrWhiteSpace(target.Progress.Notes) ? "" : $"\n\n{target.Progress.Notes}"), "Back");
                    break;
                case "Preparation steps": await ShowPreparationsAsync(host, target); break;
                case "Referenced Wonder Cards": await ShowWonderCardsAsync(host, target); break;
                case "Find local copies":
                    if (await FindPlanCopiesAsync(host, target, openCopy)) return true;
                    break;
            }
        }
    }

    private static Task ShowPreparationsAsync(Grid host, PokedexBankPlanTarget target) =>
        BrowseAsync(host, $"{target.Title} · preparation", target.Preparations!.Select(step =>
        {
            var complete = target.Progress.Preparation?.GetValueOrDefault(step.Id) == true;
            return ($"{(complete ? "✓" : "○")} {step.Label}", step.Detail ?? "No additional instructions.");
        }).ToList());

    private static async Task ShowWonderCardsAsync(Grid host, PokedexBankPlanTarget target)
    {
        const int pageSize = 5;
        var cards = target.WonderCards!;
        var page = 0;
        while (true)
        {
            var options = new List<PadOption> { new("Open target gift search") };
            var visible = cards.Skip(page * pageSize).Take(pageSize).ToArray();
            options.AddRange(visible.Select((card, index) => new PadOption($"{page * pageSize + index + 1}. {WonderCardLabel(card)}")));
            AddPaging(options, page, cards.Count, pageSize);
            var choice = await PadMenu.ShowAsync(host, $"{target.Title} · Wonder Cards",
                "References include Pokémon gifts and any item or prerequisite gifts in the plan.", options.ToArray());
            if (choice is null or "Back") return;
            if (MovePage(choice, ref page)) continue;
            if (choice == "Open target gift search")
            {
                var services = IPlatformApplication.Current?.Services;
                var session = services?.GetService<ISaveSessionService>()?.CurrentSession;
                var viewModel = services?.GetService<BoxBrowserViewModel>();
                if (session is null || viewModel is null)
                {
                    await PadMenu.ShowAsync(host, target.Title,
                        "Open the receiving game first, then use this shortcut again.", "Back");
                    continue;
                }
                await EventGallery.ShowAsync(host, viewModel, session, null, () => { }, target);
                continue;
            }
            var index = options.FindIndex(option => option.Label == choice) - 1;
            if (index >= 0 && index < visible.Length)
            {
                var card = visible[index];
                var details = $"Card {card.CardId} · {card.Games}\n{card.SourcePath}"
                    + (string.IsNullOrWhiteSpace(card.Kind) ? "" : $"\nKind: {card.Kind}")
                    + (string.IsNullOrWhiteSpace(card.Language) ? "" : $"\nLanguage: {card.Language}")
                    + (string.IsNullOrWhiteSpace(card.SearchTerm) ? "" : $"\nSearch: {card.SearchTerm}")
                    + (string.IsNullOrWhiteSpace(card.Notes) ? "" : $"\n\n{card.Notes}")
                    + (string.IsNullOrWhiteSpace(card.SourceUrl) ? "" : $"\n\nReference: {card.SourceUrl}");
                await PadMenu.ShowAsync(host, WonderCardLabel(card), details, "Back");
            }
        }
    }

    private static string WonderCardLabel(PokedexWonderCardReference card) =>
        !string.IsNullOrWhiteSpace(card.Title) ? card.Title : $"Card {card.CardId} · {card.Games}";

    private static async Task<bool> FindPlanCopiesAsync(Grid host, PokedexBankPlanTarget target,
        Func<string, PokedexObservation, Task<bool>>? openCopy)
    {
        var loading = LoadingOverlay.Show(host, "Checking local copies…", "Scanning every save and the PKForge Bank again.");
        IReadOnlyList<PokedexSource> sources;
        try { sources = await PokedexCollectionScanner.ScanAsync(loading.Cancellation.Token); }
        catch (OperationCanceledException) { return false; }
        finally { loading.Close(); }
        var copies = PokedexBankPlanCandidates.Find(target, sources);
        var incomplete = sources.Where(source => !source.Complete).Select(source => source.Label).ToArray();
        var warning = incomplete.Length == 0 ? "Fresh scan complete."
            : $"Fresh scan incomplete for {string.Join(", ", incomplete)}. Those sources were excluded.";
        if (copies.Count == 0)
        {
            await PadMenu.ShowAsync(host, target.Title, $"No matching local copies were found in the fresh scan.\n\n{warning}", "Back");
            return false;
        }
        var rows = copies.Select(copy => new CopyRow(copy.Source.SourceId, copy.Source.Label, copy.Observation,
            "Observed in the fresh scan.", null)).ToArray();
        return await ShowCopyRowsAsync(host, target.Title, rows, warning, openCopy);
    }

    private static async Task<bool> ShowCopyRowsAsync(Grid host, string title, IReadOnlyList<CopyRow> copies, string note,
        Func<string, PokedexObservation, Task<bool>>? openCopy)
    {
        const int pageSize = 6;
        var groups = copies.GroupBy(copy => copy.SourceId)
            .OrderBy(group => group.First().SourceLabel, StringComparer.OrdinalIgnoreCase).ToArray();
        var page = 0;
        while (true)
        {
            var visible = groups.Skip(page * pageSize).Take(pageSize).ToArray();
            var options = visible.Select((group, index) =>
                new PadOption($"{page * pageSize + index + 1}. {group.First().SourceLabel} ({group.Count()})")).ToList();
            AddPaging(options, page, groups.Length, pageSize);
            var choice = await PadMenu.ShowAsync(host, title,
                $"{copies.Count} matching local copies in {groups.Length} sources.\n{note}", options.ToArray());
            if (choice is null or "Back") return false;
            if (MovePage(choice, ref page)) continue;
            var groupIndex = options.FindIndex(option => option.Label == choice);
            if (groupIndex >= 0 && groupIndex < visible.Length
                && await ShowSourceCopiesAsync(host, visible[groupIndex].First().SourceLabel, visible[groupIndex].ToArray(), openCopy))
                return true;
        }
    }

    private static async Task<bool> ShowSourceCopiesAsync(Grid host, string sourceLabel,
        IReadOnlyList<CopyRow> copies, Func<string, PokedexObservation, Task<bool>>? openCopy)
    {
        const int pageSize = 6;
        copies = copies.OrderBy(copy => copy.Observation.Box)
            .ThenBy(copy => copy.Observation.Slot)
            .ThenBy(copy => copy.Subject, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var page = 0;
        while (true)
        {
            var visible = copies.Skip(page * pageSize).Take(pageSize).ToArray();
            var options = visible.Select((copy, index) => new PadOption(
                $"{page * pageSize + index + 1}. {(copy.Subject is null ? "" : copy.Subject + " · ")}{Location(copy.Observation)}")).ToList();
            AddPaging(options, page, copies.Count, pageSize);
            var choice = await PadMenu.ShowAsync(host, sourceLabel, copies[0].Freshness, options.ToArray());
            if (choice is null or "Back") return false;
            if (MovePage(choice, ref page)) continue;
            var index = options.FindIndex(option => option.Label == choice);
            if (index < 0 || index >= visible.Length) continue;
            var copy = visible[index];
            var observation = copy.Observation;
            var actions = openCopy is null ? new[] { new PadOption("Back") } : [new PadOption("Open copy"), new PadOption("Back")];
            var action = await PadMenu.ShowAsync(host, Location(observation),
                $"{sourceLabel}\n{copy.Freshness}\nFormat: {observation.Format}"
                + (string.IsNullOrWhiteSpace(observation.Nickname) ? "" : $"\nNickname: {observation.Nickname}"), actions);
            if (action == "Open copy" && openCopy is not null && await openCopy(copy.SourceId, observation)) return true;
        }
    }

    private sealed record CopyRow(
        string SourceId,
        string SourceLabel,
        PokedexObservation Observation,
        string Freshness,
        string? Subject);

    private static string Location(PokedexObservation observation) => observation.Box < 0
        ? $"Party position {observation.Slot + 1}"
        : $"Box {observation.Box + 1}, slot {observation.Slot + 1}";

    private static string SourceFreshness(PokedexInventorySource source, DateTimeOffset latestReceivedAt)
    {
        var observed = $"Last observed {source.ObservedAt.ToLocalTime():g}.";
        if (!source.LastAttemptComplete)
            return $"{observed} The scan at {source.LastAttemptAt.ToLocalTime():g} was incomplete, so this source retains its earlier observations.";
        return source.ReceivedAt < latestReceivedAt
            ? $"{observed} Retained from an earlier sync."
            : observed;
    }

    private static void AddPaging(List<PadOption> options, int page, int count, int pageSize)
    {
        if (page > 0) options.Add(new("Previous page"));
        if ((page + 1) * pageSize < count) options.Add(new("Next page"));
        options.Add(new("Back"));
    }

    private static bool MovePage(string choice, ref int page)
    {
        if (choice == "Previous page") { page--; return true; }
        if (choice == "Next page") { page++; return true; }
        return false;
    }

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
