using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class DuplicateFinderServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-dup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Scan_FindsExactDuplicatesOnly_IgnoresSameSizeDifferentContent()
    {
        string scanRoot = Path.Combine(_tempDir, "scan");
        string subDir = Path.Combine(scanRoot, "sub");
        Directory.CreateDirectory(subDir);

        // Duplicati più grandi di 4 KB per verificare sia il primo blocco che lo SHA256 completo.
        byte[] payloadA = new byte[6000];
        Array.Fill(payloadA, (byte)'A');
        byte[] payloadB = new byte[6000];
        Array.Fill(payloadB, (byte)'A');
        payloadB[5500] = (byte)'Z'; // Stessa taglia e stessi primi 4 KB, diverso dopo!

        string original = Path.Combine(scanRoot, "orig.dat");
        string copy = Path.Combine(subDir, "copia.dat");
        string samePrefixDifferentTail = Path.Combine(scanRoot, "diverso.dat");
        string differentSize = Path.Combine(scanRoot, "piccolo.dat");

        File.WriteAllBytes(original, payloadA);
        File.WriteAllBytes(copy, payloadA);
        File.WriteAllBytes(samePrefixDifferentTail, payloadB);
        File.WriteAllText(differentSize, "diverso");

        File.SetLastWriteTimeUtc(original, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(copy, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var quarantine = new QuarantineService(Path.Combine(_tempDir, "quarantine"));
        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var finder = new DuplicateFinderService(quarantine, log);

        var changes = finder.Scan([scanRoot]);

        Assert.Single(changes);
        Assert.Equal(copy, changes[0].TargetKey);
        Assert.Equal(ActionKind.QuarantineDuplicate, changes[0].ActionKind);
        Assert.Equal(original, changes[0].Metadata["OriginalKeptPath"]);

        // Applica in quarantena e poi ripristina tramite UndoCoordinator.
        var apply = finder.Apply(changes);
        Assert.Equal(1, apply.SucceededCount);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(copy));

        var coordinator = new UndoCoordinator(log, quarantine);
        var undoOutcomes = coordinator.UndoSession(apply.Session!.SessionId);
        Assert.Single(undoOutcomes);
        Assert.True(undoOutcomes[0].Succeeded);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void Scan_ExcludesSystemFoldersLikeAppDataAndWindows()
    {
        string scanRoot = Path.Combine(_tempDir, "userdocs");
        string appDataSub = Path.Combine(scanRoot, "AppData");
        string windowsSub = Path.Combine(scanRoot, "Windows");
        Directory.CreateDirectory(appDataSub);
        Directory.CreateDirectory(windowsSub);

        File.WriteAllText(Path.Combine(scanRoot, "doc.txt"), "testo uguale");
        File.WriteAllText(Path.Combine(appDataSub, "doc.txt"), "testo uguale");
        File.WriteAllText(Path.Combine(windowsSub, "doc.txt"), "testo uguale");

        var quarantine = new QuarantineService(Path.Combine(_tempDir, "quarantine"));
        var finder = new DuplicateFinderService(quarantine, new ActionLog(Path.Combine(_tempDir, "history.json")));

        var changes = finder.Scan([scanRoot]);
        Assert.Empty(changes);
    }

    [Fact]
    public void Scan_RespectsCancellationToken()
    {
        string scanRoot = Path.Combine(_tempDir, "cancel");
        Directory.CreateDirectory(scanRoot);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var finder = new DuplicateFinderService(
            new QuarantineService(Path.Combine(_tempDir, "q")),
            new ActionLog(Path.Combine(_tempDir, "h.json")));

        Assert.Throws<OperationCanceledException>(() => finder.Scan([scanRoot], cts.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
