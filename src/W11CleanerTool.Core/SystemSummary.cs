using Microsoft.Win32;

namespace W11CleanerTool.Core;

/// <summary>Riepilogo del PC per il rapporto: niente numeri di serie, niente nomi.</summary>
public static class SystemSummary
{
    public static List<string> Lines()
    {
        var lines = new List<string>();
        Try(lines, () =>
        {
            const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string product = RegistryValues.Read(RegistryHive.LocalMachine, cv, "ProductName")?.ToString() ?? "Windows";
            string edition = RegistryValues.Read(RegistryHive.LocalMachine, cv, "EditionID")?.ToString() ?? "";
            string display = RegistryValues.Read(RegistryHive.LocalMachine, cv, "DisplayVersion")?.ToString() ?? "";
            string build = RegistryValues.Read(RegistryHive.LocalMachine, cv, "CurrentBuild")?.ToString() ?? "";
            string ubr = RegistryValues.Read(RegistryHive.LocalMachine, cv, "UBR")?.ToString() ?? "";

            // Il registro dice ancora «Windows 10» anche su Windows 11: si corregge con il numero di build.
            if (int.TryParse(build, out int b) && b >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            return $"Sistema: {product} ({edition}) {display}, build {build}.{ubr}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";
        });
        Try(lines, () => "Processore: " + string.Join(", ", Wmi.Query("SELECT Name FROM Win32_Processor").Select(r => r.Str("Name"))));
        Try(lines, () =>
        {
            long bytes = Wmi.Query("SELECT Capacity FROM Win32_PhysicalMemory").Sum(r => r.Long("Capacity"));
            return "RAM: " + Heuristics.Gigabytes(bytes);
        });
        Try(lines, () => "Scheda video: " + string.Join(", ", Wmi.Query("SELECT Name, DriverVersion FROM Win32_VideoController").Select(r => $"{r.Str("Name")} (driver {r.Str("DriverVersion")})")));
        Try(lines, () => "Scheda madre: " + string.Join(", ", Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").Select(r => $"{r.Str("Manufacturer")} {r.Str("Product")}")));
        Try(lines, () => "BIOS: " + string.Join(", ", Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").Select(r =>
            $"{r.Str("Manufacturer")} {r.Str("SMBIOSBIOSVersion")} del {Heuristics.ParseWmiDate(r.Str("ReleaseDate"))?.ToString("dd/MM/yyyy") ?? "?"}")));
        return lines;
    }

    private static void Try(List<string> lines, Func<string> read)
    {
        try
        {
            lines.Add(read());
        }
        catch (Exception ex)
        {
            lines.Add("(dato non leggibile: " + ex.GetType().Name + ")");
        }
    }
}
