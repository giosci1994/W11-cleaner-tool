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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
