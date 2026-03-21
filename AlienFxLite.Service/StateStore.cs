using System.Text.Json;
using AlienFxLite.Contracts;

namespace AlienFxLite.Service;

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

    public static PersistedStateFile CreateDefaultState()
    {
        List<ZoneLightingState> defaultZones =
        [
            CreateDefaultZone(LightingZone.KbLeft),
            CreateDefaultZone(LightingZone.KbCenter),
            CreateDefaultZone(LightingZone.KbRight),
            CreateDefaultZone(LightingZone.KbNumPad),
        ];

        return new PersistedStateFile(
            new LightingSnapshot(true, 100, true, defaultZones),
            new PersistedFanState(FanControlMode.Auto, null, null));
    }

    private static ZoneLightingState CreateDefaultZone(LightingZone zone) =>
        new(zone, LightingEffect.Static, new RgbColor(255, 255, 255), null, 50, true);
}
