using System.ServiceProcess;

namespace AlienFxLite.Broker;

internal sealed class AlienFxLiteWindowsService : ServiceBase
{
    private readonly BrokerRuntime _runtime;

    public AlienFxLiteWindowsService(BrokerRuntime runtime)
    {
        _runtime = runtime;
        ServiceName = ServiceInstaller.ServiceName;
        AutoLog = false;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnStart(string[] args)
    {
        _runtime.StartAsync().GetAwaiter().GetResult();
    }

    protected override void OnStop()
    {
        _runtime.StopAsync().GetAwaiter().GetResult();
    }

    protected override void OnShutdown()
    {
        _runtime.StopAsync().GetAwaiter().GetResult();
        base.OnShutdown();
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        _runtime.HandlePowerEvent(powerStatus);
        return base.OnPowerEvent(powerStatus);
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        _runtime.HandleSessionChange(changeDescription);
        base.OnSessionChange(changeDescription);
    }
}
