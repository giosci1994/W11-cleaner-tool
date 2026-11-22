using System.Diagnostics;
using System.Text;

namespace W11CleanerTool.Core;

/// <summary>
/// Avvia un programma esterno leggendo stdout e stderr in modo asincrono (niente blocchi se uno dei due si riempie),
/// con un tempo massimo: allo scadere chiude l'intero albero dei processi e restituisce un errore chiaro.
/// </summary>
internal static class ProcessRunner
{
    public static (int ExitCode, string Output, string Error) Run(
        string fileName,
        string arguments,
        Encoding outputEncoding,
        TimeSpan timeout)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = outputEncoding,
                    StandardErrorEncoding = outputEncoding,
                },
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (stdout)
                    {
                        stdout.AppendLine(e.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (stderr)
                    {
                        stderr.AppendLine(e.Data);
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Il processo è già finito nel frattempo.
                }

                return (-1, Snapshot(stdout), $"{Path.GetFileName(fileName)} non ha risposto entro {timeout.TotalSeconds:0} secondi ed è stato interrotto.");
            }

            // Senza argomenti aspetta anche la fine delle letture asincrone.
            process.WaitForExit();
            return (process.ExitCode, Snapshot(stdout), Snapshot(stderr));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, "", $"Impossibile avviare {Path.GetFileName(fileName)}: {ex.Message}");
        }
    }

    private static string Snapshot(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString();
        }
    }

    /// <summary>Codifica della console OEM di Windows (quella che usano powercfg e gli altri comandi quando l'uscita è rediretta).</summary>
    public static Encoding GetOemConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = OperatingSystem.IsWindows() ? (int)NativeMethods.GetOEMCP() : 0;
            if (codePage <= 0)
            {
                codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            }

            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return Encoding.UTF8;
        }
    }
}
