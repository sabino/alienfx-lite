using System.IO.Pipes;
using System.Reflection;
using System.ServiceProcess;
using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Fans;
using AlienFxLite.Hardware.Lighting;

namespace AlienFxLite.Service;

internal sealed class BrokerRuntime : IDisposable
{
    private static readonly TimeSpan LightingKeepAliveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FanMaintenanceInterval = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly ServiceDiagnostics _diagnostics = new();
    private readonly StateStore _stateStore;
    private readonly ServiceConfigurationStore _configStore;
    private readonly AlienFxLightingController _lightingController = new();
    private readonly AwccFanController _fanController = new();

    private CancellationTokenSource? _cts;
    private Task? _pipeServerTask;
    private Task? _maintenanceTask;
    private bool _started;
    private string? _lastLightingError;
    private string? _lastFanError;
    private ServiceConfiguration _configuration = new(null);
    private LightingSnapshot _lightingState = StateStore.CreateDefaultState().Lighting;
    private PersistedFanState _fanState = StateStore.CreateDefaultState().Fan;

    public BrokerRuntime()
    {
        _stateStore = new StateStore(_diagnostics);
        _configStore = new ServiceConfigurationStore(_diagnostics);
    }

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _configuration = _configStore.Load();
        PersistedStateFile persistedState = _stateStore.Load();
        _lightingState = persistedState.Lighting;
        _fanState = persistedState.Fan;

        _cts = new CancellationTokenSource();

        try
        {
            RestoreLastStateCore("startup");
            _pipeServerTask = Task.Run(() => RunPipeServerAsync(_cts.Token), _cts.Token);
            _maintenanceTask = Task.Run(() => RunMaintenanceLoopAsync(_cts.Token), _cts.Token);
            _diagnostics.Info("Broker runtime started.");
        }
        catch (Exception ex)
        {
            _diagnostics.Fatal("Broker runtime failed during startup.", ex);
            throw;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _cts?.Cancel();

        try
        {
            Task[] tasks =
            [
                _pipeServerTask ?? Task.CompletedTask,
                _maintenanceTask ?? Task.CompletedTask,
            ];
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pipeServerTask = null;
            _maintenanceTask = null;
            _diagnostics.Info("Broker runtime stopped.");
        }
    }

    public void HandlePowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
        {
            ScheduleRestore("resume");
        }
    }

    public void HandleSessionChange(SessionChangeDescription changeDescription)
    {
        if (changeDescription.Reason == SessionChangeReason.SessionUnlock)
        {
            ScheduleRestore("session unlock");
        }
    }

    public void Dispose()
    {
        _lightingController.Dispose();
        _fanController.Dispose();
        _cts?.Dispose();
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream pipe = CreatePipeServer();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleClientAsync(pipe).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _diagnostics.Error("Named pipe listener failed.", ex);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                ServiceRequest request = await PipeProtocol.ReadAsync<ServiceRequest>(pipe, timeout.Token).ConfigureAwait(false);
                ServiceResponse response = Dispatch(request);
                await PipeProtocol.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _diagnostics.Error("Pipe client handling failed.", ex);
            }
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(LightingKeepAliveInterval);
        DateTimeOffset nextFanMaintenance = DateTimeOffset.UtcNow;

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_sync)
            {
                if (_lightingState.KeepAlive)
                {
                    ApplyLightingCore();
                }
                else
                {
                    _lightingController.Probe(out _lastLightingError);
                }

                if (DateTimeOffset.UtcNow < nextFanMaintenance)
                {
                    continue;
                }

                nextFanMaintenance = DateTimeOffset.UtcNow + FanMaintenanceInterval;
                if (_fanState.Mode != FanControlMode.Auto)
                {
                    ApplyFanCore();
                }
                else
                {
                    _fanController.Probe(out _lastFanError);
                }
            }
        }
    }

    private ServiceResponse Dispatch(ServiceRequest request)
    {
        try
        {
            return request.Command switch
            {
                ServiceCommands.Ping => Ok(request.RequestId, new PingResponse(GetServiceVersion(), DateTimeOffset.Now)),
                ServiceCommands.GetStatus => Ok(request.RequestId, BuildStatusSnapshot()),
                ServiceCommands.SetLightingState => HandleSetLightingState(request),
                ServiceCommands.SetFanMode => HandleSetFanMode(request),
                ServiceCommands.RestoreLastState => HandleRestoreLastState(request),
                _ => Error(request.RequestId, ServiceResponseCodes.InvalidRequest, $"Unknown command '{request.Command}'.", new NoPayload()),
            };
        }
        catch (Exception ex)
        {
            _diagnostics.Error($"Request '{request.Command}' failed.", ex);
            return Error(request.RequestId, ServiceResponseCodes.InternalError, ex.Message, new NoPayload());
        }
    }

    private ServiceResponse HandleSetLightingState(ServiceRequest request)
    {
        SetLightingStateRequest payload = ServiceJson.Deserialize<SetLightingStateRequest>(request.Payload);
        if (payload.Zones is null || payload.Zones.Count == 0)
        {
            return Error(request.RequestId, ServiceResponseCodes.InvalidRequest, "At least one lighting zone must be selected.", _lightingState);
        }

        lock (_sync)
        {
            Dictionary<LightingZone, ZoneLightingState> zones = _lightingState.ZoneStates.ToDictionary(state => state.Zone);
            foreach (LightingZone zone in payload.Zones.Distinct())
            {
                zones[zone] = new ZoneLightingState(
                    zone,
                    payload.Effect,
                    payload.PrimaryColor,
                    payload.SecondaryColor,
                    Math.Clamp(payload.Speed, 0, 100),
                    true);
            }

            _lightingState = new LightingSnapshot(
                payload.Enabled ?? _lightingState.Enabled,
                payload.Brightness ?? _lightingState.Brightness,
                payload.KeepAlive ?? _lightingState.KeepAlive,
                zones.Values.OrderBy(static state => state.Zone).ToArray());

            SaveState();
            if (!ApplyLightingCore())
            {
                return Error(request.RequestId, ServiceResponseCodes.HardwareUnavailable, _lastLightingError ?? "Lighting device unavailable.", _lightingState);
            }

            if (!_lightingController.PersistDefaultState(_lightingState, out string? persistError) &&
                !string.IsNullOrWhiteSpace(persistError))
            {
                _diagnostics.Warn($"Saved lighting sync failed: {persistError}");
            }

            return Ok(request.RequestId, _lightingState);
        }
    }

    private ServiceResponse HandleSetFanMode(ServiceRequest request)
    {
        SetFanModeRequest payload = ServiceJson.Deserialize<SetFanModeRequest>(request.Payload);

        lock (_sync)
        {
            List<int>? raw = payload.RawBoostPerFan?.Select(value => Math.Clamp(value, 0, 100)).ToList();

            int? automaticPower = _fanState.AutomaticPowerValue;
            if (payload.Mode != FanControlMode.Auto)
            {
                automaticPower ??= _fanController.GetCurrentPowerValue();
                automaticPower ??= _fanController.GetDefaultAutomaticPowerValue();
            }

            _fanState = payload.Mode switch
            {
                FanControlMode.Auto => new PersistedFanState(FanControlMode.Auto, null, automaticPower),
                FanControlMode.Max => new PersistedFanState(FanControlMode.Max, null, automaticPower),
                FanControlMode.ManualRaw => new PersistedFanState(FanControlMode.ManualRaw, raw, automaticPower),
                _ => _fanState,
            };

            SaveState();
            if (!ApplyFanCore())
            {
                return Error(request.RequestId, ServiceResponseCodes.HardwareUnavailable, _lastFanError ?? "Fan controller unavailable.", BuildFanStatus());
            }

            return Ok(request.RequestId, BuildFanStatus());
        }
    }

    private ServiceResponse HandleRestoreLastState(ServiceRequest request)
    {
        lock (_sync)
        {
            RestoreLastStateCore("client restore");
            return Ok(request.RequestId, BuildStatusSnapshot());
        }
    }

    private void RestoreLastStateCore(string reason)
    {
        _diagnostics.Info($"Restoring persisted hardware state after {reason}.");
        lock (_sync)
        {
            ApplyLightingCore();
            ApplyFanCore();
        }
    }

    private bool ApplyLightingCore()
    {
        if (_lightingController.Apply(_lightingState, out string? error))
        {
            _lastLightingError = null;
            return true;
        }

        if (!string.Equals(_lastLightingError, error, StringComparison.Ordinal))
        {
            _diagnostics.Warn($"Lighting apply failed: {error}");
        }

        _lastLightingError = error;
        return false;
    }

    private bool ApplyFanCore()
    {
        bool success = _fanState.Mode switch
        {
            FanControlMode.Auto => _fanController.SetAutomatic(_fanState.AutomaticPowerValue, out _lastFanError),
            FanControlMode.Max => _fanController.SetMaxAll(out _lastFanError),
            FanControlMode.ManualRaw when _fanState.RawBoostPerFan is not null => _fanController.SetManualRaw(_fanState.RawBoostPerFan, out _lastFanError),
            _ => false,
        };

        if (success)
        {
            _lastFanError = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_lastFanError))
        {
            _diagnostics.Warn($"Fan apply failed: {_lastFanError}");
        }

        return false;
    }

    private StatusSnapshot BuildStatusSnapshot()
    {
        lock (_sync)
        {
            return new StatusSnapshot(_lightingState, BuildFanStatus(), BuildDeviceStatus());
        }
    }

    private FanStatus BuildFanStatus()
    {
        try
        {
            IReadOnlyList<int> rpms = _fanController.GetFanRpms();
            IReadOnlyList<int> boosts = _fanController.GetRawBoosts();

            return new FanStatus(
                _fanController.IsAvailable,
                _fanState.Mode,
                rpms,
                boosts,
                _fanController.IsAvailable ? "OK" : _lastFanError ?? "Fan controller unavailable.",
                _fanState.AutomaticPowerValue);
        }
        catch (Exception ex)
        {
            _lastFanError = ex.Message;
            _diagnostics.Warn($"Fan status query failed: {ex.Message}");

            return new FanStatus(
                false,
                _fanState.Mode,
                [],
                [],
                _lastFanError,
                _fanState.AutomaticPowerValue);
        }
    }

    private DeviceStatus BuildDeviceStatus() =>
        new(
            _lightingController.IsAvailable,
            _fanController.IsAvailable,
            _lightingController.DeviceDescription,
            _lightingController.IsAvailable ? "AlienFX API v4" : null,
            _fanController.IsAvailable ? "AWCCWmiMethodFunction" : null);

    private NamedPipeServerStream CreatePipeServer()
    {
        PipeSecurity security = _configStore.CreatePipeSecurity(_configuration);
        return NamedPipeServerStreamAcl.Create(
            AlienFxLiteServiceClient.DefaultPipeName,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }

    private void SaveState()
    {
        PersistedStateFile snapshot = new(_lightingState, _fanState);
        _stateStore.Save(snapshot);
    }

    private void ScheduleRestore(string reason)
    {
        _ = Task.Run(() =>
        {
            try
            {
                RestoreLastStateCore(reason);
            }
            catch (Exception ex)
            {
                _diagnostics.Error($"Deferred restore after {reason} failed.", ex);
            }
        });
    }

    private static ServiceResponse Ok<T>(string requestId, T payload) =>
        new(requestId, true, ServiceResponseCodes.Ok, string.Empty, ServiceJson.ToElement(payload));

    private static ServiceResponse Error<T>(string requestId, string code, string message, T payload) =>
        new(requestId, false, code, message, ServiceJson.ToElement(payload));

    private static string GetServiceVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
}
