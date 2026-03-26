using System.Runtime.InteropServices;
using AlienFxLite.Contracts;

namespace AlienFxLite.Hardware.Native;

internal static class AlienFxNativeBridge
{
    private const string LibraryName = "AlienFxLite.NativeBridge";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDeviceInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 192)]
        public string Description;

        public ushort VendorId;
        public ushort ProductId;
        public int ApiVersion;
        public ushort Reserved;
        public byte SupportsGlobalEffects;
        public byte SupportsBrightness;
        public byte SupportsPersistence;
        public byte Present;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLightAction
    {
        public byte LightId;
        public byte ActionType;
        public byte SpeedPercent;
        public byte Reserved;
        public uint PrimaryColor;
        public uint SecondaryColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGlobalEffect
    {
        public byte EffectType;
        public byte Mode;
        public byte ColorCount;
        public byte SpeedPercent;
        public uint PrimaryColor;
        public uint SecondaryColor;
    }

    [DllImport(LibraryName, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AfxLiteEnumerateDevices(
        [Out] NativeDeviceInfo[]? devices,
        int capacity,
        out int totalCount);

    [DllImport(LibraryName, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AfxLiteApplyLightActions(
        string deviceId,
        NativeLightAction[] actions,
        int actionCount,
        byte[]? brightnessLightIds,
        int brightnessLightIdCount,
        int brightnessPercent,
        int persistDefault,
        int includePowerLights);

    [DllImport(LibraryName, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AfxLiteApplyGlobalEffect(
        string deviceId,
        NativeGlobalEffect effect,
        int brightnessPercent);

    [DllImport(LibraryName, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AfxLiteGetLastError(
        [Out] char[]? buffer,
        int capacity);

    public static IReadOnlyList<NativeLightingDevice> EnumerateDevices()
    {
        ThrowIfFailed(AfxLiteEnumerateDevices(null, 0, out int totalCount));
        if (totalCount <= 0)
        {
            return [];
        }

        NativeDeviceInfo[] buffer = new NativeDeviceInfo[totalCount];
        ThrowIfFailed(AfxLiteEnumerateDevices(buffer, buffer.Length, out totalCount));

        return buffer
            .Take(totalCount)
            .Where(static device => device.Present != 0)
            .Select(static device => new NativeLightingDevice(
                device.DeviceId,
                device.Description,
                device.VendorId,
                device.ProductId,
                device.ApiVersion,
                device.SupportsBrightness != 0,
                device.SupportsPersistence != 0,
                device.SupportsGlobalEffects != 0))
            .ToArray();
    }

    public static void ApplyLighting(
        string deviceId,
        IReadOnlyList<NativeLightingActionRequest> actions,
        IReadOnlyList<byte> brightnessLightIds,
        int brightnessPercent,
        bool persistDefault,
        bool includePowerLights)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException("A lighting device identifier is required.");
        }

        if (actions.Count == 0)
        {
            throw new InvalidOperationException("At least one native lighting action is required.");
        }

        NativeLightAction[] nativeActions = actions
            .Select(static action => new NativeLightAction
            {
                LightId = action.LightId,
                ActionType = action.ActionType,
                SpeedPercent = (byte)Math.Clamp(action.SpeedPercent, 0, 100),
                PrimaryColor = ToPackedColor(action.PrimaryColor),
                SecondaryColor = ToPackedColor(action.SecondaryColor ?? new RgbColor(0, 0, 0)),
            })
            .ToArray();

        byte[]? brightnessIds = brightnessLightIds.Count == 0 ? null : brightnessLightIds.Distinct().ToArray();
        ThrowIfFailed(AfxLiteApplyLightActions(
            deviceId,
            nativeActions,
            nativeActions.Length,
            brightnessIds,
            brightnessIds?.Length ?? 0,
            brightnessPercent,
            persistDefault ? 1 : 0,
            includePowerLights ? 1 : 0));
    }

    public static void ApplyGlobalEffect(
        string deviceId,
        LightingEffect effect,
        int speedPercent,
        RgbColor primaryColor,
        RgbColor? secondaryColor,
        int brightnessPercent)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException("A lighting device identifier is required.");
        }

        NativeGlobalEffect request = new()
        {
            EffectType = (byte)effect,
            Mode = 1,
            ColorCount = secondaryColor is null ? (byte)1 : (byte)2,
            SpeedPercent = (byte)Math.Clamp(speedPercent, 0, 100),
            PrimaryColor = ToPackedColor(primaryColor),
            SecondaryColor = ToPackedColor(secondaryColor ?? new RgbColor(0, 0, 0)),
        };

        ThrowIfFailed(AfxLiteApplyGlobalEffect(deviceId, request, brightnessPercent));
    }

    public static string GetProtocolLabel(int apiVersion) => apiVersion switch
    {
        0 => "AlienFX ACPI",
        2 => "AlienFX API v2",
        3 => "AlienFX API v3",
        4 => "AlienFX API v4",
        5 => "AlienFX API v5",
        6 => "AlienFX API v6",
        7 => "AlienFX API v7",
        8 => "AlienFX API v8",
        _ => $"AlienFX API {apiVersion}",
    };

    private static void ThrowIfFailed(int status)
    {
        if (status == 0)
        {
            return;
        }

        throw new InvalidOperationException(GetLastError());
    }

    private static string GetLastError()
    {
        int length = AfxLiteGetLastError(null, 0);
        if (length <= 0)
        {
            return "The native AlienFX bridge returned an unknown error.";
        }

        char[] buffer = new char[length + 1];
        AfxLiteGetLastError(buffer, buffer.Length);
        return new string(buffer).TrimEnd('\0');
    }

    private static uint ToPackedColor(RgbColor color) =>
        ((uint)color.R << 16) |
        ((uint)color.G << 8) |
        color.B;
}

internal sealed record NativeLightingDevice(
    string DeviceId,
    string Description,
    ushort VendorId,
    ushort ProductId,
    int ApiVersion,
    bool SupportsBrightness,
    bool SupportsPersistence,
    bool SupportsGlobalEffects);

internal sealed record NativeLightingActionRequest(
    byte LightId,
    byte ActionType,
    int SpeedPercent,
    RgbColor PrimaryColor,
    RgbColor? SecondaryColor);
