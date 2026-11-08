using Microsoft.Win32;
using Kind = NightLight.CompactBinary.Kind;

namespace NightLight;

internal sealed class NativeNightLight
{
    internal const string Current = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current";
    private const string Settings = "windows.data.bluelightreduction.settings";
    private const string State = "windows.data.bluelightreduction.bluelightreductionstate";
    private const string ValueName = "Data";
    private const int TemperatureField = 40;
    private const int PreviewField = 70;
    private const int CoolestKelvin = 6500;
    private const int WarmestKelvin = 1200;
    private const int TemperatureRange = CoolestKelvin - WarmestKelvin;

    private ulong lastSettingsWrite;
    private ulong lastStateWrite;

    internal readonly record struct Status(bool Enabled, int? StoredKelvin, bool Previewing,
        bool ScheduleEnabled, bool SunsetSchedule)
    {
        internal int Kelvin => StoredKelvin ?? CoolestKelvin;
        internal int Strength => (int)Math.Round(100.0 * (CoolestKelvin - Kelvin) / TemperatureRange);
    }

    internal Status ReadStatus(bool requireWritable = false)
    {
        using RegistryKey stateKey = Open(State, requireWritable);
        using RegistryKey settingsKey = Open(Settings, requireWritable);
        return ReadStatus(Read(stateKey), Read(settingsKey));
    }

    internal Status Toggle()
    {
        using RegistryKey stateKey = Open(State, writable: true);
        using RegistryKey settingsKey = Open(Settings, writable: false);
        Record state = Read(stateKey);
        Status status = ReadStatus(state, Read(settingsKey));
        if (status.Previewing)
            throw new InvalidOperationException("Release the Strength slider in Windows Settings before toggling.");

        if (status.Enabled)
            state.Payload.Remove(0);
        else
            state.Payload.SetNumber(0, Kind.Int32, 0);
        state.Payload.SetNumber(10, Kind.Int32, 2); // ZigZag encoding of manual transition (1).
        state.Payload.SetNumber(20, Kind.UInt64, (ulong)DateTime.UtcNow.ToFileTimeUtc());
        byte[] written = Write(stateKey, state, ref lastStateWrite);
        Verify(stateKey, written);
        return ReadStatus();
    }

    internal Status SetStrength(int strength, bool usePreview)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strength, 100);
        return SetTemperature(CoolestKelvin - strength * (TemperatureRange / 100), usePreview);
    }

    internal Status SetTemperature(int? kelvin, bool usePreview)
    {
        if (kelvin is < WarmestKelvin or > CoolestKelvin)
            throw new ArgumentOutOfRangeException(nameof(kelvin));

        using RegistryKey stateKey = Open(State, writable: false);
        using RegistryKey settingsKey = Open(Settings, writable: true);
        Record settings = Read(settingsKey);
        Status status = ReadStatus(Read(stateKey), settings);
        if (status.Previewing)
            throw new InvalidOperationException("Release the Strength slider in Windows Settings before setting warmth.");

        bool preview = usePreview && status.Enabled;
        if (!preview && status.StoredKelvin == kelvin)
            return status;

        if (kelvin is int temperature)
            settings.Payload.SetNumber(TemperatureField, Kind.Int16, (ulong)(temperature * 2));
        else
            settings.Payload.Remove(TemperatureField);

        if (preview)
            settings.Payload.SetBool(PreviewField, true);

        byte[] written = Write(settingsKey, settings, ref lastSettingsWrite);
        if (preview)
        {
            try
            {
                Thread.Sleep(50);
            }
            finally
            {
                written = EndPreview(settingsKey, written);
            }
        }

        Verify(settingsKey, written);
        return ReadStatus();
    }

    private byte[] EndPreview(RegistryKey key, byte[] written)
    {
        // Only clear the preview we started. Settings may be writing to the same record.
        Record current = Read(key);
        if (!current.Original.AsSpan().SequenceEqual(written))
            throw new InvalidOperationException("Windows changed the record during preview. If tint remains, move and release the native Strength slider.");

        current.Payload.Remove(PreviewField);
        try
        {
            return Write(key, current, ref lastSettingsWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException("Could not clear preview. Move and release the native Strength slider.", ex);
        }
    }

    private static Status ReadStatus(Record state, Record settings)
    {
        ulong? active = state.Payload.GetNumber(0, Kind.Int32);
        if (active is not (null or 0))
            throw new InvalidDataException("Unrecognized Night Light state.");
        if (!state.Payload.GetBool(30, true))
            throw new InvalidOperationException("Windows reports Night Light is unavailable on this device.");

        state.Payload.GetNumber(10, Kind.Int32);
        state.Payload.GetNumber(20, Kind.UInt64);
        ulong? encodedKelvin = settings.Payload.GetNumber(TemperatureField, Kind.Int16);
        int? kelvin = encodedKelvin is ulong value ? (int)(value >> 1) : null;
        if ((encodedKelvin.GetValueOrDefault() & 1) != 0 || kelvin is < WarmestKelvin or > CoolestKelvin)
            throw new InvalidDataException("Unrecognized Night Light color temperature.");

        return new(active.HasValue, kelvin, settings.Payload.GetBool(PreviewField, false),
            settings.Payload.GetBool(0, false), settings.Payload.GetBool(10, true));
    }

    private static RegistryKey Open(string record, bool writable) =>
        Registry.CurrentUser.OpenSubKey($@"{Current}\default${record}\{record}", writable)
        ?? throw new InvalidDataException($"Night Light record missing ({record}). Open Night Light in Windows Settings to initialize it.");

    private static byte[] ReadBytes(RegistryKey key) =>
        key.GetValue(ValueName) as byte[]
        ?? throw new InvalidDataException("Night Light Data is missing or has an unexpected registry type.");

    private static Record Read(RegistryKey key)
    {
        byte[] original = ReadBytes(key);
        CompactBinary envelope = CompactBinary.Parse(original);
        if (!envelope.GetStruct(0).GetBool(0, false))
            throw new InvalidDataException("Night Light record is marked invalid.");

        CompactBinary container = envelope.GetStruct(1);
        ulong modified = container.GetNumber(0, Kind.UInt64)
            ?? throw new InvalidDataException("Night Light record is missing its timestamp.");
        CompactBinary wrapper = container.GetStruct(1);
        CompactBinary payload = CompactBinary.Parse(wrapper.GetBlob(1));
        return new(original, envelope, container, wrapper, payload, modified);
    }

    private static byte[] Write(RegistryKey key, Record record, ref ulong lastWrite)
    {
        ulong latest = Math.Max(record.Modified, lastWrite);
        if (latest > ulong.MaxValue - 2)
            throw new InvalidDataException("Invalid Night Light record timestamp.");
        ulong modified = Math.Max((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), latest + 2);

        record.Wrapper.SetBlob(1, record.Payload.Encode());
        record.Container.SetNumber(0, Kind.UInt64, modified);
        record.Container.SetStruct(1, record.Wrapper);
        record.Envelope.SetStruct(1, record.Container);
        byte[] bytes = record.Envelope.Encode();

        if (!ReadBytes(key).AsSpan().SequenceEqual(record.Original))
            throw new InvalidOperationException("Windows changed Night Light before the write. Try again when the native slider is idle.");
        key.SetValue(ValueName, bytes, RegistryValueKind.Binary);
        lastWrite = modified;
        return bytes;
    }

    private static void Verify(RegistryKey key, byte[] written)
    {
        // Explorer can reject a write asynchronously even when SetValue succeeds.
        Thread.Sleep(150);
        if (!ReadBytes(key).AsSpan().SequenceEqual(written))
            throw new InvalidOperationException("The Night Light record changed after the write. Open Windows Settings to verify the current state.");
    }

    private sealed record Record(byte[] Original, CompactBinary Envelope, CompactBinary Container,
        CompactBinary Wrapper, CompactBinary Payload, ulong Modified);
}
