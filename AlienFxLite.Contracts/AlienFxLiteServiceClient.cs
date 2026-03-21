using System.IO.Pipes;

namespace AlienFxLite.Contracts;

public sealed class AlienFxLiteServiceClient
{
    public const string DefaultPipeName = "AlienFxLite.v1";

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public AlienFxLiteServiceClient(string pipeName = DefaultPipeName, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
    }

    public Task<PingResponse> PingAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NoPayload, PingResponse>(ServiceCommands.Ping, new NoPayload(), cancellationToken);

    public Task<StatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NoPayload, StatusSnapshot>(ServiceCommands.GetStatus, new NoPayload(), cancellationToken);

    public Task<LightingSnapshot> SetLightingStateAsync(SetLightingStateRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SetLightingStateRequest, LightingSnapshot>(ServiceCommands.SetLightingState, request, cancellationToken);

    public Task<FanStatus> SetFanModeAsync(SetFanModeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SetFanModeRequest, FanStatus>(ServiceCommands.SetFanMode, request, cancellationToken);

    public Task<StatusSnapshot> RestoreLastStateAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NoPayload, StatusSnapshot>(ServiceCommands.RestoreLastState, new NoPayload(), cancellationToken);

    public async Task<TResponse> SendAsync<TRequest, TResponse>(string command, TRequest payload, CancellationToken cancellationToken = default)
    {
        using NamedPipeClientStream pipe = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_connectTimeout);

        await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

        ServiceRequest request = new(
            Guid.NewGuid().ToString("N"),
            command,
            ServiceJson.ToElement(payload));

        await PipeProtocol.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        ServiceResponse response = await PipeProtocol.ReadAsync<ServiceResponse>(pipe, cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new InvalidOperationException($"{response.Code}: {response.Message}");
        }

        return ServiceJson.Deserialize<TResponse>(response.Payload);
    }
}
