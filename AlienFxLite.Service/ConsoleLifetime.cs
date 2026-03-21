namespace AlienFxLite.Service;

internal sealed class ConsoleLifetime : IDisposable
{
    private readonly BrokerRuntime _runtime;
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConsoleLifetime(BrokerRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task RunAsync()
    {
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            await _runtime.StartAsync().ConfigureAwait(false);
            Console.WriteLine("AlienFxLite service running in console mode. Press Ctrl+C to stop.");
            await _shutdownTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            await _runtime.StopAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _shutdownTcs.TrySetResult();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _shutdownTcs.TrySetResult();
    }
}
