using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace W11CleanerTool.Core.Safety;

/// <summary>Esito del tentativo di creare un punto di ripristino di Windows.</summary>
public enum RestorePointStatus
{
    Created,
    Disabled,
    AccessDenied,
    Error,
}

public sealed record RestorePointResult(
    RestorePointStatus Status,
    string Message,
    uint? ErrorCode = null);

/// <summary>
/// Crea un punto di ripristino di Windows tramite WMI (root\default, SystemRestore.CreateRestorePoint).
/// Non attiva mai la Protezione sistema da sola: se è spenta o mancano i permessi, restituisce un esito chiaro.
/// </summary>
public sealed class RestorePointService
{
    private const int RestorePointTypeModifySettings = 12;
    private const int EventTypeBeginSystemChange = 100;
    private const int MaxDescriptionLength = 200;

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Prova a creare un punto di ripristino. Non attiva mai Protezione sistema da sola.
    /// </summary>
    public RestorePointResult Create(string description = "W11-cleaner-tool")
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RestorePointResult(RestorePointStatus.Error, "I punti di ripristino sono disponibili solo su Windows.");
        }

        if (!IsAdministrator())
        {
            return new RestorePointResult(
                RestorePointStatus.AccessDenied,
                "Servono i diritti di amministratore per creare un punto di ripristino.");
        }

        if (IsPolicyDisabled())
        {
            return new RestorePointResult(
                RestorePointStatus.Disabled,
                "La Protezione sistema è disattivata dalle impostazioni di sistema. Attivala manualmente da «Crea un punto di ripristino» se vuoi usarla.");
        }

        string safeDescription = string.IsNullOrWhiteSpace(description)
            ? "W11-cleaner-tool"
            : description.Trim();
        if (safeDescription.Length > MaxDescriptionLength)
        {
            safeDescription = safeDescription[..MaxDescriptionLength];
        }

        try
        {
            using var srClass = new ManagementClass(@"root\default", "SystemRestore", null);
            using ManagementBaseObject inParams = srClass.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = safeDescription;
            inParams["RestorePointType"] = RestorePointTypeModifySettings;
            inParams["EventType"] = EventTypeBeginSystemChange;

            using ManagementBaseObject? outParams = srClass.InvokeMethod("CreateRestorePoint", inParams, null);
            if (outParams is null || outParams["ReturnValue"] is null)
            {
                return new RestorePointResult(RestorePointStatus.Error, "WMI non ha restituito un esito per CreateRestorePoint.");
            }

            uint code = Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture);
            return InterpretReturnCode(code);
        }
        catch (UnauthorizedAccessException)
        {
            return new RestorePointResult(
                RestorePointStatus.AccessDenied,
                "Permessi insufficienti per creare il punto di ripristino.",
                5);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.AccessDenied or ManagementStatus.PrivilegeNotHeld)
        {
            return new RestorePointResult(
                RestorePointStatus.AccessDenied,
                "Permessi insufficienti per creare il punto di ripristino.",
                (uint)ex.ErrorCode);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.NotFound or ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace)
        {
            return new RestorePointResult(
                RestorePointStatus.Disabled,
                "La Protezione sistema non è disponibile o è spenta su questo PC.",
                (uint)ex.ErrorCode);
        }
        catch (COMException ex)
        {
            return InterpretHResult(unchecked((uint)ex.HResult), ex.Message);
        }
        catch (Exception ex)
        {
            return new RestorePointResult(RestorePointStatus.Error, "Errore durante la creazione del punto di ripristino: " + ex.Message);
        }
    }

    internal static RestorePointResult InterpretReturnCode(uint code) => code switch
    {
        0 => new RestorePointResult(RestorePointStatus.Created, "Punto di ripristino creato.", 0),
        5 => new RestorePointResult(RestorePointStatus.AccessDenied, "Servono i diritti di amministratore per creare un punto di ripristino.", code),
        1058 or 1060 => new RestorePointResult(
            RestorePointStatus.Disabled,
            "La Protezione sistema è spenta o il servizio Copia shadow è disattivato. Puoi attivarla da «Crea un punto di ripristino» in Windows.",
            code),
        16389 or 0x80004005 => IsSessionIntervalZero()
            ? new RestorePointResult(
                RestorePointStatus.Disabled,
                "La Protezione sistema sembra spenta sul disco di sistema. Puoi attivarla manualmente da «Crea un punto di ripristino» in Windows.",
                code)
            : new RestorePointResult(
                RestorePointStatus.Disabled,
                "Impossibile creare il punto di ripristino: verifica che la Protezione sistema sia attiva sul disco C:.",
                code),
        _ => new RestorePointResult(RestorePointStatus.Error, $"CreateRestorePoint ha restituito il codice {code}.", code),
    };

    internal static RestorePointResult InterpretHResult(uint hresult, string message) => hresult switch
    {
        0x80070005 => new RestorePointResult(RestorePointStatus.AccessDenied, "Permessi insufficienti per creare il punto di ripristino.", hresult),
        0x80070422 or 0x80070424 => new RestorePointResult(
            RestorePointStatus.Disabled,
            "La Protezione sistema o il servizio Copia shadow è disattivato.",
            hresult),
        0x80004005 => new RestorePointResult(
            RestorePointStatus.Disabled,
            "La Protezione sistema è spenta sul disco di sistema o non ha spazio assegnato.",
            hresult),
        _ => new RestorePointResult(RestorePointStatus.Error, $"Errore WMI ({hresult:X8}): {message}", hresult),
    };

    private static bool IsPolicyDisabled()
        => RegistryValues.ReadInt(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR") == 1;

    private static bool IsSessionIntervalZero()
        => RegistryValues.ReadInt(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval") == 0;
}
