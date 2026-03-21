namespace AlienFxLite.UI;

internal enum AppCommand
{
    Ui = 0,
    ServiceConsole = 1,
    InstallService = 2,
    UninstallService = 3,
}

internal sealed record AppLaunchOptions(
    AppCommand Command,
    bool StartupLaunch,
    string? BinaryPath,
    string? AllowedUserSid)
{
    public static AppLaunchOptions FromArgs(IEnumerable<string> args)
    {
        List<string> values = args.ToList();
        bool startup = values.Any(static arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

        string? GetOptionValue(string name)
        {
            int index = values.FindIndex(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= values.Count)
            {
                return null;
            }

            return values[index + 1];
        }

        if (values.Any(static arg => string.Equals(arg, "--service-console", StringComparison.OrdinalIgnoreCase)))
        {
            return new AppLaunchOptions(AppCommand.ServiceConsole, startup, null, null);
        }

        if (values.Any(static arg => string.Equals(arg, "--install-service", StringComparison.OrdinalIgnoreCase)))
        {
            return new AppLaunchOptions(
                AppCommand.InstallService,
                startup,
                GetOptionValue("--binary-path"),
                GetOptionValue("--allowed-user-sid"));
        }

        if (values.Any(static arg => string.Equals(arg, "--uninstall-service", StringComparison.OrdinalIgnoreCase)))
        {
            return new AppLaunchOptions(AppCommand.UninstallService, startup, null, null);
        }

        return new AppLaunchOptions(AppCommand.Ui, startup, null, null);
    }
}
