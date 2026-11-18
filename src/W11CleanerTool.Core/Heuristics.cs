namespace W11CleanerTool.Core;

/// <summary>Regole pure (senza accesso al sistema), così si possono provare con i test.</summary>
public static class Heuristics
{
    /// <summary>Tipi di memoria SMBIOS: 26 = DDR4, 34 = DDR5.</summary>
    public static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        _ => "",
    };

    /// <summary>
    /// Dice se la RAM sembra girare alla velocità base invece che al suo profilo (XMP, EXPO, DOCP).
    /// È indicativo: Windows non legge il profilo XMP, legge solo la velocità dichiarata e quella configurata.
    /// </summary>
    public static CheckStatus RamSpeed(int ratedMtps, int configuredMtps, int smbiosType)
    {
        if (configuredMtps <= 0)
        {
            return CheckStatus.Info;
        }

        if (ratedMtps > 0 && configuredMtps < ratedMtps)
        {
            return CheckStatus.Warning;
        }

        // Velocità base JEDEC tipiche: chi ha un kit da gaming di solito ha un profilo più alto.
        return smbiosType switch
        {
            26 when configuredMtps <= 2666 => CheckStatus.Warning,
            34 when configuredMtps <= 4800 => CheckStatus.Warning,
            _ => CheckStatus.Ok,
        };
    }

    /// <summary>Un driver più vecchio di 12 mesi merita uno sguardo; non vuol dire che sia sbagliato.</summary>
    public static CheckStatus DriverAge(DateTime? driverDate, DateTime today)
    {
        if (driverDate is null)
        {
            return CheckStatus.Info;
        }

        return driverDate.Value < today.AddMonths(-12) ? CheckStatus.Warning : CheckStatus.Ok;
    }

    /// <summary>Spazio libero sul disco di sistema: sotto il 10% è un problema, sotto il 15% un avviso.</summary>
    public static CheckStatus FreeSpace(long freeBytes, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return CheckStatus.Info;
        }

        double ratio = (double)freeBytes / totalBytes;
        if (ratio < 0.10)
        {
            return CheckStatus.Problem;
        }

        return ratio < 0.15 ? CheckStatus.Warning : CheckStatus.Ok;
    }

    /// <summary>Frequenza del monitor: avviso se quella attuale è sotto la massima disponibile alla stessa risoluzione.</summary>
    public static CheckStatus RefreshRate(int current, int max)
    {
        if (current <= 1 || max <= 1)
        {
            return CheckStatus.Info;
        }

        return current < max ? CheckStatus.Warning : CheckStatus.Ok;
    }

    /// <summary>Converte una data WMI (CIM_DATETIME, es. 20240115000000.000000-000) in DateTime.</summary>
    public static DateTime? ParseWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return null;
        }

        if (int.TryParse(value.AsSpan(0, 4), out int y)
            && int.TryParse(value.AsSpan(4, 2), out int m)
            && int.TryParse(value.AsSpan(6, 2), out int d)
            && y > 1900 && m is >= 1 and <= 12 && d is >= 1 and <= 31)
        {
            try
            {
                return new DateTime(y, m, d);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Byte in GB leggibili.</summary>
    public static string Gigabytes(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.#} GB";
}
