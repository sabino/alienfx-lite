using System.Security.Principal;
using System.Threading;

namespace AlienFxLite.UI;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly bool _ownsMutex;
    private RegisteredWaitHandle? _registeredWait;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activationEvent, bool ownsMutex)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstanceCoordinator? TryCreate()
    {
        string suffix = GetPerUserSuffix();
        Mutex mutex = new(true, $@"Local\AlienFxLite.Ui.{suffix}", out bool createdNew);
        EventWaitHandle activationEvent = new(false, EventResetMode.AutoReset, $@"Local\AlienFxLite.Ui.Activate.{suffix}");

        if (!createdNew)
        {
            activationEvent.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceCoordinator(mutex, activationEvent, ownsMutex: true);
    }

    public static void SignalExistingInstance()
    {
        string suffix = GetPerUserSuffix();
        try
        {
            using EventWaitHandle existing = EventWaitHandle.OpenExisting($@"Local\AlienFxLite.Ui.Activate.{suffix}");
            existing.Set();
        }
        catch
        {
        }
    }

    public void StartListening(Action onActivationRequested)
    {
        _registeredWait?.Unregister(null);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) => ((Action)state!).Invoke(),
            onActivationRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        _activationEvent.Dispose();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    private static string GetPerUserSuffix()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        return string.IsNullOrWhiteSpace(sid) ? Environment.UserName : sid.Replace('\\', '_');
    }
}
