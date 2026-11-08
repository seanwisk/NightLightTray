using System.Globalization;
using System.Security;
using NightLight;

const string usage = """
    NightLightProbe status
    NightLightProbe set-strength <0–100> [--preview]
    NightLightProbe toggle
    NightLightProbe verify [--preview]
    """;

var native = new NativeNightLight();
try
{
    switch (args)
    {
        case ["status"]:
            Print(native.ReadStatus());
            return 0;
        case ["toggle"]:
            Print(native.Toggle());
            return 0;
        case ["set-strength", var value, .. var options] when IsPreviewOption(options):
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int strength)
                || strength is < 0 or > 100)
            {
                Console.Error.WriteLine("Strength must be an integer between 0 and 100.");
                return 2;
            }
            Print(native.SetStrength(strength, options.Length != 0));
            return 0;
        case ["verify", .. var options] when IsPreviewOption(options):
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Run verify in an interactive console where you can see the physical display.");
                return 2;
            }
            if (!Verify(native, options.Length != 0))
                return 1;
            Console.WriteLine("PASS. The display and Windows Settings checks passed for this run.");
            return 0;
        case [] or ["--help"]:
            Console.WriteLine(usage);
            return 0;
        default:
            Console.Error.WriteLine(usage);
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
    or UnauthorizedAccessException or SecurityException)
{
    Console.Error.WriteLine($"Night Light unavailable or unverified: {ex.Message}");
    return 1;
}

static bool IsPreviewOption(string[] options) => options is [] or ["--preview"];

static void Print(NativeNightLight.Status status)
{
    string schedule = status.ScheduleEnabled
        ? status.SunsetSchedule ? "sunset to sunrise" : "set hours"
        : "off";
    string temperature = status.StoredKelvin is null
        ? $"default, interpreted as {status.Kelvin} K"
        : $"{status.Kelvin} K";
    Console.WriteLine($"Night Light {(status.Enabled ? "on" : "off")} | Warmth {status.Strength}% ({temperature}) | Schedule {schedule} | Preview {(status.Previewing ? "on" : "off")}");
}

static bool Verify(NativeNightLight native, bool preview)
{
    var original = native.ReadStatus();
    Console.WriteLine("Original state");
    Print(original);
    Console.WriteLine("Run away from a scheduled transition. Look at a white or gray area on the physical screen.");
    Console.WriteLine("Keep Windows Settings closed for the display checks. The last step tells you when to open it.");
    Console.WriteLine("Answer y only if the stated check passes. Answer n if it fails or you are unsure. The probe will restore your original settings.");
    if (!Confirm("Is Windows Settings closed and are you ready to start?"))
        return false;

    try
    {
        int firstStrength = original.Strength < 50 ? 75 : 25;
        int secondStrength = 100 - firstStrength;
        if (!original.Enabled)
        {
            // Make the first enable observable even if the old strength was 0%.
            native.SetStrength(secondStrength, preview);
            var initialOn = native.Toggle();
            Print(initialOn);
            if (!initialOn.Enabled || initialOn.Strength != secondStrength
                || !Confirm($"Night Light switched from off to on at {secondStrength}%. Did the white or gray area become more yellow or orange?"))
                return false;
        }

        int previousStrength = original.Enabled ? original.Strength : secondStrength;
        var first = native.SetStrength(firstStrength, preview);
        Print(first);
        if (!first.Enabled || first.Strength != firstStrength
            || !ConfirmStrengthChange(previousStrength, firstStrength))
            return false;

        var second = native.SetStrength(secondStrength, preview);
        Print(second);
        if (!second.Enabled || second.Strength != secondStrength
            || !ConfirmStrengthChange(firstStrength, secondStrength))
            return false;

        if (native.Toggle().Enabled || !Confirm($"Night Light switched off from {secondStrength}%. Did the yellow or orange tint disappear?"))
            return false;

        var off = native.SetStrength(50, preview);
        if (off.Enabled || off.Strength != 50
            || !Confirm("The probe stored 50% while Night Light was off. Did the screen stay untinted after this change?"))
            return false;

        var enabled = native.Toggle();
        if (!enabled.Enabled || enabled.Strength != 50
            || !Confirm("Night Light switched back on at 50%. Did the yellow or orange tint return? The next step checks the exact value in Settings."))
            return false;

        if (enabled.ScheduleEnabled != original.ScheduleEnabled || enabled.SunsetSchedule != original.SunsetSchedule)
            throw new InvalidOperationException("The schedule changed during the test.");

        Console.WriteLine();
        Console.WriteLine("Open Windows Settings now. Go to System > Display > Night light.");
        Console.WriteLine("  1. Night Light is on. The button should say Turn off now.");
        Console.WriteLine("  2. Strength is 50, halfway along the slider. Hover over the slider thumb to see the value if needed.");
        if (original.ScheduleEnabled)
        {
            string mode = original.SunsetSchedule ? "Sunset to sunrise" : "Set hours";
            Console.WriteLine($"  3. Schedule night light is on and still set to {mode}.");
        }
        else
        {
            Console.WriteLine("  3. Schedule night light is off.");
        }
        return Confirm("Close Settings without changing any controls, then return here. Did all three checks match?");
    }
    finally
    {
        try
        {
            if (!original.Enabled && native.ReadStatus().Enabled)
                native.Toggle();
            native.SetTemperature(original.StoredKelvin, preview);
        }
        finally
        {
            if (native.ReadStatus().Enabled != original.Enabled)
                native.Toggle();
        }

        var restored = native.ReadStatus();
        if (restored != original)
            throw new InvalidOperationException("The original settings could not be confirmed. Restore them in Windows Settings.");
        Console.WriteLine("Original strength and state restored.");
    }
}

static bool ConfirmStrengthChange(int from, int to)
{
    string expected = to > from ? "more yellow or orange" : "less yellow or orange";
    return Confirm($"Night Light stayed on and strength changed from {from}% to {to}%. Did the same white or gray area become {expected}?");
}

static bool Confirm(string prompt)
{
    Console.Write($"{prompt} [y/N] ");
    return string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
}
