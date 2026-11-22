using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class ActionLogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddSessionAndReadSessions_PersistsToJson()
    {
        string logPath = Path.Combine(_tempDir, "history.json");
        var log = new ActionLog(logPath);

        var session = new ActionSession
        {
            SessionId = "s1",
            TimestampUtc = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc),
            RestorePointCreated = true,
            Entries =
            [
                new ActionLogEntry
                {
                    Id = "e1",
                    ActionKind = ActionKind.StartupDisable,
                    Title = "AppTest",
                    Target = "HKCU_RUN|AppTest",
                    OldValue = "<absent>",
                    NewValue = "AwAAAA==",
                },
            ],
        };

        log.AddSession(session);

        var reloaded = new ActionLog(logPath);
        var sessions = reloaded.ReadSessions();
        Assert.Single(sessions);
        Assert.Equal("s1", sessions[0].SessionId);
        Assert.True(sessions[0].RestorePointCreated);
        Assert.Single(sessions[0].Entries);
        Assert.Equal("e1", sessions[0].Entries[0].Id);
        Assert.False(sessions[0].Entries[0].Undone);
    }

    [Fact]
    public void MarkEntryAndSessionUndone_UpdatesState()
    {
        string logPath = Path.Combine(_tempDir, "history.json");
        var log = new ActionLog(logPath);

        log.AddSession(new ActionSession
        {
            SessionId = "s1",
            Entries =
            [
                new ActionLogEntry { Id = "e1", Title = "Uno" },
                new ActionLogEntry { Id = "e2", Title = "Due" },
            ],
        });

        Assert.True(log.MarkEntryUndone("e1"));
        Assert.True(log.FindEntry("e1")!.Undone);
        Assert.False(log.FindEntry("e2")!.Undone);

        Assert.Equal(1, log.MarkSessionUndone("s1"));
        Assert.True(log.FindEntry("e2")!.Undone);
        Assert.False(log.MarkEntryUndone("missing"));
    }

    [Fact]
    public void ReadSessions_CorruptJsonDoesNotCrash()
    {
        Directory.CreateDirectory(_tempDir);
        string logPath = Path.Combine(_tempDir, "history.json");
        File.WriteAllText(logPath, "{ non-valido");

        var log = new ActionLog(logPath);
        var sessions = log.ReadSessions();
        Assert.Empty(sessions);
    }

    [Fact]
    public void CorruptJson_RecoversFromBackupAndKeepsGoodBackup()
    {
        Directory.CreateDirectory(_tempDir);
        string logPath = Path.Combine(_tempDir, "history.json");
        var log = new ActionLog(logPath);
        log.AddSession(new ActionSession { SessionId = "s1", Entries = [new ActionLogEntry { Id = "e1", Title = "Uno" }] });
        log.AddSession(new ActionSession { SessionId = "s2", Entries = [new ActionLogEntry { Id = "e2", Title = "Due" }] });
        // Ora history.json.bak contiene la sola sessione s1.
        string goodBackup = File.ReadAllText(log.BackupFilePath);

        File.WriteAllText(logPath, "{ non-valido");

        var recovered = log.ReadSessions();
        Assert.Contains(recovered, s => s.SessionId == "s1");

        log.AddSession(new ActionSession { SessionId = "s3", Entries = [new ActionLogEntry { Id = "e3", Title = "Tre" }] });
        // Il file corrotto non ha sostituito la copia buona.
        Assert.Equal(goodBackup, File.ReadAllText(log.BackupFilePath));
        Assert.Contains(log.ReadSessions(), s => s.SessionId == "s1");
    }

    [Fact]
    public void ReadAndWrite_LockedFile_ThrowsAndNeverOverwrites()
    {
        string logPath = Path.Combine(_tempDir, "history.json");
        var log = new ActionLog(logPath);
        log.AddSession(new ActionSession { SessionId = "s1", Entries = [new ActionLogEntry { Id = "e1", Title = "Uno" }] });
        string before = File.ReadAllText(logPath);

        using (new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<ActionLogUnavailableException>(() => log.ReadSessions());
            Assert.Throws<ActionLogUnavailableException>(() => log.EnsureAccessible());
            Assert.Throws<ActionLogUnavailableException>(() => log.AddSession(new ActionSession { SessionId = "s2" }));
            Assert.Throws<ActionLogUnavailableException>(() => log.MarkEntryUndone("e1"));
        }

        Assert.Equal(before, File.ReadAllText(logPath));
        Assert.Single(log.ReadSessions());
    }

    [Fact]
    public void Save_KeepsBackupOfPreviousFile()
    {
        string logPath = Path.Combine(_tempDir, "history.json");
        var log = new ActionLog(logPath);
        log.AddSession(new ActionSession { SessionId = "s1" });
        Assert.False(File.Exists(log.BackupFilePath));

        string firstVersion = File.ReadAllText(logPath);
        log.AddSession(new ActionSession { SessionId = "s2" });

        Assert.True(File.Exists(log.BackupFilePath));
        Assert.Equal(firstVersion, File.ReadAllText(log.BackupFilePath));
        Assert.Equal(2, log.ReadSessions().Count);
    }

    [Fact]
    public void AppendEntry_AddsToExistingSession_AndMarkQuarantinePurged()
    {
        string logPath = Path.Combine(_tempDir, "history.json");
        var log = new ActionLog(logPath);
        log.AddSession(new ActionSession { SessionId = "s1" });

        log.AppendEntry("s1", new ActionLogEntry { Id = "e1", ActionKind = ActionKind.QuarantineDuplicate, QuarantinePath = Path.Combine(_tempDir, "q", "a.txt") });
        log.AppendEntry("s1", new ActionLogEntry { Id = "e2", ActionKind = ActionKind.QuarantineDuplicate, QuarantinePath = Path.Combine(_tempDir, "q", "b.txt") });
        Assert.Throws<ActionLogUnavailableException>(() => log.AppendEntry("manca", new ActionLogEntry()));

        var reloaded = new ActionLog(logPath);
        Assert.Equal(2, reloaded.GetSession("s1")!.Entries.Count);

        Assert.Equal(1, reloaded.MarkQuarantinePurged([Path.Combine(_tempDir, "q", "a.txt")]));
        Assert.True(reloaded.FindEntry("e1")!.QuarantinePurged);
        Assert.False(reloaded.FindEntry("e2")!.QuarantinePurged);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
