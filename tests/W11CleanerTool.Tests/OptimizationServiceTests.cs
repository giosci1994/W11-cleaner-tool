using W11CleanerTool.Core;
using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class OptimizationServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-opt-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParsePowerPlans_WorksInItalianAndEnglishWithParentheses()
    {
        const string italian = """
            Schema di alimentazione esistenti (* Attivo)
            -----------------------------------
            GUID dello schema di alimentazione: 381b4222-f694-41f0-9685-ff5bb260df2e  (Bilanciato) *
            GUID dello schema di alimentazione: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Prestazioni elevate (Custom))
            """;

        var plansIt = OptimizationService.ParsePowerPlans(italian);
        Assert.Equal(2, plansIt.Count);
        Assert.Equal("Bilanciato", plansIt[0].Name);
        Assert.True(plansIt[0].IsActive);
        Assert.Equal("Prestazioni elevate (Custom)", plansIt[1].Name);
        Assert.False(plansIt[1].IsActive);

        const string english = """
            Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
            Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance) *
            """;

        var plansEn = OptimizationService.ParsePowerPlans(english);
        Assert.Equal(2, plansEn.Count);
        Assert.False(plansEn[0].IsActive);
        Assert.True(plansEn[1].IsActive);
    }

    [Fact]
    public void StartupAndPowerPlan_ApplyAndUndo_RestoresExactState()
    {
        var store = new FakeStartupStore();
        store.Entries.Add(new StartupEntry(OptimizationService.ScopeHkcuRun, "ChatApp", "chat.exe", IsEnabled: true, RequiresAdmin: false));

        var powerCfgCalls = new List<string>();
        const string powerList = """
            GUID dello schema di alimentazione: 381b4222-f694-41f0-9685-ff5bb260df2e  (Bilanciato) *
            GUID dello schema di alimentazione: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Prestazioni elevate)
            """;

        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var service = new OptimizationService(log, new RestorePointService(), store, args =>
        {
            powerCfgCalls.Add(args);
            return args == "/list" ? (0, powerList, "") : (0, "", "");
        });

        var changes = service.Scan();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.False(c.IsSelected));

        var selected = changes.Select(c => c with { IsSelected = true }).ToList();
        var apply = service.Apply(selected);
        Assert.Equal(2, apply.SucceededCount);
        Assert.False(OptimizationService.IsStartupApprovedEnabled(store.ReadApprovedBytes(OptimizationService.ScopeHkcuRun, "ChatApp")));
        Assert.Contains("/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", powerCfgCalls);

        var coordinator = new UndoCoordinator(log, new QuarantineService(Path.Combine(_tempDir, "q")), service);
        var undo = coordinator.UndoSession(apply.Session!.SessionId);
        Assert.Equal(2, undo.Count);
        Assert.All(undo, u => Assert.True(u.Succeeded));
        Assert.Null(store.ReadApprovedBytes(OptimizationService.ScopeHkcuRun, "ChatApp"));
        Assert.Contains("/setactive 381b4222-f694-41f0-9685-ff5bb260df2e", powerCfgCalls);
    }

    [Fact]
    public void KeepAwakeService_TogglesAndDisposesCleanly()
    {
        var calls = new List<NativeMethods.ExecutionState>();
        using var keepAwake = new KeepAwakeService(state =>
        {
            calls.Add(state);
            return NativeMethods.ExecutionState.ES_CONTINUOUS;
        });

        Assert.False(keepAwake.IsActive);
        Assert.True(keepAwake.Enable());
        Assert.True(keepAwake.IsActive);

        keepAwake.Dispose();
        Assert.False(keepAwake.IsActive);
        Assert.Equal(2, calls.Count);
        Assert.Equal(NativeMethods.ExecutionState.ES_CONTINUOUS | NativeMethods.ExecutionState.ES_SYSTEM_REQUIRED, calls[0]);
        Assert.Equal(NativeMethods.ExecutionState.ES_CONTINUOUS, calls[1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class FakeStartupStore : IStartupStore
    {
        private readonly Dictionary<string, byte[]> _approved = new(StringComparer.Ordinal);
        public List<StartupEntry> Entries { get; } = new();

        public List<StartupEntry> GetEntries() => Entries;

        public byte[]? ReadApprovedBytes(string scope, string name)
            => _approved.TryGetValue($"{scope}|{name}", out byte[]? val) ? val : null;

        public void WriteApprovedBytes(string scope, string name, byte[]? value)
        {
            string key = $"{scope}|{name}";
            if (value is null)
            {
                _approved.Remove(key);
            }
            else
            {
                _approved[key] = value;
            }
        }
    }
}
