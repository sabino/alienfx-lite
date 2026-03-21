using System.ServiceProcess;

namespace AlienFxLite.Service;

public static class BrokerHost
{
    public static bool TryRunAsWindowsService()
    {
        if (Environment.UserInteractive)
        {
            return false;
        }

        using BrokerRuntime runtime = new();
        ServiceBase.Run(new AlienFxLiteWindowsService(runtime));
        return true;
    }

    public static async Task RunConsoleBrokerAsync()
    {
        using BrokerRuntime runtime = new();
        using ConsoleLifetime lifetime = new(runtime);
        await lifetime.RunAsync().ConfigureAwait(false);
    }
}
