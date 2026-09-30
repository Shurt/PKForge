using PKForge.App.Services;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;

namespace PKForge.App.Views;

/// <summary>Uploads a separate Checkpoint backup. Restoration stays on the console.</summary>
internal static class CheckpointTransferPage
{
    private static bool _open;

    private static CheckpointConnectionService Connection =>
        IPlatformApplication.Current!.Services.GetRequiredService<CheckpointConnectionService>();

    public static async Task ShowSettingsAsync(Grid host)
    {
        while (true)
        {
            var service = Connection;
            var choice = await PadMenu.ShowAsync(host, "3DS connection",
                $"3DS address: {(service.Address.Length == 0 ? "Not set" : service.Address)}\n"
                + "Used by Save data > Send to 3DS. The address is remembered; enter Checkpoint's receive PIN for each transfer.",
                "Edit 3DS address", "Forget address", "Done");
            if (choice is null or "Done") return;
            if (choice == "Forget address") service.SetAddress("");
            else if (choice == "Edit 3DS address") await EditAddressAsync(host, service);
        }
    }

    private static async Task<bool> EditAddressAsync(Grid host, CheckpointConnectionService service)
    {
        var current = service.Address;
        while (true)
        {
            var address = await TextPopup.ShowLineAsync(host, "3DS IP address",
                "IPv4 address shown by Checkpoint, e.g. 192.168.1.42", current);
            if (address is null) return false;
            current = address;
            try
            {
                service.SetAddress(address);
                return service.Address.Length != 0;
            }
            catch (ArgumentException error)
            {
                await PadMenu.ShowAsync(host, "Invalid 3DS address", error.Message, "Back");
            }
        }
    }

    public static async Task SendAsync(Grid host, ISaveEngineSession session)
    {
        if (_open) return;
        _open = true;
        try
        {
            // Freeze and validate the current save before any network request. This export
            // never writes the source document or uses an old Checkpoint backup as a baseline.
            var save = CheckpointSaveExport.Prepare(session);
            var service = Connection;
            if (service.Address.Length == 0 && !await EditAddressAsync(host, service)) return;

            while (true)
            {
                var choice = await PadMenu.ShowAsync(host, "Send to 3DS",
                    $"{save.TitleName} → {service.Address}\n"
                    + "On the 3DS: close the game, back up its current save in Checkpoint, then open Receive for this game.\n"
                    + "Both devices must be on the same reachable network. This sends the currently open save as a new backup. Restore it in Checkpoint afterward.",
                    "Send backup", "Change 3DS address", "Cancel");
                if (choice is null or "Cancel") return;
                if (choice == "Change 3DS address")
                {
                    await EditAddressAsync(host, service);
                    if (service.Address.Length == 0) return;
                    continue;
                }

                var pin = await TextPopup.ShowLineAsync(host, "Checkpoint receive PIN",
                    "Enter the four digits shown on the 3DS");
                if (pin is null) return;
                if (!CheckpointTransferClient.IsValidPin(pin))
                {
                    await PadMenu.ShowAsync(host, "Invalid PIN", "Enter exactly four digits, including any leading zeroes.", "Back");
                    continue;
                }

                var loader = LoadingOverlay.Show(host, "Sending to 3DS", save.TitleName);
                loader.Report("Connecting and sending backup…", 0);
                string title;
                string message;
                try
                {
                    var result = await service.SendAsync(service.Address, pin, save.Data,
                        save.TitleId, save.TitleName, loader.Cancellation.Token);
                    title = "Backup received";
                    message = $"Checkpoint received {result.BackupName}.\n\n"
                        + $"On the 3DS, select this backup under {save.TitleName} and choose Restore. "
                        + "Keep the backup you made of the previous save. The game's active save has not been replaced by this transfer.";
                }
                catch (OperationCanceledException)
                {
                    title = "Transfer cancelled";
                    message = "Check Checkpoint's backup list before retrying; the upload may have reached the console. "
                        + "No restore was requested.";
                }
                catch (Exception error)
                {
                    title = "Transfer not confirmed";
                    message = error.Message + "\n\nCheck Checkpoint's Receive screen and backup list before retrying. "
                        + "No restore was requested.";
                }
                finally { loader.Close(); }
                await PadMenu.ShowAsync(host, title, message, "Done");
                return;
            }
        }
        catch (Exception error)
        {
            await PadMenu.ShowAsync(host, "Cannot send save", error.Message, "Back");
        }
        finally { _open = false; }
    }
}
