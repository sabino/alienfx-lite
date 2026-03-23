using System.Text.Json;
using AlienFxLite.Contracts;

namespace AlienFxLite.Broker;

internal sealed record PersistedFanState(
    FanControlMode Mode,
    List<int>? RawBoostPerFan,
    int? AutomaticPowerValue);

internal sealed record PersistedLightingState(
    string? ActiveDeviceKey,
    List<LightingSnapshot> Snapshots);

internal sealed record PersistedStateFile(
    PersistedLightingState Lighting,
    PersistedFanState Fan);

internal sealed record LegacyPersistedStateFile(
    LightingSnapshot Lighting,
    PersistedFanState Fan);

internal sealed record LegacyZoneLightingStateV0(
    string? Zone,
    LightingEffect Effect,
    RgbColor PrimaryColor,
    RgbColor? SecondaryColor,
    int Speed,
    bool Enabled = true);

internal sealed record LegacyLightingSnapshotV0(
    bool Enabled,
    int Brightness,
    bool KeepAlive,
    List<LegacyZoneLightingStateV0>? ZoneStates);

internal sealed record LegacyPersistedStateFileV0(
    LegacyLightingSnapshotV0? Lighting,
    PersistedFanState? Fan);

internal sealed class StateStore
{
    private readonly ServiceDiagnostics _diagnostics;

    public StateStore(ServiceDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public PersistedStateFile Load()
    {
        if (!File.Exists(_diagnostics.StateFilePath))
        {
            return CreateDefaultState();
        }

        try
        {
            string json = File.ReadAllText(_diagnostics.StateFilePath);
            PersistedStateFile? state = JsonSerializer.Deserialize<PersistedStateFile>(json, ServiceJson.Options);
            if (TryNormalizeState(state, out PersistedStateFile? normalized))
            {
                return normalized;
            }

            LegacyPersistedStateFile? legacy = JsonSerializer.Deserialize<LegacyPersistedStateFile>(json, ServiceJson.Options);
            if (legacy is not null)
            {
                List<LightingSnapshot> snapshots = legacy.Lighting.DeviceKey is null
                    ? []
                    : [legacy.Lighting];

                return new PersistedStateFile(
                    new PersistedLightingState(legacy.Lighting.DeviceKey, snapshots),
                    legacy.Fan);
            }

            LegacyPersistedStateFileV0? legacyV0 = JsonSerializer.Deserialize<LegacyPersistedStateFileV0>(json, ServiceJson.Options);
            if (legacyV0?.Fan is not null)
            {
                _diagnostics.Warn("Migrating legacy persisted state without device-bound lighting snapshots. Lighting state will be reset to defaults.");
                return new PersistedStateFile(
                    new PersistedLightingState(null, []),
                    NormalizeFanState(legacyV0.Fan));
            }

            return CreateDefaultState();
        }
        catch (Exception ex)
        {
            _diagnostics.Error("Failed to load persisted state. Reverting to defaults.", ex);
            return CreateDefaultState();
        }
    }

    public void Save(PersistedStateFile state)
    {
        string json = JsonSerializer.Serialize(state, ServiceJson.Options);
        File.WriteAllText(_diagnostics.StateFilePath, json);
    }

    public static PersistedStateFile CreateDefaultState() =>
        new(
            new PersistedLightingState(null, []),
            new PersistedFanState(FanControlMode.Auto, null, null));

    private static bool TryNormalizeState(PersistedStateFile? state, out PersistedStateFile normalized)
    {
        if (state?.Lighting is null || state.Fan is null)
        {
            normalized = CreateDefaultState();
            return false;
        }

        normalized = new PersistedStateFile(
            new PersistedLightingState(
                state.Lighting.ActiveDeviceKey,
                state.Lighting.Snapshots?.Where(static snapshot => snapshot is not null).ToList() ?? []),
            NormalizeFanState(state.Fan));

        return true;
    }

    private static PersistedFanState NormalizeFanState(PersistedFanState fan) =>
        new(
            fan.Mode,
            fan.RawBoostPerFan?.ToList(),
            fan.AutomaticPowerValue);
}
