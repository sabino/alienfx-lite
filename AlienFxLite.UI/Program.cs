using AlienFxLite.Broker;

namespace AlienFxLite.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppLaunchOptions options = AppLaunchOptions.FromArgs(args);

        if (BrokerHost.TryRunAsWindowsService())
        {
            return 0;
        }

        switch (options.Command)
        {
            case AppCommand.ServiceConsole:
                BrokerHost.RunConsoleBrokerAsync().GetAwaiter().GetResult();
                return 0;
            case AppCommand.InstallService:
                ServiceInstaller.InstallOrUpdate(
                    options.BinaryPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve the AlienFx Lite executable path."),
                    options.AllowedUserSid);
                return 0;
            case AppCommand.UninstallService:
                ServiceInstaller.Uninstall();
                return 0;
        }

        using SingleInstanceCoordinator? singleInstance = SingleInstanceCoordinator.TryCreate();
        if (singleInstance is null)
        {
            SingleInstanceCoordinator.SignalExistingInstance();
            return 0;
        }

        App app = new();
        app.InitializeComponent();

        MainWindow window = new(options);
        singleInstance.StartListening(window.HandleExternalActivationRequest);
        app.Run(window);
        return 0;
    }
}
