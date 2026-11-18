using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace W11CleanerTool.Core.Checks;

/// <summary>Checklist gaming: legge e basta, non cambia niente.</summary>
public static class GamingChecks
{
    private const string Area = "Gaming";

    private static readonly Dictionary<Guid, string> KnownPowerPlans = new()
    {
        [new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Bilanciato",
        [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "Prestazioni elevate",
        [new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")] = "Prestazioni ottimali",
        [new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Risparmio energia",
    };

    public static List<CheckResult> Run()
    {
        var results = new List<CheckResult>();
        Safe(results, "Modalità gioco", GameMode);
        Safe(results, "Pianificazione GPU accelerata", HardwareGpuScheduling);
        Safe(results, "Piano energetico", PowerPlan);
        SafeMany(results, "Frequenza del monitor", RefreshRates);
        Safe(results, "Integrità della memoria", MemoryIntegrity);
        Safe(results, "Registrazione in background", BackgroundRecording);
        SafeMany(results, "Driver della scheda video", GpuDrivers);
        Safe(results, "Spazio sul disco di sistema", SystemDiskSpace);
        Safe(results, "Riavvio in sospeso", PendingReboot);
        return results;
    }

    private static void Safe(List<CheckResult> results, string title, Func<CheckResult> check)
    {
        try
        {
            results.Add(check());
        }
        catch (Exception ex)
        {
            results.Add(new CheckResult(Area, title, CheckStatus.Info, "Non sono riuscito a leggere questo dato.", ex.GetType().Name + ": " + ex.Message));
        }
    }

    private static void SafeMany(List<CheckResult> results, string title, Func<IEnumerable<CheckResult>> check)
    {
        try
        {
            results.AddRange(check());
        }
        catch (Exception ex)
        {
            results.Add(new CheckResult(Area, title, CheckStatus.Info, "Non sono riuscito a leggere questo dato.", ex.GetType().Name + ": " + ex.Message));
        }
    }

    private static CheckResult GameMode()
    {
        const string path = @"Software\Microsoft\GameBar";
        int? value = RegistryValues.ReadInt(RegistryHive.CurrentUser, path, "AutoGameModeEnabled");
        string detail = $@"HKCU\{path}\AutoGameModeEnabled = {(value?.ToString() ?? "non impostato (predefinito: attiva)")}";
        return value == 0
            ? new CheckResult(Area, "Modalità gioco", CheckStatus.Warning, "La Modalità gioco è spenta. Accendila da Impostazioni › Giochi › Modalità gioco.", detail)
            : new CheckResult(Area, "Modalità gioco", CheckStatus.Ok, "La Modalità gioco è attiva.", detail);
    }

    private static CheckResult HardwareGpuScheduling()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        int? value = RegistryValues.ReadInt(RegistryHive.LocalMachine, path, "HwSchMode");
        string detail = $@"HKLM\{path}\HwSchMode = {(value?.ToString() ?? "non impostato")} (2 = attiva, 1 = spenta)";
        return value switch
        {
            2 => new CheckResult(Area, "Pianificazione GPU accelerata", CheckStatus.Ok, "La pianificazione GPU accelerata dall'hardware è attiva.", detail),
            1 => new CheckResult(Area, "Pianificazione GPU accelerata", CheckStatus.Info, "La pianificazione GPU accelerata è spenta. Con le schede video recenti di solito conviene accenderla: Impostazioni › Schermo › Grafica.", detail),
            _ => new CheckResult(Area, "Pianificazione GPU accelerata", CheckStatus.Info, "Impostazione predefinita di Windows. Si cambia da Impostazioni › Schermo › Grafica, se la scheda video lo permette.", detail),
        };
    }

    private static CheckResult PowerPlan()
    {
        uint rc = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr);
        if (rc != 0 || ptr == IntPtr.Zero)
        {
            return new CheckResult(Area, "Piano energetico", CheckStatus.Info, "Non sono riuscito a leggere il piano energetico.", $"PowerGetActiveScheme = {rc}");
        }

        Guid guid;
        try
        {
            guid = Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            NativeMethods.LocalFree(ptr);
        }

        string name = KnownPowerPlans.TryGetValue(guid, out string? n) ? n : "personalizzato";
        string detail = $"Piano attivo: {name} ({guid})";
        return name == "Risparmio energia"
            ? new CheckResult(Area, "Piano energetico", CheckStatus.Warning, "Il PC è in Risparmio energia: nei giochi rende meno. Su un PC fisso conviene Bilanciato o Prestazioni elevate.", detail)
            : new CheckResult(Area, "Piano energetico", CheckStatus.Ok, $"Piano energetico: {name}.", detail);
    }

    private static IEnumerable<CheckResult> RefreshRates()
    {
        var results = new List<CheckResult>();
        var device = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var current = new NativeMethods.DEVMODE { dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODE>() };
                if (NativeMethods.EnumDisplaySettings(device.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref current))
                {
                    int max = current.dmDisplayFrequency;
                    var mode = new NativeMethods.DEVMODE { dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODE>() };
                    for (int m = 0; NativeMethods.EnumDisplaySettings(device.DeviceName, m, ref mode); m++)
                    {
                        if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmDisplayFrequency > max)
                        {
                            max = mode.dmDisplayFrequency;
                        }
                    }

                    int number = results.Count + 1;
                    string title = $"Frequenza del monitor {number}";
                    string detail = $"{device.DeviceString}: {current.dmPelsWidth}×{current.dmPelsHeight} a {current.dmDisplayFrequency} Hz (massimo a questa risoluzione: {max} Hz)";
                    CheckStatus status = Heuristics.RefreshRate(current.dmDisplayFrequency, max);
                    string explanation = status == CheckStatus.Warning
                        ? $"Il monitor {number} va a {current.dmDisplayFrequency} Hz ma può arrivare a {max} Hz. Si cambia da Impostazioni › Schermo › Schermo avanzato."
                        : $"Il monitor {number} va a {current.dmDisplayFrequency} Hz.";
                    results.Add(new CheckResult(Area, title, status, explanation, detail));
                }
            }

            device = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        }

        if (results.Count == 0)
        {
            results.Add(new CheckResult(Area, "Frequenza del monitor", CheckStatus.Info, "Non ho trovato monitor da controllare.", "EnumDisplayDevices: nessun dispositivo collegato al desktop"));
        }

        return results;
    }

    private static CheckResult MemoryIntegrity()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
        int? value = RegistryValues.ReadInt(RegistryHive.LocalMachine, path, "Enabled");
        string detail = $@"HKLM\{path}\Enabled = {(value?.ToString() ?? "non impostato")}";
        return value == 1
            ? new CheckResult(Area, "Integrità della memoria", CheckStatus.Info, "L'integrità della memoria è attiva: protegge il PC e in alcuni giochi costa qualche FPS. È una protezione di sicurezza: si lascia accesa, salvo motivi precisi.", detail)
            : new CheckResult(Area, "Integrità della memoria", CheckStatus.Info, "L'integrità della memoria è spenta. Si controlla da Sicurezza di Windows › Sicurezza dispositivi › Isolamento core.", detail);
    }

    private static CheckResult BackgroundRecording()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
        int? value = RegistryValues.ReadInt(RegistryHive.CurrentUser, path, "HistoricalCaptureEnabled");
        string detail = $@"HKCU\{path}\HistoricalCaptureEnabled = {(value?.ToString() ?? "non impostato (predefinito: spenta)")}";
        return value == 1
            ? new CheckResult(Area, "Registrazione in background", CheckStatus.Warning, "La Xbox Game Bar registra sempre gli ultimi minuti di gioco: costa un po' di prestazioni. Se non ti serve, spegnila da Impostazioni › Giochi › Acquisizioni.", detail)
            : new CheckResult(Area, "Registrazione in background", CheckStatus.Ok, "La registrazione continua in background è spenta.", detail);
    }

    private static IEnumerable<CheckResult> GpuDrivers()
    {
        var results = new List<CheckResult>();
        foreach (var row in Wmi.Query("SELECT Name, DriverVersion, DriverDate, AdapterCompatibility FROM Win32_VideoController"))
        {
            string name = row.Str("Name");
            DateTime? date = Heuristics.ParseWmiDate(row.Str("DriverDate"));
            CheckStatus status = Heuristics.DriverAge(date, DateTime.Today);
            string when = date?.ToString("dd/MM/yyyy") ?? "data sconosciuta";
            string explanation = status == CheckStatus.Warning
                ? $"Il driver di {name} è del {when}: ha più di un anno. Controlla sul sito del produttore se ce n'è uno nuovo."
                : $"Il driver di {name} è del {when}.";
            results.Add(new CheckResult(Area, "Driver della scheda video", status, explanation, $"{name} · {row.Str("AdapterCompatibility")} · versione {row.Str("DriverVersion")} · {when}"));
        }

        return results;
    }

    private static CheckResult SystemDiskSpace()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var drive = new DriveInfo(root);
        CheckStatus status = Heuristics.FreeSpace(drive.AvailableFreeSpace, drive.TotalSize);
        string free = Heuristics.Gigabytes(drive.AvailableFreeSpace);
        string total = Heuristics.Gigabytes(drive.TotalSize);
        string explanation = status switch
        {
            CheckStatus.Problem => $"Sul disco {root} restano solo {free} su {total}: Windows e i giochi rallentano. Serve fare spazio.",
            CheckStatus.Warning => $"Sul disco {root} restano {free} su {total}: conviene fare un po' di spazio.",
            _ => $"Sul disco {root} restano {free} su {total}.",
        };
        return new CheckResult(Area, "Spazio sul disco di sistema", status, explanation, $"{root} {drive.DriveFormat}: liberi {drive.AvailableFreeSpace:N0} byte su {drive.TotalSize:N0}");
    }

    private static CheckResult PendingReboot()
    {
        bool cbs = RegistryValues.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        bool wu = RegistryValues.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        string detail = $"Component Based Servicing\\RebootPending: {(cbs ? "sì" : "no")} · WindowsUpdate\\RebootRequired: {(wu ? "sì" : "no")}";
        return cbs || wu
            ? new CheckResult(Area, "Riavvio in sospeso", CheckStatus.Warning, "Windows aspetta un riavvio per finire un aggiornamento. Riavvia quando puoi.", detail)
            : new CheckResult(Area, "Riavvio in sospeso", CheckStatus.Ok, "Nessun riavvio in sospeso.", detail);
    }
}
