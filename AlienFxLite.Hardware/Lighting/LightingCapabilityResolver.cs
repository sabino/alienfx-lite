using AlienFxLite.Contracts;

namespace AlienFxLite.Hardware.Lighting;

internal static class LightingCapabilityResolver
{
    private static readonly IReadOnlyList<LightingEffect> LegacyZoneEffects =
        [LightingEffect.Static, LightingEffect.Pulse, LightingEffect.Morph];

    private static readonly IReadOnlyList<LightingEffect> AdvancedZoneEffects =
    [
        LightingEffect.Static,
        LightingEffect.Pulse,
        LightingEffect.Morph,
        LightingEffect.Breathing,
        LightingEffect.Spectrum,
        LightingEffect.Rainbow,
    ];

    private static readonly IReadOnlyList<LightingEffect> MonitorZoneEffects =
        [LightingEffect.Static, LightingEffect.Pulse, LightingEffect.Morph, LightingEffect.Breathing];

    private static readonly IReadOnlyList<LightingEffect> KeyboardV5Effects =
    [
        LightingEffect.Static,
        LightingEffect.Pulse,
        LightingEffect.Morph,
        LightingEffect.Breathing,
        LightingEffect.Rainbow,
    ];

    public static IReadOnlyList<LightingEffect> GetSupportedEffects(int apiVersion) => apiVersion switch
    {
        4 or 7 or 8 => AdvancedZoneEffects,
        6 => MonitorZoneEffects,
        5 => KeyboardV5Effects,
        2 or 3 => LegacyZoneEffects,
        _ => LightingEffectCatalog.DefaultSupportedEffects,
    };
}
