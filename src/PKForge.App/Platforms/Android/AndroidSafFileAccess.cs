using Android.Content;
using Android.Net;
using Android.OS;
using PKForge.Domain;

namespace PKForge.App;

/// <summary>Reads and writes save documents exclusively through Android's Storage Access Framework.</summary>
public sealed class AndroidSafFileAccess : ISaveFileAccess
{
    private static ContentResolver Resolver => Platform.AppContext.ContentResolver
        ?? throw new InvalidOperationException("Android ContentResolver is unavailable.");

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var uri = Parse(documentId);
        await using var input = Resolver.OpenInputStream(uri)
            ?? throw new IOException($"The document provider could not open {uri} for reading.");
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public ValueTask WriteAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var uri = Parse(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        // SAF cannot portably rename-and-swap while preserving the document URI. Open
        // without truncating, and reject non-seekable providers before changing bytes.
        // SafeSaveWriter has already persisted a restore point and verifies this write.
        using var descriptor = Resolver.OpenFileDescriptor(uri, "rw")
            ?? throw new IOException($"The document provider could not open {uri} for writing.");
        var fileDescriptor = descriptor.FileDescriptor
            ?? throw new IOException("The document provider returned no file descriptor.");
        Android.Systems.Os.Lseek(fileDescriptor, 0, Android.Systems.OsConstants.SeekSet);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new ParcelFileDescriptor.AutoCloseOutputStream(descriptor);
        output.Write(bytes.ToArray());
        output.Flush();
        // Do not honor cancellation after modifying the document. Finish the replacement
        // and flush the descriptor; the caller will read it back before accepting it.
        Android.Systems.Os.Ftruncate(fileDescriptor, bytes.Length);
        Android.Systems.Os.Fsync(fileDescriptor);
        return ValueTask.CompletedTask;
    }

    public static void PersistPermission(Android.Net.Uri uri, Intent intent)
    {
        var grants = intent.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        if (grants == 0)
            throw new InvalidOperationException("The picker result did not grant document access.");
        Resolver.TakePersistableUriPermission(uri, grants);
    }

    private static Android.Net.Uri Parse(string documentId) => Android.Net.Uri.Parse(documentId)
        ?? throw new ArgumentException("The document identifier is not a valid content URI.", nameof(documentId));
}
