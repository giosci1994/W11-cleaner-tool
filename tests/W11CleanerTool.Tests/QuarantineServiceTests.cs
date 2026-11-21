using System.Security.Cryptography;
using System.Text;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class QuarantineServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-quar-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MoveToQuarantineAndRestore_PreservesHashAndContent()
    {
        string workDir = Path.Combine(_tempDir, "work");
        string quarantineDir = Path.Combine(_tempDir, "quarantine");
        Directory.CreateDirectory(workDir);

        string originalFile = Path.Combine(workDir, "documento.txt");
        byte[] content = Encoding.UTF8.GetBytes("contenuto importante di test");
        File.WriteAllBytes(originalFile, content);
        string expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var service = new QuarantineService(quarantineDir);
        var item = service.MoveToQuarantine(originalFile, "sess-1");

        Assert.False(File.Exists(originalFile));
        Assert.True(File.Exists(item.QuarantinedPath));
        Assert.Equal(originalFile, item.OriginalPath);
        Assert.Equal(content.Length, item.SizeBytes);
        Assert.Equal(expectedHash, item.Sha256);
        Assert.True(File.Exists(Path.Combine(quarantineDir, "sess-1", "manifest.json")));

        var listed = service.ListItems();
        Assert.Single(listed);

        var restore = service.RestoreItem(item.Id);
        Assert.True(restore.Success);
        Assert.True(File.Exists(originalFile));
        Assert.Equal(content, File.ReadAllBytes(originalFile));
        Assert.Empty(service.ListItems());
    }

    [Fact]
    public void MoveToQuarantine_SameFilenameDifferentFolders_NoCollision()
    {
        string dirA = Path.Combine(_tempDir, "a");
        string dirB = Path.Combine(_tempDir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        string fileA = Path.Combine(dirA, "note.txt");
        string fileB = Path.Combine(dirB, "note.txt");
        File.WriteAllText(fileA, "A");
        File.WriteAllText(fileB, "B");

        var service = new QuarantineService(Path.Combine(_tempDir, "q"));
        var itemA = service.MoveToQuarantine(fileA, "sess-same");
        var itemB = service.MoveToQuarantine(fileB, "sess-same");

        Assert.NotEqual(itemA.QuarantinedPath, itemB.QuarantinedPath);
        Assert.Equal(2, service.ListItems().Count);
    }

    [Fact]
    public void RestoreItem_WhenTargetAlreadyExists_DoesNotOverwrite()
    {
        string workDir = Path.Combine(_tempDir, "collision");
        Directory.CreateDirectory(workDir);
        string file = Path.Combine(workDir, "foto.txt");
        File.WriteAllText(file, "vecchio");

        var service = new QuarantineService(Path.Combine(_tempDir, "q"));
        var item = service.MoveToQuarantine(file, "s1");

        File.WriteAllText(file, "nuovo");
        var restore = service.RestoreItem(item.Id);

        Assert.True(restore.Success);
        Assert.NotNull(restore.RestoredPath);
        Assert.NotEqual(file, restore.RestoredPath);
        Assert.Equal("nuovo", File.ReadAllText(file));
        Assert.Equal("vecchio", File.ReadAllText(restore.RestoredPath!));
    }

    [Fact]
    public void GetExpiredItemsAndPurgeItems_Respects30Days()
    {
        string workDir = Path.Combine(_tempDir, "expiry");
        Directory.CreateDirectory(workDir);
        string oldFile = Path.Combine(workDir, "old.bin");
        string freshFile = Path.Combine(workDir, "fresh.bin");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(freshFile, "fresh");

        var service = new QuarantineService(Path.Combine(_tempDir, "q"));
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        var oldItem = service.MoveToQuarantine(oldFile, "s-old", now.AddDays(-31));
        var freshItem = service.MoveToQuarantine(freshFile, "s-fresh", now.AddDays(-10));

        var expired = service.GetExpiredItems(30, now);
        Assert.Single(expired);
        Assert.Equal(oldItem.Id, expired[0].Id);

        int purged = service.PurgeItems([oldItem.Id]);
        Assert.Equal(1, purged);
        Assert.False(File.Exists(oldItem.QuarantinedPath));

        var remaining = service.ListItems();
        Assert.Single(remaining);
        Assert.Equal(freshItem.Id, remaining[0].Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
