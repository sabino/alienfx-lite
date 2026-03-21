using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AlienFxLite.UI;

internal sealed class DesktopSettingsStore
{
    private const string SettingsFileName = "desktop-settings.json";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AlienFxLite";

    private readonly string _rootDirectory;
    private readonly string _settingsPath;

    public DesktopSettingsStore()
    {
        _rootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienFxLite");
        _settingsPath = Path.Combine(_rootDirectory, SettingsFileName);
        Directory.CreateDirectory(_rootDirectory);
    }

    public DesktopSettings Load()
    {
        DesktopSettings loaded = DesktopSettings.Default;
        if (File.Exists(_settingsPath))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(_settingsPath)) ?? DesktopSettings.Default;
            }
            catch
            {
                loaded = DesktopSettings.Default;
            }
        }

        return loaded with { StartWithWindows = IsStartupEnabled() };
    }

    public void Save(DesktopSettings settings)
    {
        Directory.CreateDirectory(_rootDirectory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        SyncStartupRegistration(settings.StartWithWindows);
    }

    public void SyncStartupRegistration(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the Windows startup registry key.");

        if (enabled)
        {
            key.SetValue(RunValueName, BuildStartupCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        string? existing = key?.GetValue(RunValueName) as string;
        return !string.IsNullOrWhiteSpace(existing);
    }

    private static string BuildStartupCommand()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Unable to determine the current AlienFx Lite executable path.");
        }

        return $"\"{executablePath}\" --startup";
    }
}
