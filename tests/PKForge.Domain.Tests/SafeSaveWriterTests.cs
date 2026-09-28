using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class SafeSaveWriterTests
{
    private static SaveSnapshot Snapshot(byte[] original) => new(
        "PKM Test",
        9,
        original,
        [],
        "test.sav");

    [Fact]
    public async Task InvalidCandidateDoesNotCreateBackupOrWrite()
    {
        var engine = new FakeSaveEngine(valid: false);
        var backup = new FakeBackupService();
        var access = new FakeFileAccess();
        var writer = new SafeSaveWriter(engine, backup, access);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 4, 5, 6 }).AsTask());

        Assert.Equal(0, backup.Calls);
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task ValidCandidateBacksUpBeforeSingleWrite()
    {
        var engine = new FakeSaveEngine(valid: true);
        var backup = new FakeBackupService();
        var access = new FakeFileAccess();
        var writer = new SafeSaveWriter(engine, backup, access);

        var receipt = await writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 4, 5, 6 });

        Assert.Equal(1, backup.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(backup.CreatedOrder, access.WriteOrder - 1);
        Assert.Equal("backup-1", receipt.BackupId);
    }

    [Fact]
    public async Task UnchangedCandidateCreatesNoBackupAndWritesNothing()
    {
        var engine = new FakeSaveEngine(valid: true);
        var backup = new FakeBackupService();
        var access = new FakeFileAccess();
        var writer = new SafeSaveWriter(engine, backup, access);

        var receipt = await writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 1, 2, 3 });

        Assert.False(receipt.Changed);
        Assert.Equal(string.Empty, receipt.BackupId);
        Assert.Equal(0, backup.Calls);
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task ChangeDescriptionFlowsIntoTheBackup()
    {
        var engine = new FakeSaveEngine(valid: true);
        var backup = new FakeBackupService();
        var writer = new SafeSaveWriter(engine, backup, new FakeFileAccess());

        await writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 4, 5, 6 },
            "Edit Garchomp (Box 3, Slot 12)");

        Assert.Equal("Edit Garchomp (Box 3, Slot 12)", backup.LastDescription);
        Assert.Equal("content://save", backup.LastDocumentId);
    }

    [Fact]
    public async Task NewerEmulatorSaveIsNotOverwrittenOrBackedUpAsAnOlderSnapshot()
    {
        var backup = new FakeBackupService();
        var access = new FakeFileAccess { Bytes = [9, 9, 9] };
        var writer = new SafeSaveWriter(new FakeSaveEngine(true), backup, access);

        await Assert.ThrowsAsync<SaveConflictException>(() =>
            writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }).AsTask());

        Assert.Equal(new byte[] { 9, 9, 9 }, access.Bytes);
        Assert.Equal(0, access.Writes);
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task SaveChangedDuringBackupIsNotOverwritten()
    {
        var access = new FakeFileAccess();
        var backup = new FakeBackupService { OnCreate = () => { access.Bytes = [9, 9, 9]; return Task.CompletedTask; } };
        var writer = new SafeSaveWriter(new FakeSaveEngine(true), backup, access);

        await Assert.ThrowsAsync<SaveConflictException>(() =>
            writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }).AsTask());

        Assert.Equal(1, backup.Calls);
        Assert.Equal(0, access.Writes);
        Assert.Equal(new byte[] { 9, 9, 9 }, access.Bytes);
    }

    [Fact]
    public async Task BackupFailureNeverStartsDocumentWrite()
    {
        var access = new FakeFileAccess();
        var backup = new FakeBackupService { OnCreate = () => throw new IOException("Disk full") };
        var writer = new SafeSaveWriter(new FakeSaveEngine(true), backup, access);

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }).AsTask());

        Assert.Equal(0, access.Writes);
        Assert.Equal(new byte[] { 1, 2, 3 }, access.Bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialOrUnverifiedWriteNamesTheRecoveryBackup(bool throws)
    {
        var access = new FakeFileAccess { PartialWrite = true, ThrowOnWrite = throws };
        var backup = new FakeBackupService();
        var writer = new SafeSaveWriter(new FakeSaveEngine(true), backup, access);

        var error = await Assert.ThrowsAsync<SaveWriteFailedException>(() =>
            writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }).AsTask());

        Assert.Equal("backup-1", error.BackupId);
        Assert.Contains("Restore points", error.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, backup.LastBytes);
        Assert.Equal(new byte[] { 4 }, access.Bytes);
    }

    [Fact]
    public async Task OverlappingAppWritesCannotBothCommitFromTheSameBaseline()
    {
        var enteredBackup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBackup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backup = new FakeBackupService
        {
            OnCreate = async () => { enteredBackup.SetResult(); await releaseBackup.Task; },
        };
        var access = new FakeFileAccess();
        var writer = new SafeSaveWriter(new FakeSaveEngine(true), backup, access);
        var first = writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }).AsTask();
        await enteredBackup.Task;
        var second = writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 7, 8, 9 }).AsTask();
        releaseBackup.SetResult();

        Assert.True((await first).Changed);
        await Assert.ThrowsAsync<SaveConflictException>(() => second);
        Assert.Equal(1, access.Writes);
        Assert.Equal(new byte[] { 4, 5, 6 }, access.Bytes);
    }

    [Fact]
    public async Task CancellationAfterWritingStartsDoesNotSkipVerification()
    {
        using var cancellation = new CancellationTokenSource();
        var access = new FakeFileAccess { OnWrite = cancellation.Cancel };
        var writer = new SafeSaveWriter(new FakeSaveEngine(true), new FakeBackupService(), access);

        var receipt = await writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }, cancellationToken: cancellation.Token);

        Assert.True(receipt.Changed);
        Assert.Equal(3, access.Reads);
        Assert.False(access.WriteToken.CanBeCanceled);
    }

    [Theory]
    [InlineData(true, "save", true)]
    [InlineData(false, "save", true)]
    [InlineData(false, "other-save", false)]
    public async Task UnsafeDocumentStateClosesOnlyItsConnectedSession(bool conflict, string connectedId, bool closed)
    {
        var sessions = new FakeSessions(connectedId);
        var access = new FakeFileAccess { PartialWrite = !conflict };
        if (conflict) access.Bytes = [9, 9, 9];
        var writer = new SafeSaveWriter(new FakeSaveEngine(true), new FakeBackupService(), access, sessions: sessions);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }).AsTask());

        Assert.Equal(closed, sessions.Current is null);
    }

    [Fact]
    public async Task ValidationRefusalKeepsConnectedSession()
    {
        var sessions = new FakeSessions("save");
        var writer = new SafeSaveWriter(new FakeSaveEngine(false), new FakeBackupService(), new FakeFileAccess(), sessions: sessions);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync("save", Snapshot([1, 2, 3]), new byte[] { 4, 5, 6 }).AsTask());

        Assert.NotNull(sessions.Current);
    }

    private sealed class FakeSessions(string documentId) : ISaveSessionService
    {
        public SaveSession? Current { get; private set; } = new(new PickedDocument(documentId, "test.sav"), Snapshot([1, 2, 3]));
        public ISaveEngineSession? CurrentSession => null;
        public void Close() => Current = null;
        public void RevertToBaseline() => throw new NotSupportedException();
        public void MarkWritten(string documentId, ReadOnlyMemory<byte> written) => throw new NotSupportedException();
        public ValueTask<SaveSession> OpenAsync(PickedDocument document, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSaveEngine(bool valid) : ISaveEngine
    {
        public SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null) => throw new NotImplementedException();
        public ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null) => throw new NotImplementedException();
        public ReadOnlyMemory<byte> Serialize(SaveSnapshot snapshot) => throw new NotImplementedException();
        public bool Validate(ReadOnlyMemory<byte> bytes) => valid;
        public SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes, string? displayName = null) => throw new NotImplementedException();
        public BankEntryInfo? TryDescribeEntity(byte[] bytes, string sourceName, string? format = null) => throw new NotImplementedException();
        public ISaveEngineSession? OpenEntitySession(byte[] entityBytes, string? displayName = null, string? format = null) => throw new NotImplementedException();
        public ISaveEngineSession OpenBlankSession(int generation, string? displayName = null) => throw new NotImplementedException();
    }

    private sealed class FakeBackupService : IBackupService
    {
        public int Calls { get; private set; }
        public int CreatedOrder { get; private set; } = -1;
        public string? LastDescription { get; private set; }
        public string? LastDocumentId { get; private set; }
        public byte[]? LastBytes { get; private set; }
        public Func<Task>? OnCreate { get; init; }

        public async ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDescription = changeDescription;
            LastBytes = source.OriginalBytes.ToArray();
            if (OnCreate is not null) await OnCreate();
            CreatedOrder = Order++;
            return new BackupReceipt($"backup-{Calls}", DateTimeOffset.UtcNow, "sha");
        }

        public ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription, CancellationToken cancellationToken, string? documentId)
        {
            LastDocumentId = documentId;
            return CreateAsync(source, changeDescription, cancellationToken);
        }

        public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeFileAccess : ISaveFileAccess
    {
        public byte[] Bytes { get; set; } = [1, 2, 3];
        public bool PartialWrite { get; init; }
        public bool ThrowOnWrite { get; init; }
        public Action? OnWrite { get; init; }
        public CancellationToken WriteToken { get; private set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int WriteOrder { get; private set; } = -1;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Bytes.ToArray());
        }
        public ValueTask WriteAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Writes++;
            WriteOrder = Order++;
            WriteToken = cancellationToken;
            Bytes = PartialWrite ? bytes[..1].ToArray() : bytes.ToArray();
            OnWrite?.Invoke();
            if (ThrowOnWrite) throw new IOException("Provider failed mid-write");
            return ValueTask.CompletedTask;
        }
    }

    private static int Order;
}
