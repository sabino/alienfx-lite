using System.Text.Json;
using AlienFxLite.Contracts;

namespace AlienFxLite.Broker;

internal sealed record PersistedFanState(
    FanControlMode Mode,
    List<int>? RawBoostPerFan,
    int? AutomaticPowerValue);

internal sealed record PersistedStateFile(
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
            return state ?? CreateDefaultState();
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
            new LightingSnapshot(true, 100, true, null, []),
            new PersistedFanState(FanControlMode.Auto, null, null));
}
