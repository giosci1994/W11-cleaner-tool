using Microsoft.Win32;

namespace W11CleanerTool.Core;

/// <summary>Letture del registro, solo lettura.</summary>
internal static class RegistryValues
{
    public static object? Read(RegistryHive hive, string path, string name)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static int? ReadInt(RegistryHive hive, string path, string name)
        => Read(hive, path, name) is int i ? i : null;

    public static bool KeyExists(RegistryHive hive, string path)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
