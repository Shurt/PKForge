using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>A paged, read-only list of stored copies. Uses the shared touch/gamepad
/// menus so long collections and long save descriptions remain navigable.</summary>
internal static class CollectionLocationsPage
{
    public static async Task ShowAsync(Grid host, string name, IReadOnlyList<CollectionLocation> copies,
        IReadOnlyList<string> unreadableSources)
    {
        const int pageSize = 6;
        var page = 0;
        var pages = Math.Max(1, (copies.Count + pageSize - 1) / pageSize);
        while (true)
        {
            var options = new List<PadOption>();
            var visible = copies.Skip(page * pageSize).Take(pageSize).ToArray();
            for (var i = 0; i < visible.Length; i++)
            {
                var copy = visible[i];
                options.Add(new PadOption($"{page * pageSize + i + 1}. {copy.SourceLabel} · {copy.PositionLabel}",
                    Detail: Describe(copy)));
            }
            if (page > 0) options.Add(new("Previous page"));
            if (page + 1 < pages) options.Add(new("Next page"));
            if (unreadableSources.Count > 0) options.Add(new("Unreadable saves"));
            options.Add(new("Done"));
            var message = copies.Count == 0 ? "No matching copies found in the scanned collection."
                : $"{copies.Count} matching copies · page {page + 1}/{pages}";
            message += "\nCurrent storage locations in the Bank and visible shelf saves. Open-session results include unsaved edits.";
            if (unreadableSources.Count > 0)
                message += $"\nScan incomplete: {unreadableSources.Count} saves could not be read.";
            var choice = await PadMenu.ShowAsync(host, $"Find copies · {name}", message, options.ToArray());
            if (choice is null or "Done") return;
            if (choice == "Previous page") { page--; continue; }
            if (choice == "Next page") { page++; continue; }
            if (choice == "Unreadable saves")
            {
                await PadMenu.ShowAsync(host, "Unreadable saves",
                    string.Join("\n\n", unreadableSources) + "\n\nCheck access to these files and try Find copies again.", "Back");
                continue;
            }
            var index = options.FindIndex(option => option.Label == choice);
            if (index < 0 || index >= visible.Length) continue;
            var selected = visible[index];
            await PadMenu.ShowAsync(host, selected.SourceLabel,
                $"{selected.PositionLabel}\n{Describe(selected)}\n\n{selected.SourceDetail}", "Back");
        }
    }

    private static string Describe(CollectionLocation copy)
    {
        var form = LivingDexCatalogBuilder.FormName(copy.Species, copy.Form);
        if (form.Length == 0) form = "Base form";
        return string.Join(" · ", new[] { copy.Nickname, form, copy.Shiny ? "Shiny" : "Not shiny" }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
