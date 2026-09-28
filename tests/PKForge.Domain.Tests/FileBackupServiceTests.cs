using PKForge.Domain;
using PKForge.Infrastructure;
using System.Security.Cryptography;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class FileBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    private static SaveSnapshot Snapshot(byte[] bytes, string name) => new("PKM Test", 7, bytes, [], name);

    [Fact]
    public async Task CreateListReadRoundTrips()
    {
        var service = new FileBackupService(_root);
        var bytes = new byte[] { 1, 2, 3, 4 };

        var receipt = await service.CreateAsync(Snapshot(bytes, "save.srm"), null, default, "document-a");
        var listed = await service.ListAsync();
        var read = await service.ReadAsync(receipt.BackupId);

        var info = Assert.Single(listed);
        Assert.Equal(receipt.BackupId, info.BackupId);
        Assert.Equal("save.srm", info.DisplayName);
        Assert.Equal(receipt.Sha256, info.Sha256);
        Assert.Equal("document-a", info.DocumentId);
        Assert.Equal(bytes, read.ToArray());
    }

    [Fact]
    public async Task ChangeDescriptionPersistsInTheSidecar()
    {
        var service = new FileBackupService(_root);

        var receipt = await service.CreateAsync(Snapshot([1, 2, 3], "save.sav"), "Deposited Pikachu in the Bank");
        var info = Assert.Single(await service.ListAsync());

        Assert.Equal(receipt.BackupId, info.BackupId);
        Assert.Equal("Deposited Pikachu in the Bank", info.ChangeDescription);
    }

    [Fact]
    public async Task ReadDetectsCorruptBytes()
    {
        var service = new FileBackupService(_root);
        var receipt = await service.CreateAsync(Snapshot([1, 2, 3], "save.sav"));

        await File.WriteAllBytesAsync(Path.Combine(_root, receipt.BackupId + ".bin"), [9, 9, 9]);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(receipt.BackupId).AsTask());
    }

    [Fact]
    public async Task PrunesOldestBeyondMaxVersionsPerDocument()
    {
        var service = new FileBackupService(_root, maxVersions: 2);
        var firstA = await service.CreateAsync(Snapshot([1], "a"), null, default, "document-a");
        await service.CreateAsync(Snapshot([2], "a"), null, default, "document-a");
        await service.CreateAsync(Snapshot([3], "b"), null, default, "document-b");
        await service.CreateAsync(Snapshot([4], "b"), null, default, "document-b");
        await service.CreateAsync(Snapshot([5], "a"), null, default, "document-a");

        var listed = await service.ListAsync();
        Assert.Equal(4, listed.Count);
        Assert.DoesNotContain(listed, x => x.BackupId == firstA.BackupId);
        Assert.Equal(2, listed.Count(x => x.DocumentId == "document-a"));
        Assert.Equal(2, listed.Count(x => x.DocumentId == "document-b"));
    }

    [Fact]
    public async Task NewDocumentBackupsDoNotPruneLegacyBackups()
    {
        var service = new FileBackupService(_root, maxVersions: 1);
        var legacy = await service.CreateAsync(Snapshot([1], "old.sav"));
        await service.CreateAsync(Snapshot([2], "new.sav"), null, default, "document-a");
        await service.CreateAsync(Snapshot([3], "new.sav"), null, default, "document-a");

        var listed = await service.ListAsync();
        var legacyInfo = Assert.Single(listed, x => x.BackupId == legacy.BackupId);
        Assert.True(legacyInfo.IsLegacy);
        Assert.Equal(new byte[] { 1 }, (await service.ReadAsync(legacy.BackupId)).ToArray());
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task SidecarFromBeforeDocumentTrackingRemainsReadable()
    {
        Directory.CreateDirectory(_root);
        const string id = "20250101T000000000Z-legacy";
        var bytes = new byte[] { 7, 8, 9 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(_root, id + ".bin"), bytes);
        await File.WriteAllTextAsync(Path.Combine(_root, id + ".json"), $$"""
            {
              "BackupId": "{{id}}",
              "CreatedUtc": "2025-01-01T00:00:00+00:00",
              "Sha256": "{{sha}}",
              "DisplayName": "legacy.sav",
              "Format": "PK7",
              "Generation": 7,
              "SizeBytes": 3
            }
            """);

        var service = new FileBackupService(_root, maxVersions: 1);
        var info = Assert.Single(await service.ListAsync());

        Assert.True(info.IsLegacy);
        Assert.Null(info.DocumentId);
        Assert.Equal(bytes, (await service.ReadAsync(id)).ToArray());
    }

    [Fact]
    public void BackupCompatibilityRequiresTheSameFormatAndGeneration()
    {
        var backup = new BackupInfo("id", DateTimeOffset.UtcNow, "hash", "source.sav", "PK8", 8, 4,
            DocumentId: "document-a");

        Assert.True(backup.IsForDocument("document-a"));
        Assert.False(backup.IsForDocument("document-b"));
        Assert.True(backup.HasCompatibleFormat(new SaveSnapshot("pk8", 8, new byte[4], [], "target.sav")));
        Assert.False(backup.HasCompatibleFormat(new SaveSnapshot("PK9", 9, new byte[4], [], "target.sav")));
    }

    [Fact]
    public void RestoreTargetRulesRejectWrongFilesAndAllowIdentifiedRecovery()
    {
        var target = new SaveSnapshot("PK8", 8, new byte[4], [], "target.sav");
        var identified = new BackupInfo("id", DateTimeOffset.UtcNow, "hash", "source.sav", "PK8", 8, 4,
            DocumentId: "document-a");
        var legacy = identified with { DocumentId = null };

        Assert.Equal(BackupRestoreMatch.Allowed, identified.MatchRestoreTarget(null, null));
        Assert.Equal(BackupRestoreMatch.LegacyNeedsOpenDocument, legacy.MatchRestoreTarget(null, null));
        Assert.Equal(BackupRestoreMatch.DifferentDocument, identified.MatchRestoreTarget("document-b", target));
        Assert.Equal(BackupRestoreMatch.Allowed, legacy.MatchRestoreTarget("document-b", target));
        Assert.Equal(BackupRestoreMatch.IncompatibleFormat,
            identified.MatchRestoreTarget("document-a", target with { Format = "PK9", Generation = 9 }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
