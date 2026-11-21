namespace W11CleanerTool.Core;

/// <summary>
/// Impedisce la sospensione del PC (es. durante download lunghi) finché è attivo,
/// senza cambiare nessuna impostazione permanente di Windows.
/// </summary>
public sealed class KeepAwakeService : IDisposable
{
    private readonly Func<NativeMethods.ExecutionState, NativeMethods.ExecutionState> _setState;
    private bool _disposed;

    public KeepAwakeService()
        : this(NativeMethods.SetThreadExecutionState)
    {
    }

    internal KeepAwakeService(Func<NativeMethods.ExecutionState, NativeMethods.ExecutionState> setState)
    {
        _setState = setState ?? throw new ArgumentNullException(nameof(setState));
    }

    public bool IsActive { get; private set; }

    /// <summary>Attiva la modalità «non sospendere» per il processo/thread corrente.</summary>
    public bool Enable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var previous = _setState(
            NativeMethods.ExecutionState.ES_CONTINUOUS |
            NativeMethods.ExecutionState.ES_SYSTEM_REQUIRED);

        bool succeeded = previous != 0;
        if (succeeded)
        {
            IsActive = true;
        }

        return succeeded;
    }

    /// <summary>Ripristina lo stato normale di sospensione del thread corrente.</summary>
    public bool Disable()
    {
        if (!IsActive)
        {
            return true;
        }

        var previous = _setState(NativeMethods.ExecutionState.ES_CONTINUOUS);
        IsActive = false;
        return previous != 0;
    }

    /// <summary>Attiva o disattiva in base a <paramref name="enabled"/>.</summary>
    public bool SetEnabled(bool enabled) => enabled ? Enable() : Disable();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disable();
        _disposed = true;
    }
}
