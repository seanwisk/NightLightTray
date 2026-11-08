using System.ComponentModel;
using System.Security;
using NightLight;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\NightLightTray.ef4afdf1-155a-4ff3-ae24-23d4cb6c7db1", out bool first);
        if (!first)
            return;

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        using var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            using var context = new TrayContext();
            Application.Run(context);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageBox.Show(ex.Message, "Night Light Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException
        or InvalidOperationException or UnauthorizedAccessException or SecurityException or Win32Exception;
}
