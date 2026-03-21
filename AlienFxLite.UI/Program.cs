using AlienFxLite.Service;

namespace AlienFxLite.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (BrokerHost.TryRunAsWindowsService())
        {
            return 0;
        }

        App app = new();
        app.InitializeComponent();

        MainWindow window = new(UiLaunchOptions.FromArgs(args));
        app.Run(window);
        return 0;
    }
}
