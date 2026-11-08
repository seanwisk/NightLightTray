using System.ComponentModel;
using Microsoft.Win32;

namespace NightLight;

internal sealed class RegistryWatch : IDisposable
{
    // Watch the common parent so missing or replaced Night Light keys can recover.
    private const uint Filter = 0x10000005; // Thread-agnostic, key names, and value changes.
    private readonly object gate = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly RegistryKey key;
    private readonly Action<int> changed;
    private RegisteredWaitHandle? wait;
    private bool disposed;

    internal RegistryWatch(Action<int> changed)
    {
        this.changed = changed;
        try
        {
            key = Registry.CurrentUser.OpenSubKey(NativeNightLight.Current)
                ?? throw new InvalidDataException("The Night Light registry store is missing.");
        }
        catch
        {
            signal.Dispose();
            throw;
        }
        int error;
        lock (gate)
            error = Arm();
        if (error != 0)
        {
            Dispose();
            throw new Win32Exception(error);
        }
    }

    private int Arm()
    {
        int error = Shell.RegNotifyChangeKeyValue(key.Handle, true, Filter, signal.SafeWaitHandle, true);
        if (error == 0)
            wait = ThreadPool.RegisterWaitForSingleObject(signal, OnChange, null, Timeout.Infinite, executeOnlyOnce: true);
        return error;
    }

    private void OnChange(object? _, bool timedOut)
    {
        lock (gate)
        {
            if (!disposed)
                changed(Arm());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            wait?.Unregister(null);
            key.Dispose();
            signal.Dispose();
        }
    }
}
