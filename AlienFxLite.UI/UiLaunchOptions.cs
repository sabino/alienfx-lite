namespace AlienFxLite.UI;

internal sealed record UiLaunchOptions(bool StartupLaunch)
{
    public static UiLaunchOptions FromArgs(IEnumerable<string> args) =>
        new(args.Any(static arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)));
}
