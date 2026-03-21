using System.ServiceProcess;

namespace AlienFxLite.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        BrokerRuntime runtime = new();

        if (Environment.UserInteractive)
        {
            using ConsoleLifetime lifetime = new(runtime);
            await lifetime.RunAsync().ConfigureAwait(false);
            return;
        }

        ServiceBase.Run(new AlienFxLiteWindowsService(runtime));
    }
}
