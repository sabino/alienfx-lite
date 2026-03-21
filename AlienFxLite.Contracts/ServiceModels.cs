using System.Text.Json;

namespace AlienFxLite.Contracts;

public enum LightingZone
{
    KbLeft = 0,
    KbCenter = 1,
    KbRight = 2,
    KbNumPad = 3,
}

public enum LightingEffect
{
    Static = 0,
    Pulse = 1,
    Morph = 2,
}

public enum FanControlMode
{
    Auto = 0,
    Max = 1,
    ManualRaw = 2,
}

public static class ServiceCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "getStatus";
    public const string SetLightingState = "setLightingState";
    public const string SetFanMode = "setFanMode";
    public const string RestoreLastState = "restoreLastState";
}

public static class ServiceResponseCodes
{
    public const string Ok = "ok";
    public const string InvalidRequest = "invalid_request";
    public const string HardwareUnavailable = "hardware_unavailable";
    public const string InternalError = "internal_error";
}

public readonly record struct RgbColor(byte R, byte G, byte B);

public sealed record ZoneLightingState(
    LightingZone Zone,
    LightingEffect Effect,
    RgbColor PrimaryColor,
    RgbColor? SecondaryColor,
    int Speed,
    bool Enabled = true);

public sealed record LightingSnapshot(
    bool Enabled,
    int Brightness,
    bool KeepAlive,
    IReadOnlyList<ZoneLightingState> ZoneStates);

public sealed record SetLightingStateRequest(
    IReadOnlyList<LightingZone> Zones,
    LightingEffect Effect,
    RgbColor PrimaryColor,
    RgbColor? SecondaryColor,
    int Speed,
    int? Brightness,
    bool? KeepAlive,
    bool? Enabled);

public sealed record SetFanModeRequest(
    FanControlMode Mode,
    IReadOnlyList<int>? RawBoostPerFan = null);

public sealed record FanStatus(
    bool Available,
    FanControlMode Mode,
    IReadOnlyList<int> Rpm,
    IReadOnlyList<int> RawBoosts,
    string Message,
    int? AutomaticPowerValue = null);

public sealed record DeviceStatus(
    bool LightingAvailable,
    bool FanAvailable,
    string? LightingDevice,
    string? LightingProtocol,
    string? FanProvider);

public sealed record StatusSnapshot(
    LightingSnapshot Lighting,
    FanStatus Fan,
    DeviceStatus Devices);

public sealed record PingResponse(
    string ServiceVersion,
    DateTimeOffset ServiceTime);

public sealed record NoPayload;

public sealed record ServiceRequest(
    string RequestId,
    string Command,
    JsonElement Payload);

public sealed record ServiceResponse(
    string RequestId,
    bool Ok,
    string Code,
    string Message,
    JsonElement Payload);
