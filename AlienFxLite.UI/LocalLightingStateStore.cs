using System.IO;
using System.Text.Json;
using AlienFxLite.Contracts;

namespace AlienFxLite.UI;

internal sealed record LocalLightingState(
    string? ActiveDeviceKey,
    List<LightingSnapshot> Snapshots)
{
    public static LocalLightingState Default { get; } = new(null, []);
}

internal sealed class LocalLightingStateStore
{
    private const string StateFileName = "lighting-state.json";

    private readonly string _rootDirectory;
    private readonly string _statePath;

    public LocalLightingStateStore()
    {
        _rootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienFxLite");
        _statePath = Path.Combine(_rootDirectory, StateFileName);
        Directory.CreateDirectory(_rootDirectory);
    }

    public LocalLightingState Load()
    {
        if (!File.Exists(_statePath))
        {
            return LocalLightingState.Default;
        }

        try
        {
            LocalLightingState? state = JsonSerializer.Deserialize<LocalLightingState>(File.ReadAllText(_statePath));
            return Normalize(state);
        }
        catch
        {
            return LocalLightingState.Default;
        }
    }

    public void Save(LocalLightingState state)
    {
        Directory.CreateDirectory(_rootDirectory);
        File.WriteAllText(
            _statePath,
            JsonSerializer.Serialize(Normalize(state), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static LocalLightingState Normalize(LocalLightingState? state) =>
        state is null
            ? LocalLightingState.Default
            : new LocalLightingState(
                state.ActiveDeviceKey,
                state.Snapshots
                    .Where(static snapshot => snapshot is not null)
                    .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.DeviceKey))
                    .ToList());
}
