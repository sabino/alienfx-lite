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
            if (state is not null)
            {
                return state;
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
}
