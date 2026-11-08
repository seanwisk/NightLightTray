using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NightLight;

internal sealed class TrayContext : ApplicationContext
{
    private const string Title = "Night Light Tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValue = "NightLightTray";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int TrayMessage = Shell.AppMessage + 1;
    private const int WheelMessage = Shell.AppMessage + 2;
    private const int RegistryMessage = Shell.AppMessage + 3;
    private const int WheelDelta = 120;
    private const int IconId = 1;

    private readonly NativeNightLight backend = new();
    private readonly Queue<int> commands = new(); // 0 toggles. Other values are signed detents.
    private readonly TrayWindow window;
    private readonly int taskbarCreated = Shell.RegisterWindowMessage("TaskbarCreated");
    private readonly Shell.MouseCallback mouseCallback;
    private readonly Shell.IconIdentifier identifier;
    private RegistryWatch? watch;
    private Shell.IconData iconData;
    private nint hook;
    private uint shellProcess;
    private Color iconColor;
    private bool iconFilled;
    private ContextMenuStrip? menu;
    private ToolStripMenuItem? statusItem;
    private ToolStripMenuItem? toggleItem;
    private NativeNightLight.Status? status;
    private string? nativeError;
    private string? watchError;
    private string? wheelError;
    private int wheelRemainder;
    private bool added, refresh, busy, closing;

    internal TrayContext()
    {
        mouseCallback = OnMouse;
        window = new TrayWindow(this);
        identifier = new Shell.IconIdentifier { Size = (uint)Marshal.SizeOf<Shell.IconIdentifier>(),
            Window = window.Handle, Id = IconId };
        iconData = new Shell.IconData
        {
            Size = (uint)Marshal.SizeOf<Shell.IconData>(), Window = window.Handle, Id = IconId,
            Flags = Shell.IconFlags, Callback = TrayMessage, Tip = Title, Info = "", InfoTitle = "", Version = 4
        };
        try
        {
            UpdateIconTheme();
            AddIcon();
            if (!added)
                throw new InvalidOperationException("Windows could not add the Night Light icon to the notification area.");
            hook = Shell.InstallHook(Shell.LowLevelMouse, mouseCallback, Shell.GetModuleHandle(null), 0);
            if (hook == 0)
                wheelError = "Mouse-wheel control is unavailable.";
            StartWatch();
            RequestRefresh();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void StartWatch()
    {
        if (watch is not null)
            return;
        nint handle = window.Handle;
        try
        {
            watch = new RegistryWatch(error => Shell.PostMessage(handle, RegistryMessage, error, 0));
            watchError = null;
        }
        catch (Exception ex) when (Program.IsExpected(ex))
        {
            watchError = "Automatic status updates are unavailable.";
        }
    }

    private void AddIcon()
    {
        iconData.Version = 4;
        added = Shell.NotifyIcon(Shell.Add, ref iconData) || Shell.NotifyIcon(Shell.Modify, ref iconData);
        if (added)
        {
            Shell.NotifyIcon(Shell.SetVersion, ref iconData);
            shellProcess = 0;
            nint shellWindow = Shell.GetShellWindow();
            if (shellWindow != 0)
                Shell.GetWindowThreadProcessId(shellWindow, out shellProcess);
        }
    }

    private void UpdateIconTheme()
    {
        Color color = SystemInformation.HighContrast ? SystemColors.WindowText
            : Registry.GetValue($@"HKEY_CURRENT_USER\{Personalize}", "SystemUsesLightTheme", 0) is 1
                ? Color.Black : Color.White;
        UpdateIcon(color);
    }

    private void UpdateIcon(Color color)
    {
        bool filled = status is { Enabled: true };
        if (iconData.Icon != 0 && iconColor == color && iconFilled == filled)
            return;

        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var moon = new GraphicsPath())
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            moon.AddBezier(18, 4, 4, 2, -2, 23, 12, 28);
            moon.AddBezier(12, 28, 20, 31, 27, 26, 29, 19);
            moon.AddBezier(29, 19, 18, 24, 12, 12, 18, 4);
            moon.CloseFigure();
            if (filled)
            {
                using var brush = new SolidBrush(color);
                graphics.FillPath(brush, moon);
            }
            else
            {
                using var pen = new Pen(color, 2.5f) { LineJoin = LineJoin.Round };
                graphics.DrawPath(pen, moon);
            }
        }
        nint old = iconData.Icon;
        iconData.Icon = bitmap.GetHicon();
        iconColor = color;
        iconFilled = filled;
        if (added)
            Shell.NotifyIcon(Shell.Modify, ref iconData);
        if (old != 0)
            Shell.DestroyIcon(old);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message == Shell.MouseWheel && !closing && added && menu?.Visible != true)
        {
            var mouse = Marshal.PtrToStructure<Shell.MouseEvent>(data);
            if (Shell.Shell_NotifyIconGetRect(in identifier, out var bounds) == 0
                && bounds.Contains(mouse.Point) && IsOverShell(mouse.Point))
            {
                short delta = unchecked((short)(mouse.Data >> 16));
                Shell.PostMessage(window.Handle, WheelMessage, delta, 0);
            }
            else
            {
                wheelRemainder = 0;
            }
        }
        return Shell.CallNextHookEx(hook, code, message, data);
    }

    private bool IsOverShell(Shell.ScreenPoint point)
    {
        nint hit = Shell.WindowFromPoint(point);
        return hit != 0 && Shell.GetWindowThreadProcessId(hit, out uint process) != 0 && process == shellProcess;
    }

    private void OnWheel(int delta)
    {
        if (status is not { Previewing: false } || closing)
            return;
        wheelRemainder += delta;
        int detents = wheelRemainder / WheelDelta;
        wheelRemainder %= WheelDelta;
        if (detents != 0)
            Enqueue(detents);
    }

    private void Enqueue(int command)
    {
        if (closing || status is not { Previewing: false })
            return;
        commands.Enqueue(command);
        _ = PumpAsync();
    }

    private void RequestRefresh()
    {
        refresh = true;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (busy || closing)
            return;

        busy = true;
        try
        {
            while (!closing && (refresh || commands.Count != 0))
            {
                refresh = false;
                bool write = commands.TryDequeue(out int command);
                // Coalesce consecutive detents in one direction to limit CloudStore writes.
                while (write && command != 0 && commands.TryPeek(out int next) && Math.Sign(next) == Math.Sign(command))
                    command += commands.Dequeue();

                int detents = command;
                try
                {
                    status = await Task.Run(() => write ? Apply(detents) : backend.ReadStatus(requireWritable: true));
                    nativeError = null;
                }
                catch (Exception ex) when (Program.IsExpected(ex))
                {
                    status = null;
                    nativeError = ex.Message;
                    commands.Clear();
                    wheelRemainder = 0;
                }
                UpdateStatus();
            }
        }
        finally
        {
            busy = false;
            if (closing)
                ExitThread();
        }
    }

    private NativeNightLight.Status Apply(int detents)
    {
        if (detents == 0)
            return backend.Toggle();

        var latest = backend.ReadStatus();
        int strength = Math.Clamp(latest.Strength + detents * 5, 0, 100);
        return strength == latest.Strength ? latest : backend.SetStrength(strength, usePreview: true);
    }

    private void UpdateStatus()
    {
        UpdateIcon(iconColor);
        string caption = status is { } current
            ? $"Night Light {(current.Enabled ? "on" : "off")} · Warmth {current.Strength}%"
            : "Night Light unavailable";
        if (statusItem is not null && toggleItem is not null)
        {
            statusItem.Text = caption;
            statusItem.ToolTipText = nativeError ?? watchError ?? wheelError ?? "";
            toggleItem.Text = status is { Enabled: true } ? "Turn off" : "Turn on";
            toggleItem.Enabled = status is { Previewing: false };
        }
        if (caption != iconData.Tip)
        {
            iconData.Tip = caption;
            if (added)
                Shell.NotifyIcon(Shell.Modify, ref iconData);
        }
    }

    private void ShowMenu()
    {
        if (closing || menu?.Visible == true)
            return;

        // WinForms picks up the current system color mode when creating the new menu.
        Application.SetColorMode(SystemColorMode.System);
        menu?.Dispose();
        menu = new ContextMenuStrip { ShowItemToolTips = true };
        statusItem = new ToolStripMenuItem { Enabled = false };
        toggleItem = new ToolStripMenuItem("Turn on", null, (_, _) => Enqueue(0));
        var startup = new ToolStripMenuItem("Launch at startup", null, (_, _) => TryAction(ToggleStartup));
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            startup.Checked = run?.GetValue(StartupValue) is string;
        }
        catch (Exception ex) when (Program.IsExpected(ex))
        {
            startup.Enabled = false;
            startup.ToolTipText = ex.Message;
        }
        menu.Items.AddRange([statusItem, new ToolStripSeparator(), toggleItem, startup,
            new ToolStripMenuItem("Open Night Light settings", null, (_, _) => TryAction(OpenSettings)),
            new ToolStripSeparator(), new ToolStripMenuItem("Exit", null, (_, _) => BeginExit())]);
        menu.Closed += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.Keyboard && added)
                Shell.NotifyIcon(Shell.SetFocus, ref iconData);
        };
        UpdateStatus();
        StartWatch();
        RequestRefresh();
        Shell.SetForegroundWindow(window.Handle);
        Point anchor = Shell.Shell_NotifyIconGetRect(in identifier, out var bounds) == 0
            ? new Point(bounds.Left, bounds.Top) : Cursor.Position;
        menu.Show(anchor, ToolStripDropDownDirection.AboveRight);
    }

    private static void ToggleStartup()
    {
        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (run.GetValue(StartupValue) is string)
            run.DeleteValue(StartupValue, throwOnMissingValue: false);
        else
        {
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the executable.");
            string command = $"\"{path}\"";
            if (command.Length > 260)
                throw new InvalidOperationException("Move the executable to a shorter path to use launch at startup.");
            run.SetValue(StartupValue, command, RegistryValueKind.String);
        }
    }

    private static void OpenSettings()
    {
        using Process? process = Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true });
    }

    private static void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (Program.IsExpected(ex))
        {
            MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BeginExit()
    {
        closing = true;
        commands.Clear();
        if (!busy)
            ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            watch?.Dispose();
            if (hook != 0)
                Shell.UnhookWindowsHookEx(hook);
            if (added)
                Shell.NotifyIcon(Shell.Delete, ref iconData);
            if (iconData.Icon != 0)
                Shell.DestroyIcon(iconData.Icon);
            menu?.Dispose();
            window.DestroyHandle();
        }
        base.Dispose(disposing);
    }

    private sealed class TrayWindow : NativeWindow
    {
        private readonly TrayContext owner;

        internal TrayWindow(TrayContext owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = Title, Style = unchecked((int)0x80000000), ExStyle = 0x80 });
        }

        protected override void WndProc(ref Message message)
        {
            if (owner.window is null)
            {
                base.WndProc(ref message);
                return;
            }

            switch (message.Msg)
            {
                case TrayMessage:
                    switch ((int)message.LParam & 0xffff)
                    {
                        case Shell.LeftButtonUp:
                        case Shell.KeySelect:
                            owner.Enqueue(0);
                            break;
                        case Shell.ContextMenu:
                            owner.ShowMenu();
                            break;
                    }
                    return;
                case WheelMessage:
                    owner.OnWheel((int)message.WParam);
                    return;
                case RegistryMessage:
                    if (message.WParam != 0)
                    {
                        owner.watch?.Dispose();
                        owner.watch = null;
                        owner.watchError = "Automatic status updates are unavailable.";
                    }
                    owner.RequestRefresh();
                    return;
                case Shell.SettingChange:
                case Shell.ThemeChanged:
                case Shell.SysColorChange:
                    TryAction(owner.UpdateIconTheme);
                    break;
                default:
                    if (message.Msg != 0 && message.Msg == owner.taskbarCreated && !owner.closing)
                    {
                        owner.AddIcon();
                        owner.StartWatch();
                        owner.RequestRefresh();
                    }
                    break;
            }
            base.WndProc(ref message);
        }
    }
}
