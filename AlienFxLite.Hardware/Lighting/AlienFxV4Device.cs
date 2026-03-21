using System.Diagnostics;
using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Hid;
using Microsoft.Win32.SafeHandles;

namespace AlienFxLite.Hardware.Lighting;

internal sealed class AlienFxV4Device : IDisposable
{
    private static readonly byte[] CommandControl = [6, 0x03, 0x21, 0x00, 0x03, 0x00, 0xff];
    private static readonly byte[] CommandColorSelect = [5, 0x03, 0x23, 0x01, 0x00, 0x01];
    private static readonly byte[] CommandColorSet = [7, 0x03, 0x24, 0x00, 0x07, 0xd0, 0x00, 0xfa];
    private static readonly byte[] CommandSetOneColor = [2, 0x03, 0x27];
    private static readonly byte[] CommandBrightness = [2, 0x03, 0x26];
    private static readonly byte[] V4OpCodes = [0xd0, 0xdc, 0xcf, 0xdc, 0x82, 0xac, 0xe8];

    private readonly SafeFileHandle _handle;
    private readonly int _reportLength;

    public AlienFxV4Device(SafeFileHandle handle, string devicePath, int reportLength)
    {
        _handle = handle;
        DevicePath = devicePath;
        _reportLength = reportLength;
    }

    public string DevicePath { get; }

    public static AlienFxV4Device? Open(ushort vendorId, ushort productId, out string? error)
    {
        foreach (HidDeviceInfo device in HidNative.EnumerateDevices())
        {
            if (device.VendorId != vendorId || device.ProductId != productId || device.OutputReportLength != 34)
            {
                continue;
            }

            SafeFileHandle handle = HidNative.OpenHandle(device.DevicePath);
            if (handle.IsInvalid)
            {
                continue;
            }

            error = null;
            return new AlienFxV4Device(handle, device.DevicePath, device.OutputReportLength);
        }

        error = $"Lighting device VID_{vendorId:X4}/PID_{productId:X4} not found.";
        return null;
    }

    public bool ApplyLight(ZoneLightingState state, byte lightId, out string? error)
    {
        error = null;
        if (!PrepareAndSend(
                CommandColorSelect,
                [
                    new CommandMod(5, [1]),
                    new CommandMod(6, [lightId]),
                ],
                out error))
        {
            return false;
        }

        List<LightPhase> phases = BuildPhases(state);
        List<CommandMod> mods = [];
        for (int phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
        {
            int offset = 3 + (phaseIndex * 8);
            LightPhase phase = phases[phaseIndex];
            mods.Add(new CommandMod(
                offset,
                [
                    phase.Type,
                    phase.Time,
                    V4OpCodes[phase.Type],
                    0,
                    phase.Tempo,
                    phase.Color.R,
                    phase.Color.G,
                    phase.Color.B,
                ]));
        }

        return PrepareAndSend(CommandColorSet, mods, out error);
    }

    public bool ApplyStaticZones(RgbColor color, IReadOnlyList<byte> zoneIds, out string? error)
    {
        error = null;
        if (zoneIds.Count == 0)
        {
            error = "At least one lighting zone is required.";
            return false;
        }

        List<byte> payload =
        [
            color.R,
            color.G,
            color.B,
            0,
            (byte)zoneIds.Count,
        ];
        payload.AddRange(zoneIds);

        return PrepareAndSend(CommandSetOneColor, [new CommandMod(3, payload.ToArray())], out error);
    }

    public bool Reset(out string? error)
    {
        error = null;
        if (!WaitForReady())
        {
            error = "Lighting controller did not report ready state.";
            return false;
        }

        if (!PrepareAndSend(CommandControl, [new CommandMod(4, [4])], out error))
        {
            return false;
        }

        return PrepareAndSend(CommandControl, [new CommandMod(4, [1])], out error);
    }

    public bool BeginSavedLightingBlock(byte blockId, out string? error)
    {
        error = null;
        if (!UpdateColors(out error))
        {
            return false;
        }

        if (!PrepareAndSend(CommandControl, [new CommandMod(4, [4, 0, blockId])], out error))
        {
            return false;
        }

        return PrepareAndSend(CommandControl, [new CommandMod(4, [1, 0, blockId])], out error);
    }

    public bool CommitSavedLightingBlock(byte blockId, out string? error)
    {
        error = null;
        if (!PrepareAndSend(CommandControl, [new CommandMod(4, [2, 0, blockId])], out error))
        {
            return false;
        }

        return PrepareAndSend(CommandControl, [new CommandMod(4, [6, 0, blockId])], out error);
    }

    public bool SetStartupLightingBlock(byte blockId, out string? error) =>
        PrepareAndSend(CommandControl, [new CommandMod(4, [7, 0, blockId])], out error);

    public bool UpdateColors(out string? error) =>
        PrepareAndSend(CommandControl, [], out error);

    public bool SetBrightness(int brightnessPercent, IReadOnlyList<byte> lightIds, out string? error)
    {
        int clamped = Math.Clamp(brightnessPercent, 0, 100);
        List<CommandMod> mods =
        [
            new CommandMod(3, [(byte)(100 - clamped), 0, (byte)lightIds.Count]),
            new CommandMod(6, lightIds.ToArray()),
        ];

        return PrepareAndSend(CommandBrightness, mods, out error);
    }

    public bool WaitForReady()
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            int status = GetDeviceStatus();
            if (status == 33 || status == 35 || status == 36 || status == 38)
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private List<LightPhase> BuildPhases(ZoneLightingState state)
    {
        byte tempo = MapTempo(state.Speed);
        const byte time = 0x07;

        return state.Effect switch
        {
            LightingEffect.Pulse =>
            [
                new LightPhase((byte)LightingEffect.Pulse, time, tempo, state.PrimaryColor),
                new LightPhase((byte)LightingEffect.Pulse, time, tempo, new RgbColor(0, 0, 0)),
            ],
            LightingEffect.Morph =>
            [
                new LightPhase((byte)LightingEffect.Morph, time, tempo, state.PrimaryColor),
                new LightPhase((byte)LightingEffect.Morph, time, tempo, state.SecondaryColor ?? new RgbColor(0, 0, 0)),
            ],
            _ => [new LightPhase((byte)LightingEffect.Static, time, 0xfa, state.PrimaryColor)],
        };
    }

    private static byte MapTempo(int speed)
    {
        int clamped = Math.Clamp(speed, 0, 100);
        return (byte)Math.Clamp(0xF0 - ((clamped * 0xE0) / 100), 0x10, 0xF0);
    }

    private int GetDeviceStatus()
    {
        byte[] buffer = new byte[_reportLength];
        buffer[0] = 0;
        if (!HidNative.HidD_GetInputReport(_handle, buffer, buffer.Length))
        {
            return 0;
        }

        return buffer[2];
    }

    private bool PrepareAndSend(byte[] command, IReadOnlyList<CommandMod> mods, out string? error)
    {
        error = null;
        byte[] buffer = new byte[_reportLength];
        Array.Copy(command, buffer, Math.Min(command[0] + 1, command.Length));
        buffer[0] = 0;

        foreach (CommandMod mod in mods)
        {
            Array.Copy(mod.Value, 0, buffer, mod.Offset, mod.Value.Length);
        }

        if (!HidNative.HidD_SetOutputReport(_handle, buffer, buffer.Length))
        {
            error = $"Failed to send lighting report to {DevicePath}.";
            return false;
        }

        return true;
    }

    private sealed record CommandMod(int Offset, byte[] Value);

    private sealed record LightPhase(byte Type, byte Time, byte Tempo, RgbColor Color);
}
