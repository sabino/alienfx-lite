using AlienFxLite.Broker;

namespace AlienFxLite.Service;

internal static class Program
{
    public static void Main()
    {
        if (BrokerHost.TryRunAsWindowsService())
        {
            return;
        }

        BrokerHost.RunConsoleBrokerAsync().GetAwaiter().GetResult();
    }
}
