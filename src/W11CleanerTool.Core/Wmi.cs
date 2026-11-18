using System.Management;

namespace W11CleanerTool.Core;

/// <summary>Piccolo aiuto per leggere WMI senza far cadere l'app se una classe non risponde.</summary>
internal static class Wmi
{
    public static List<Dictionary<string, object?>> Query(string wql, string scope = @"root\cimv2")
    {
        var rows = new List<Dictionary<string, object?>>();
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject obj in results)
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData p in obj.Properties)
                {
                    row[p.Name] = p.Value;
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    public static string Str(this Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out object? v) && v is not null ? v.ToString()?.Trim() ?? "" : "";

    public static long Long(this Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out object? v) && v is not null)
        {
            try
            {
                return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return 0;
            }
        }

        return 0;
    }
}
