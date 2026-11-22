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

    /// <summary>Windows ha risposto senza errori ma non ha creato un punto nuovo (ne esiste già uno recente).</summary>
    RecentPointExists,
}

public sealed record RestorePointResult(
    RestorePointStatus Status,
    string Message,
    uint? ErrorCode = null);

/// <summary>Punto di ripristino letto da WMI (root\default, SystemRestore).</summary>
public sealed record RestorePointInfo(uint SequenceNumber, DateTime? CreationTimeLocal, string Description);

/// <summary>Fotografia dell'elenco dei punti di ripristino: <see cref="QueryOk"/> è false se WMI non ha risposto.</summary>
public sealed record RestorePointSnapshot(bool QueryOk, RestorePointInfo? Latest);

/// <summary>
/// Crea un punto di ripristino di Windows tramite WMI (root\default, SystemRestore.CreateRestorePoint).
/// Non attiva mai la Protezione sistema da sola: se è spenta o mancano i permessi, restituisce un esito chiaro.
/// </summary>
public sealed class RestorePointService
{
    private const int RestorePointTypeModifySettings = 12;
    private const int EventTypeBeginSystemChange = 100;
    private const int MaxDescriptionLength = 200;

    private readonly Func<string, RestorePointResult>? _createOverride;

    public RestorePointService()
    {
    }

    /// <summary>Solo per i test: sostituisce la creazione vera con un esito finto.</summary>
    internal RestorePointService(Func<string, RestorePointResult> createOverride)
    {
        _createOverride = createOverride ?? throw new ArgumentNullException(nameof(createOverride));
    }

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
        if (_createOverride is not null)
        {
            return _createOverride(description);
        }

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

        // Windows crea al massimo un punto ogni 24 ore (SystemRestorePointCreationFrequency, 1440 minuti):
        // se ne esiste già uno recente, CreateRestorePoint risponde 0 senza crearne uno nuovo.
        // Per questo si confronta il punto più recente prima e dopo la chiamata.
        // END_SYSTEM_CHANGE (101) non si usa: CreateRestorePoint via WMI non restituisce il numero di sequenza
        // e, come Checkpoint-Computer di PowerShell, BEGIN_SYSTEM_CHANGE da solo crea già un punto completo.
        RestorePointSnapshot before = ReadLatestRestorePoint();

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
            RestorePointResult interpreted = InterpretReturnCode(code);
            if (interpreted.Status != RestorePointStatus.Created)
            {
                return interpreted;
            }

            return EvaluateCreation(before, ReadLatestRestorePoint(), DateTime.Now);
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

    /// <summary>
    /// Decide se dopo una risposta 0 di CreateRestorePoint è nato davvero un punto nuovo,
    /// confrontando il punto più recente prima e dopo la chiamata.
    /// </summary>
    internal static RestorePointResult EvaluateCreation(RestorePointSnapshot before, RestorePointSnapshot after, DateTime nowLocal)
    {
        if (!after.QueryOk)
        {
            return new RestorePointResult(
                RestorePointStatus.Error,
                "Windows ha risposto senza errori, ma non riesco a leggere l'elenco dei punti di ripristino per verificare che il punto sia stato creato.",
                0);
        }

        if (after.Latest is null)
        {
            return new RestorePointResult(
                RestorePointStatus.Error,
                "Windows ha risposto senza errori, ma nell'elenco non c'è nessun punto di ripristino.",
                0);
        }

        bool isNew;
        if (before.QueryOk)
        {
            isNew = before.Latest is null || after.Latest.SequenceNumber > before.Latest.SequenceNumber;
        }
        else
        {
            // Senza l'elenco di prima si guarda la data: un punto nato negli ultimi 10 minuti è quello appena chiesto.
            isNew = after.Latest.CreationTimeLocal is DateTime created &&
                    (nowLocal - created).Duration() <= TimeSpan.FromMinutes(10);
        }

        string dateText = after.Latest.CreationTimeLocal is DateTime createdAt
            ? createdAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : "data sconosciuta";

        if (!isNew)
        {
            return new RestorePointResult(
                RestorePointStatus.RecentPointExists,
                $"Windows non ha creato un punto nuovo: ne esiste già uno di meno di 24 ore fa, del {dateText}.",
                0);
        }

        return new RestorePointResult(
            RestorePointStatus.Created,
            $"Punto di ripristino creato ({dateText}).",
            0);
    }

    /// <summary>Legge il punto di ripristino più recente (numero di sequenza più alto).</summary>
    public static RestorePointSnapshot ReadLatestRestorePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RestorePointSnapshot(false, null);
        }

        try
        {
            RestorePointInfo? latest = null;
            using var searcher = new ManagementObjectSearcher(
                @"root\default",
                "SELECT SequenceNumber, CreationTime, Description FROM SystemRestore");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    if (obj["SequenceNumber"] is null)
                    {
                        continue;
                    }

                    uint sequence = Convert.ToUInt32(obj["SequenceNumber"], CultureInfo.InvariantCulture);
                    DateTime? created = null;
                    if (obj["CreationTime"] is string dmtf && !string.IsNullOrWhiteSpace(dmtf))
                    {
                        try
                        {
                            created = ManagementDateTimeConverter.ToDateTime(dmtf);
                        }
                        catch (Exception ex) when (ex is ArgumentException or FormatException)
                        {
                            created = null;
                        }
                    }

                    string description = obj["Description"]?.ToString() ?? "";
                    if (latest is null || sequence > latest.SequenceNumber)
                    {
                        latest = new RestorePointInfo(sequence, created, description);
                    }
                }
            }

            return new RestorePointSnapshot(true, latest);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            return new RestorePointSnapshot(false, null);
        }
    }

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
