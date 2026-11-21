using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Core.Actions;

/// <summary>Voce di avvio rilevata su Windows.</summary>
public sealed record StartupEntry(
    string Scope,
    string Name,
    string Command,
    bool IsEnabled,
    bool RequiresAdmin);

/// <summary>Piano energetico rilevato tramite powercfg.</summary>
public sealed record PowerPlanInfo(
    Guid Guid,
    string Name,
    bool IsActive);

internal interface IStartupStore
{
    List<StartupEntry> GetEntries();
    byte[]? ReadApprovedBytes(string scope, string name);
    void WriteApprovedBytes(string scope, string name, byte[]? value);
}

/// <summary>
/// Ottimizzazione reversibile di Windows 11:
/// 1) programmi all'avvio (HKCU/HKLM Run e cartella Startup, tramite StartupApproved);
/// 2) piano energetico (powercfg).
/// </summary>
public sealed class OptimizationService
{
    internal const string ScopeHkcuRun = "HKCU_RUN";
    internal const string ScopeHklmRun = "HKLM_RUN";
    internal const string ScopeStartupFolder = "STARTUP_FOLDER";

    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string FolderApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string AbsentMarker = "<absent>";

    private static readonly Regex PowerPlanRegex = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\((.+)\)\s*(\*)?\s*$",
        RegexOptions.Compiled);

    private readonly ActionLog _actionLog;
    private readonly RestorePointService _restorePointService;
    private readonly IStartupStore _startupStore;
    private readonly Func<string, (int ExitCode, string Output, string Error)> _runPowerCfg;

    public OptimizationService(ActionLog? actionLog = null, RestorePointService? restorePointService = null)
        : this(actionLog, restorePointService, new WindowsStartupStore(), RunPowerCfgProcess)
    {
    }

    internal OptimizationService(
        ActionLog? actionLog,
        RestorePointService? restorePointService,
        IStartupStore startupStore,
        Func<string, (int ExitCode, string Output, string Error)> runPowerCfg)
    {
        _actionLog = actionLog ?? new ActionLog();
        _restorePointService = restorePointService ?? new RestorePointService();
        _startupStore = startupStore ?? throw new ArgumentNullException(nameof(startupStore));
        _runPowerCfg = runPowerCfg ?? throw new ArgumentNullException(nameof(runPowerCfg));
    }

    /// <summary>Elenca in sola lettura i programmi all'avvio e i piani energetici disponibili.</summary>
    public List<PlannedChange> Scan(CancellationToken cancellationToken = default)
    {
        var changes = new List<PlannedChange>();

        foreach (var item in _startupStore.GetEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ActionKind kind = item.IsEnabled ? ActionKind.StartupDisable : ActionKind.StartupEnable;
            string before = item.IsEnabled ? "Abilitato all'avvio" : "Disabilitato";
            string after = item.IsEnabled ? "Disabilitato all'avvio" : "Abilitato all'avvio";
            string scopeDesc = item.Scope switch
            {
                ScopeHkcuRun => "Registro utente (HKCU\\...\\Run)",
                ScopeHklmRun => "Registro sistema (HKLM\\...\\Run)",
                _ => "Cartella Esecuzione automatica",
            };

            changes.Add(new PlannedChange
            {
                Id = $"startup:{item.Scope}:{item.Name}",
                Category = "Avvio di Windows",
                Title = item.Name,
                Explanation = item.IsEnabled
                    ? $"«{item.Name}» parte da solo all'accensione. Disabilitarlo velocizza l'avvio (puoi riattivarlo in qualsiasi momento)."
                    : $"«{item.Name}» è attualmente disabilitato all'avvio. Puoi riattivarlo.",
                TechnicalDetail = $"{scopeDesc} · comando: {item.Command}",
                BeforeValue = before,
                AfterValue = after,
                EstimatedBytes = 0,
                RequiresAdmin = item.RequiresAdmin,
                IsReversible = true,
                IsSelected = false,
                TargetKey = $"{item.Scope}|{item.Name}",
                ActionKind = kind,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var plans = GetPowerPlans();
        var active = plans.FirstOrDefault(p => p.IsActive);
        if (active is not null)
        {
            foreach (var plan in plans.Where(p => !p.IsActive))
            {
                changes.Add(new PlannedChange
                {
                    Id = $"powerplan:{plan.Guid:D}",
                    Category = "Piano energetico",
                    Title = $"Piano energetico: {plan.Name}",
                    Explanation = $"Cambia il piano energetico da «{active.Name}» a «{plan.Name}».",
                    TechnicalDetail = $"powercfg /setactive {plan.Guid:D} (attuale: {active.Name} · {active.Guid:D})",
                    BeforeValue = active.Name,
                    AfterValue = plan.Name,
                    EstimatedBytes = 0,
                    RequiresAdmin = false,
                    IsReversible = true,
                    IsSelected = false,
                    TargetKey = plan.Guid.ToString("D"),
                    ActionKind = ActionKind.PowerPlanChange,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OldGuid"] = active.Guid.ToString("D"),
                    },
                });
            }
        }

        return changes;
    }

    /// <summary>Applica le ottimizzazioni selezionate salvando i valori precedenti in ActionLog.</summary>
    public ActionExecutionResult Apply(
        IEnumerable<PlannedChange> changes,
        bool createRestorePoint = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var selected = changes.Where(c => c.IsSelected).ToList();
        var outcomes = new List<ActionItemOutcome>();

        if (selected.Count == 0)
        {
            return new ActionExecutionResult(null, null, outcomes);
        }

        RestorePointResult? restorePoint = createRestorePoint
            ? _restorePointService.Create("W11-cleaner-tool: ottimizzazione")
            : null;

        var session = new ActionSession
        {
            TimestampUtc = DateTime.UtcNow,
            RestorePointCreated = restorePoint?.Status == RestorePointStatus.Created,
        };

        foreach (var change in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (outcome, entry) = change.ActionKind switch
            {
                ActionKind.StartupDisable => ApplyStartupToggle(change, enable: false),
                ActionKind.StartupEnable => ApplyStartupToggle(change, enable: true),
                ActionKind.PowerPlanChange => ApplyPowerPlanChange(change),
                _ => (new ActionItemOutcome(change.Id, change.Title, false, "Azione non supportata da OptimizationService."), null),
            };

            outcomes.Add(outcome);
            if (outcome.Succeeded && entry is not null)
            {
                session.Entries.Add(entry);
            }
        }

        if (session.Entries.Count > 0)
        {
            _actionLog.AddSession(session);
            return new ActionExecutionResult(session, restorePoint, outcomes);
        }

        return new ActionExecutionResult(null, restorePoint, outcomes);
    }

    /// <summary>Annulla una modifica registrata da OptimizationService.</summary>
    public UndoOutcome UndoEntry(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.ActionKind is ActionKind.StartupDisable or ActionKind.StartupEnable)
        {
            if (!TrySplitStartupTarget(entry.Target, out string scope, out string name))
            {
                return new UndoOutcome(entry.Id, entry.Title, false, "Riferimento della voce di avvio non valido.");
            }

            try
            {
                byte[]? restoreBytes = string.Equals(entry.OldValue, AbsentMarker, StringComparison.Ordinal)
                    ? null
                    : Convert.FromBase64String(entry.OldValue);

                _startupStore.WriteApprovedBytes(scope, name, restoreBytes);
                return new UndoOutcome(entry.Id, entry.Title, true, $"Stato originale di «{name}» ripristinato.");
            }
            catch (FormatException)
            {
                return new UndoOutcome(entry.Id, entry.Title, false, "Valore originale salvato non valido.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                return new UndoOutcome(entry.Id, entry.Title, false, "Permessi insufficienti per ripristinare la voce: " + ex.Message);
            }
        }

        if (entry.ActionKind == ActionKind.PowerPlanChange)
        {
            if (!Guid.TryParse(entry.OldValue, out Guid oldGuid))
            {
                return new UndoOutcome(entry.Id, entry.Title, false, "GUID del piano energetico precedente non valido.");
            }

            var (exitCode, _, error) = _runPowerCfg($"/setactive {oldGuid:D}");
            return exitCode == 0
                ? new UndoOutcome(entry.Id, entry.Title, true, $"Piano energetico precedente ({oldGuid:D}) ripristinato.")
                : new UndoOutcome(entry.Id, entry.Title, false, $"powercfg ha restituito errore ({exitCode}): {error}".Trim());
        }

        return new UndoOutcome(entry.Id, entry.Title, false, "Tipo di modifica non gestito da OptimizationService.");
    }

    public List<PowerPlanInfo> GetPowerPlans()
    {
        var (exitCode, output, _) = _runPowerCfg("/list");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return new List<PowerPlanInfo>();
        }

        return ParsePowerPlans(output);
    }

    internal static List<PowerPlanInfo> ParsePowerPlans(string output)
    {
        var plans = new List<PowerPlanInfo>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return plans;
        }

        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PowerPlanRegex.Match(line);
            if (match.Success && Guid.TryParse(match.Groups[1].Value, out Guid guid))
            {
                string name = match.Groups[2].Value.Trim();
                bool isActive = match.Groups[3].Success;
                plans.Add(new PowerPlanInfo(guid, name, isActive));
            }
        }

        return plans;
    }

    internal static bool IsStartupApprovedEnabled(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return true;
        }

        return (data[0] & 0x01) == 0;
    }

    internal static byte[] BuildStartupApprovedBytes(bool enable, DateTime utcNow)
    {
        var bytes = new byte[12];
        if (enable)
        {
            bytes[0] = 0x02;
            return bytes;
        }

        bytes[0] = 0x03;
        byte[] fileTime = BitConverter.GetBytes(utcNow.ToFileTimeUtc());
        Buffer.BlockCopy(fileTime, 0, bytes, 4, 8);
        return bytes;
    }

    private (ActionItemOutcome Outcome, ActionLogEntry? Entry) ApplyStartupToggle(PlannedChange change, bool enable)
    {
        if (!TrySplitStartupTarget(change.TargetKey, out string scope, out string name))
        {
            return (new ActionItemOutcome(change.Id, change.Title, false, "Voce di avvio non valida."), null);
        }

        try
        {
            byte[]? oldBytes = _startupStore.ReadApprovedBytes(scope, name);
            byte[] newBytes = BuildStartupApprovedBytes(enable, DateTime.UtcNow);
            _startupStore.WriteApprovedBytes(scope, name, newBytes);

            var entry = new ActionLogEntry
            {
                ActionKind = change.ActionKind,
                Title = change.Title,
                Target = change.TargetKey,
                OldValue = oldBytes is null ? AbsentMarker : Convert.ToBase64String(oldBytes),
                NewValue = Convert.ToBase64String(newBytes),
                TimestampUtc = DateTime.UtcNow,
            };

            string msg = enable
                ? $"«{name}» abilitato all'avvio."
                : $"«{name}» disabilitato all'avvio.";
            return (new ActionItemOutcome(change.Id, change.Title, true, msg), entry);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            string msg = scope == ScopeHklmRun && !RestorePointService.IsAdministrator()
                ? $"Servono i diritti di amministratore per modificare «{name}» in HKLM."
                : $"Impossibile modificare «{name}»: {ex.Message}";
            return (new ActionItemOutcome(change.Id, change.Title, false, msg), null);
        }
    }

    private (ActionItemOutcome Outcome, ActionLogEntry? Entry) ApplyPowerPlanChange(PlannedChange change)
    {
        if (!Guid.TryParse(change.TargetKey, out Guid newGuid))
        {
            return (new ActionItemOutcome(change.Id, change.Title, false, "GUID piano energetico non valido."), null);
        }

        string oldGuidStr = "";
        var currentActive = GetPowerPlans().FirstOrDefault(p => p.IsActive);
        if (currentActive is not null)
        {
            oldGuidStr = currentActive.Guid.ToString("D");
        }
        else if (change.Metadata.TryGetValue("OldGuid", out string? metaOld))
        {
            oldGuidStr = metaOld;
        }

        var (exitCode, _, error) = _runPowerCfg($"/setactive {newGuid:D}");
        if (exitCode != 0)
        {
            return (new ActionItemOutcome(change.Id, change.Title, false, $"powercfg ha restituito errore ({exitCode}): {error}".Trim()), null);
        }

        var entry = new ActionLogEntry
        {
            ActionKind = ActionKind.PowerPlanChange,
            Title = change.Title,
            Target = "PowerPlan",
            OldValue = oldGuidStr,
            NewValue = newGuid.ToString("D"),
            TimestampUtc = DateTime.UtcNow,
        };
        return (new ActionItemOutcome(change.Id, change.Title, true, $"Piano energetico impostato su «{change.AfterValue}»."), entry);
    }

    private static bool TrySplitStartupTarget(string target, out string scope, out string name)
    {
        int sep = target.IndexOf('|');
        if (sep <= 0 || sep >= target.Length - 1)
        {
            scope = "";
            name = "";
            return false;
        }

        scope = target[..sep];
        name = target[(sep + 1)..];
        return scope is ScopeHkcuRun or ScopeHklmRun or ScopeStartupFolder;
    }

    private static (int ExitCode, string Output, string Error) RunPowerCfgProcess(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("powercfg.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private sealed class WindowsStartupStore : IStartupStore
    {
        public List<StartupEntry> GetEntries()
        {
            var list = new List<StartupEntry>();
            ReadRunKey(RegistryHive.CurrentUser, ScopeHkcuRun, requiresAdmin: false, list);
            ReadRunKey(RegistryHive.LocalMachine, ScopeHklmRun, requiresAdmin: true, list);
            ReadStartupFolder(list);
            return list;
        }

        public byte[]? ReadApprovedBytes(string scope, string name)
        {
            var (hive, subKey) = ApprovedLocation(scope);
            return RegistryValues.Read(hive, subKey, name) as byte[];
        }

        public void WriteApprovedBytes(string scope, string name, byte[]? value)
        {
            var (hive, subKey) = ApprovedLocation(scope);
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            if (value is null)
            {
                using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
                return;
            }

            using RegistryKey writableKey = baseKey.CreateSubKey(subKey, writable: true);
            writableKey.SetValue(name, value, RegistryValueKind.Binary);
        }

        private static (RegistryHive Hive, string SubKey) ApprovedLocation(string scope) => scope switch
        {
            ScopeHklmRun => (RegistryHive.LocalMachine, RunApprovedSubKey),
            ScopeStartupFolder => (RegistryHive.CurrentUser, FolderApprovedSubKey),
            _ => (RegistryHive.CurrentUser, RunApprovedSubKey),
        };

        private static void ReadRunKey(RegistryHive hive, string scope, bool requiresAdmin, List<StartupEntry> list)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? key = baseKey.OpenSubKey(RunSubKey, writable: false);
                if (key is null)
                {
                    return;
                }

                string approvedPath = RunApprovedSubKey;
                foreach (string name in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string command = key.GetValue(name)?.ToString() ?? "";
                    byte[]? approved = RegistryValues.Read(hive, approvedPath, name) as byte[];
                    list.Add(new StartupEntry(
                        scope,
                        name,
                        command,
                        IsStartupApprovedEnabled(approved),
                        requiresAdmin));
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Ignora chiavi non leggibili.
            }
        }

        private static void ReadStartupFolder(List<StartupEntry> list)
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    string fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    byte[]? approved = RegistryValues.Read(RegistryHive.CurrentUser, FolderApprovedSubKey, fileName) as byte[];
                    list.Add(new StartupEntry(
                        ScopeStartupFolder,
                        fileName,
                        file,
                        IsStartupApprovedEnabled(approved),
                        RequiresAdmin: false));
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Ignora errori di accesso alla cartella.
            }
        }
    }
}
