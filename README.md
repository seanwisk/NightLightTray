# Night Light Tray

Run `artifacts\tray-win-x64\NightLightTray.exe` from a folder where you want to keep it. The standalone x64 executable runs in the notification area. The moon is outlined when Night Light is off and filled when it is on. If the icon is under the taskbar's overflow arrow, drag it into the visible tray for easy wheel access.

- Scroll up for warmer light or down for cooler light. One detent changes warmth by five percentage points. While Night Light is off, scrolling sets the warmth for the next time it turns on.
- Left-click the moon to toggle Night Light. Right-click for the current warmth, toggle, **Launch at startup**, **Open Night Light settings**, and **Exit**.
- Launch at startup is off by default. It uses the current executable path. If you move the file, turn the option off and back on from the new location.

The app changes Windows 11’s native Night Light through private CloudStore records in the signed-in user's registry. The format and display refresh behavior can change with Windows updates. The Settings shortcut is available when native controls are unavailable.

## Verification

On Windows 11 25H2 build 26200.9711, the guided console probe visibly changed strength and toggled Night Light. After restarting Explorer, a plain registry write stored the new strength while the display stayed at its previous warmth. A fresh post-restart run using native preview passed. The tray uses that short native preview when Night Light is on and stores the strength directly when it is off.

The tray integration checks exercised its input callbacks, five-point steps and limits, the context menu, registry notifications, startup registration and removal, icon registration after a simulated taskbar rebuild, and clean exit. The packaged executable ran with no main window on the same PC, which has .NET 8 installed system-wide. Over a 6.1-second idle sample it used no measurable CPU time, 42.5 MiB of working set, and 10.3 MiB of private memory.

For a desktop smoke test, run the tray app, scroll one detent over its visible moon icon, and confirm the tooltip changes by five points. Scroll elsewhere and verify the warmth stays the same. Check that left-click toggles, right-click opens the menu, Settings changes update the tooltip, the menu follows the Windows theme, and the icon returns after restarting Explorer. Use **Exit** to close the app.

## Console probe

From `artifacts\win-x64` in PowerShell:

```powershell
.\NightLightProbe.exe status
.\NightLightProbe.exe verify
```

Run away from a scheduled Night Light transition. Keep Windows Settings closed and look at the same white or gray area of the physical screen during each display check. Open Settings only when the final prompt tells you to check that Night Light is on, Strength is 50, and the schedule matches the original. Close Settings without touching the controls before answering that prompt. Answer `y` when the check passes, or `n` if it fails or you are unsure. The probe restores the original strength and on/off state when it finishes or stops early. If interrupted before cleanup, restore the values printed at the start in Windows Settings.

If the normal strength write changes the stored value and the physical display stays the same, repeat with `verify --preview`. It briefly uses Windows' native preview flag while Night Light is on. If a preview is interrupted and tint remains, move and release the native Strength slider.

To reproduce the Explorer restart test, restart Windows Explorer from Task Manager and then run `verify --preview`. Judge the physical screen at each step.

For individual operations:

```powershell
.\NightLightProbe.exe set-strength 50
.\NightLightProbe.exe toggle
```

The probe uses a linear warmth scale from 6500 K at 0% to 1200 K at 100%. When Night Light is off, changing warmth stores a value for the next enable. The probe leaves the Windows schedule intact.

## Build

With the .NET 10 SDK, run from this folder:

```powershell
dotnet publish .\Tray -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o .\artifacts\tray-win-x64
dotnet publish .\NightLightProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o .\artifacts\win-x64
```

## Windows controls

Use the Night Light tile in **Win+A** to toggle it and open `ms-settings:nightlight` for the Strength slider. [Microsoft Night Light controls](https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-display-brightness-and-color-in-windows)

[CloudStore format](https://github.com/kvnxiao/win-nightlight-cli/blob/main/docs/nightlight-registry-format.md) · [Native refresh and preview observations](https://github.com/kvnxiao/win-nightlight-cli/blob/main/docs/color-temperature-transitions.md)
