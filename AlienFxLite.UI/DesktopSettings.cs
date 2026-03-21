namespace AlienFxLite.UI;

internal sealed record DesktopSettings(bool StartWithWindows, bool MinimizeToTray)
{
    public static DesktopSettings Default { get; } = new(false, false);
}
