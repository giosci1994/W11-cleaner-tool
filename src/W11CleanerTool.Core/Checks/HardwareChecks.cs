namespace W11CleanerTool.Core.Checks;

/// <summary>RAM (XMP, EXPO, DOCP) e driver: legge e basta, non cambia niente.</summary>
public static class HardwareChecks
{
    private const string Area = "RAM e driver";

    public static List<CheckResult> Run()
    {
        var results = new List<CheckResult>();
        Safe(results, "Velocità della RAM", Ram);
        Safe(results, "Dispositivi con errori", DevicesWithErrors);
        Safe(results, "Driver principali", MainDrivers);
        return results;
    }

    private static void Safe(List<CheckResult> results, string title, Func<IEnumerable<CheckResult>> check)
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

    private static IEnumerable<CheckResult> Ram()
    {
        var modules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");
        if (modules.Count == 0)
        {
            return [new CheckResult(Area, "Velocità della RAM", CheckStatus.Info, "Non ho trovato i banchi di RAM.", "Win32_PhysicalMemory: nessun risultato")];
        }

        long totalBytes = modules.Sum(m => m.Long("Capacity"));
        int rated = (int)modules.Max(m => m.Long("Speed"));
        int configured = (int)modules.Min(m => m.Long("ConfiguredClockSpeed"));
        int type = (int)modules.Max(m => m.Long("SMBIOSMemoryType"));
        string typeName = Heuristics.MemoryTypeName(type);
        CheckStatus status = Heuristics.RamSpeed(rated, configured, type);

        string detail = string.Join(" | ", modules.Select(m =>
            $"{m.Str("DeviceLocator")}: {Heuristics.Gigabytes(m.Long("Capacity"))} {m.Str("Manufacturer")} {m.Str("PartNumber")} · dichiarata {m.Long("Speed")} MT/s · configurata {m.Long("ConfiguredClockSpeed")} MT/s · tipo SMBIOS {m.Long("SMBIOSMemoryType")}"));

        string summary = $"{Heuristics.Gigabytes(totalBytes)} {typeName}".Trim();
        string explanation = status switch
        {
            CheckStatus.Warning when configured < rated =>
                $"RAM {summary} a {configured} MT/s, ma i banchi dichiarano {rated} MT/s. Probabilmente il profilo XMP (sulle schede AMD si chiama EXPO o DOCP) è spento: si attiva nel BIOS, non da Windows.",
            CheckStatus.Warning =>
                $"RAM {summary} a {configured} MT/s: è la velocità base. Se i banchi sono da gaming, controlla nel BIOS che il profilo XMP (EXPO o DOCP sulle schede AMD) sia attivo. È un'indicazione, non una certezza.",
            CheckStatus.Ok => $"RAM {summary} a {configured} MT/s.",
            _ => $"RAM {summary}: velocità non disponibile.",
        };

        return [new CheckResult(Area, "Velocità della RAM", status, explanation, detail)];
    }

    private static IEnumerable<CheckResult> DevicesWithErrors()
    {
        var rows = Wmi.Query("SELECT Name, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        if (rows.Count == 0)
        {
            return [new CheckResult(Area, "Dispositivi con errori", CheckStatus.Ok, "Nessun dispositivo con errori.", "Win32_PnPEntity: ConfigManagerErrorCode = 0 per tutti")];
        }

        return rows.Select(r =>
        {
            long code = r.Long("ConfigManagerErrorCode");
            string name = string.IsNullOrEmpty(r.Str("Name")) ? "Dispositivo senza nome" : r.Str("Name");
            string why = code switch
            {
                22 => "è disattivato",
                28 => "non ha il driver",
                10 => "non riesce ad avviarsi",
                43 => "si è fermato per un problema",
                45 => "non è collegato in questo momento",
                _ => "ha un problema",
            };
            CheckStatus status = code is 22 or 45 ? CheckStatus.Info : CheckStatus.Warning;
            return new CheckResult(Area, "Dispositivo con errori", status, $"{name}: {why}. Si controlla da Gestione dispositivi.", $"{name} · classe {r.Str("PNPClass")} · codice errore {code}");
        }).ToList();
    }

    private static IEnumerable<CheckResult> MainDrivers()
    {
        var rows = Wmi.Query("SELECT DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'DISPLAY' OR DeviceClass = 'NET' OR DeviceClass = 'MEDIA'");
        var results = new List<CheckResult>();
        foreach (var r in rows.Where(r => !string.IsNullOrEmpty(r.Str("DeviceName"))))
        {
            string name = r.Str("DeviceName");
            DateTime? date = Heuristics.ParseWmiDate(r.Str("DriverDate"));
            CheckStatus status = Heuristics.DriverAge(date, DateTime.Today);
            string kind = r.Str("DeviceClass") switch
            {
                "DISPLAY" => "Video",
                "NET" => "Rete",
                "MEDIA" => "Audio",
                _ => r.Str("DeviceClass"),
            };
            string when = date?.ToString("dd/MM/yyyy") ?? "data sconosciuta";
            string explanation = status == CheckStatus.Warning
                ? $"{kind} · {name}: driver del {when}, ha più di un anno. Controlla sul sito del produttore."
                : $"{kind} · {name}: driver del {when}.";
            results.Add(new CheckResult(Area, $"Driver {kind.ToLowerInvariant()}", status, explanation, $"{name} · {r.Str("DriverProviderName")} · versione {r.Str("DriverVersion")} · {when}"));
        }

        if (results.Count == 0)
        {
            results.Add(new CheckResult(Area, "Driver principali", CheckStatus.Info, "Non ho trovato i driver di video, rete e audio.", "Win32_PnPSignedDriver: nessun risultato"));
        }

        return results;
    }
}
