using System.Text.Json;

namespace AlienFxLite.Contracts;

public enum LightingEffect
{
    Static = 0,
    Pulse = 1,
    Morph = 2,
    Breathing = 3,
    Spectrum = 4,
    Rainbow = 5,
}

public static class LightingEffectCatalog
{
    public static IReadOnlyList<LightingEffect> DefaultSupportedEffects { get; } =
        [LightingEffect.Static, LightingEffect.Pulse, LightingEffect.Morph];

    public static IReadOnlyList<RgbColor> DefaultSpectrumPalette { get; } =
    [
        new(255, 72, 72),
        new(255, 154, 56),
        new(255, 214, 72),
        new(110, 255, 97),
        new(90, 228, 255),
        new(88, 132, 255),
        new(184, 110, 255),
    ];

    public static bool IsAnimated(LightingEffect effect) => effect != LightingEffect.Static;

    public static bool SupportsSecondaryColor(LightingEffect effect) => effect == LightingEffect.Morph;

    public static bool SupportsPalette(LightingEffect effect) => effect == LightingEffect.Spectrum;

    public static bool UsesPrimaryColor(LightingEffect effect) => effect is not LightingEffect.Rainbow && effect is not LightingEffect.Spectrum;

    public static IReadOnlyList<LightingEffect> GetSupportedEffects(LightingDeviceProfile? profile) =>
        profile?.SupportedEffects is { Count: > 0 } effects
            ? effects
            : DefaultSupportedEffects;

    public static LightingEffect GetDefaultEffect(LightingDeviceProfile? profile) =>
        GetSupportedEffects(profile).FirstOrDefault();

    public static bool SupportsEffect(LightingDeviceProfile? profile, LightingEffect effect) =>
        GetSupportedEffects(profile).Contains(effect);

    public static LightingEffect NormalizeEffect(LightingDeviceProfile? profile, LightingEffect effect) =>
        SupportsEffect(profile, effect)
            ? effect
            : GetDefaultEffect(profile);

    public static bool RequiresWholeDeviceSelection(LightingDeviceProfile? profile, LightingEffect effect) =>
        profile?.ApiVersion == 5 && IsAnimated(effect);
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

public sealed record LightingZoneDefinition(
    int ZoneId,
    string Name,
    bool IsPowerOrIndicator,
    IReadOnlyList<byte> LightIds);

public sealed record LightingGridDefinition(
    int GridId,
    string Name,
    int Columns,
    int Rows,
    IReadOnlyList<int?> Cells);

public sealed record LightingDeviceProfile(
    string DeviceKey,
    string DisplayName,
    ushort VendorId,
    ushort ProductId,
    int ApiVersion,
    string SurfaceName,
    string Protocol,
    IReadOnlyList<LightingZoneDefinition> Zones,
    LightingGridDefinition? PreviewGrid,
    bool SupportsBrightness = true,
    bool SupportsPersistence = false,
    bool SupportsGlobalEffects = false,
    IReadOnlyList<LightingEffect>? SupportedEffects = null,
    string? HardwareId = null,
    string? HardwareDescription = null);

public sealed record ZoneLightingState(
    int ZoneId,
    LightingEffect Effect,
    RgbColor PrimaryColor,
    RgbColor? SecondaryColor,
    int Speed,
    bool Enabled = true,
    int Brightness = 100,
    IReadOnlyList<RgbColor>? Palette = null);

public sealed record LightingSnapshot(
    bool Enabled,
    int Brightness,
    bool KeepAlive,
    string? DeviceKey,
    IReadOnlyList<ZoneLightingState> ZoneStates);

public sealed record SetLightingStateRequest(
    string? DeviceKey,
    IReadOnlyList<int> ZoneIds,
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
    string? FanProvider,
    LightingDeviceProfile? LightingProfile,
    IReadOnlyList<LightingDeviceProfile> LightingProfiles);

public sealed record StatusSnapshot(
    LightingSnapshot Lighting,
    FanStatus Fan,
    DeviceStatus Devices,
    IReadOnlyList<LightingSnapshot> LightingStates);

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
