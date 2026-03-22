using System.IO.Pipes;
using System.Reflection;
using System.ServiceProcess;
using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Fans;
using AlienFxLite.Hardware.Lighting;

namespace AlienFxLite.Broker;

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
    private Dictionary<string, LightingSnapshot> _lightingStates = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeLightingDeviceKey;
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
        _lightingStates = persistedState.Lighting.Snapshots
            .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.DeviceKey))
            .GroupBy(static snapshot => snapshot.DeviceKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        _activeLightingDeviceKey = persistedState.Lighting.ActiveDeviceKey;
        _fanState = persistedState.Fan;
        EnsureLightingProfileState();

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
                if (_lightingStates.Values.Any(static snapshot => snapshot.KeepAlive))
                {
                    EnsureLightingProfileState();
                    MaintainLightingCore();
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
        if (payload.ZoneIds is null || payload.ZoneIds.Count == 0)
        {
            return Error(request.RequestId, ServiceResponseCodes.InvalidRequest, "At least one lighting zone must be selected.", GetActiveLightingState());
        }

        lock (_sync)
        {
            EnsureLightingProfileState();
            LightingDeviceProfile? profile = ResolveActiveProfile(payload.DeviceKey);
            if (profile is null)
            {
                return Error(request.RequestId, ServiceResponseCodes.HardwareUnavailable, _lastLightingError ?? "Lighting device unavailable.", GetActiveLightingState());
            }

            if (!LightingEffectCatalog.SupportsEffect(profile, payload.Effect))
            {
                string supported = string.Join(", ", LightingEffectCatalog.GetSupportedEffects(profile));
                return Error(
                    request.RequestId,
                    ServiceResponseCodes.InvalidRequest,
                    $"Effect '{payload.Effect}' is not supported for '{profile.DisplayName}'. Supported effects: {supported}.",
                    GetOrCreateLightingState(profile));
            }

            LightingSnapshot snapshot = GetOrCreateLightingState(profile);
            Dictionary<int, ZoneLightingState> zones = snapshot.ZoneStates.ToDictionary(static state => state.ZoneId);
            foreach (int zoneId in payload.ZoneIds.Distinct())
            {
                zones[zoneId] = new ZoneLightingState(
                    zoneId,
                    payload.Effect,
                    payload.PrimaryColor,
                    payload.SecondaryColor,
                    Math.Clamp(payload.Speed, 0, 100),
                    true);
            }

            LightingSnapshot updated = NormalizeSnapshot(profile, new LightingSnapshot(
                payload.Enabled ?? snapshot.Enabled,
                payload.Brightness ?? snapshot.Brightness,
                payload.KeepAlive ?? snapshot.KeepAlive,
                profile.DeviceKey,
                zones.Values.OrderBy(static state => state.ZoneId).ToArray()));

            _lightingStates[profile.DeviceKey] = updated;
            _activeLightingDeviceKey = profile.DeviceKey;
            SaveState();

            if (!ApplyLightingCore(profile.DeviceKey))
            {
                return Error(request.RequestId, ServiceResponseCodes.HardwareUnavailable, _lastLightingError ?? "Lighting device unavailable.", updated);
            }

            if (!_lightingController.PersistDefaultState(updated, out string? persistError) &&
                !string.IsNullOrWhiteSpace(persistError))
            {
                _diagnostics.Warn($"Saved lighting sync failed: {persistError}");
            }

            return Ok(request.RequestId, updated);
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
            EnsureLightingProfileState();
            ApplyAllLightingStates(keepAliveOnly: false);
            ApplyFanCore();
        }
    }

    private bool ApplyLightingCore(string deviceKey)
    {
        EnsureLightingProfileState();
        if (!_lightingStates.TryGetValue(deviceKey, out LightingSnapshot? snapshot))
        {
            return true;
        }

        if (_lightingController.Apply(snapshot, out string? error))
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

    private bool MaintainLightingCore() => ApplyAllLightingStates(keepAliveOnly: true);

    private bool ApplyAllLightingStates(bool keepAliveOnly)
    {
        EnsureLightingProfileState();
        bool anyAttempted = false;
        bool allSucceeded = true;

        foreach (LightingSnapshot snapshot in GetAvailableLightingStates()
                     .Where(snapshot => !keepAliveOnly || snapshot.KeepAlive))
        {
            anyAttempted = true;
            string? error;
            bool success = keepAliveOnly
                ? _lightingController.Maintain(snapshot, out error) || _lightingController.Apply(snapshot, out error)
                : _lightingController.Apply(snapshot, out error);

            if (success)
            {
                _lastLightingError = null;
                continue;
            }

            allSucceeded = false;
            if (!string.Equals(_lastLightingError, error, StringComparison.Ordinal))
            {
                _diagnostics.Warn($"Lighting {(keepAliveOnly ? "keepalive" : "apply")} failed: {error}");
            }

            _lastLightingError = error;
        }

        return !anyAttempted || allSucceeded;
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
            EnsureLightingProfileState();
            LightingSnapshot active = GetActiveLightingState();
            IReadOnlyList<LightingSnapshot> storedStates = _lightingStates.Values
                .OrderBy(static snapshot => snapshot.DeviceKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new StatusSnapshot(active, BuildFanStatus(), BuildDeviceStatus(), storedStates);
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

    private DeviceStatus BuildDeviceStatus()
    {
        LightingDeviceProfile? activeProfile = ResolveActiveProfile(_activeLightingDeviceKey);
        return new DeviceStatus(
            _lightingController.AvailableProfiles.Count > 0,
            _fanController.IsAvailable,
            activeProfile?.HardwareDescription,
            activeProfile?.Protocol,
            _fanController.IsAvailable ? "AWCCWmiMethodFunction" : null,
            activeProfile,
            _lightingController.AvailableProfiles);
    }

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
        PersistedStateFile snapshot = new(
            new PersistedLightingState(
                _activeLightingDeviceKey,
                _lightingStates.Values
                    .OrderBy(static lighting => lighting.DeviceKey, StringComparer.OrdinalIgnoreCase)
                    .ToList()),
            _fanState);

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
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

    private void EnsureLightingProfileState()
    {
        _lightingController.Probe(out _lastLightingError);
        IReadOnlyList<LightingDeviceProfile> profiles = _lightingController.AvailableProfiles;
        if (profiles.Count == 0)
        {
            _activeLightingDeviceKey = null;
            return;
        }

        Dictionary<string, LightingSnapshot> nextStates = new(StringComparer.OrdinalIgnoreCase);
        foreach (LightingDeviceProfile profile in profiles)
        {
            LightingSnapshot snapshot = ResolvePersistedSnapshot(profile);
            nextStates[profile.DeviceKey] = NormalizeSnapshot(profile, snapshot);
        }

        _lightingStates = nextStates;

        LightingDeviceProfile activeProfile = ResolveActiveProfile(_activeLightingDeviceKey)
            ?? _lightingController.CurrentProfile
            ?? profiles[0];

        _activeLightingDeviceKey = activeProfile.DeviceKey;
        SaveState();
    }

    private LightingSnapshot ResolvePersistedSnapshot(LightingDeviceProfile profile)
    {
        if (_lightingStates.TryGetValue(profile.DeviceKey, out LightingSnapshot? exact))
        {
            return exact;
        }

        LightingSnapshot? migrated = _lightingStates.Values.FirstOrDefault(snapshot =>
            !string.IsNullOrWhiteSpace(snapshot.DeviceKey) &&
            (profile.DeviceKey.EndsWith($"|{snapshot.DeviceKey}", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(snapshot.DeviceKey, BuildLegacyProfileKey(profile), StringComparison.OrdinalIgnoreCase)));

        return migrated ?? CreateDefaultSnapshot(profile);
    }

    private LightingSnapshot GetOrCreateLightingState(LightingDeviceProfile profile)
    {
        LightingSnapshot snapshot = ResolvePersistedSnapshot(profile);
        _lightingStates[profile.DeviceKey] = snapshot;
        return snapshot;
    }

    private LightingSnapshot GetActiveLightingState()
    {
        LightingDeviceProfile? profile = ResolveActiveProfile(_activeLightingDeviceKey);
        if (profile is null)
        {
            return CreateDefaultSnapshot(null);
        }

        return _lightingStates.TryGetValue(profile.DeviceKey, out LightingSnapshot? snapshot)
            ? snapshot
            : CreateDefaultSnapshot(profile);
    }

    private IReadOnlyList<LightingSnapshot> GetAvailableLightingStates() =>
        _lightingController.AvailableProfiles
            .Select(GetOrCreateLightingState)
            .ToArray();

    private LightingDeviceProfile? ResolveActiveProfile(string? deviceKey)
    {
        IReadOnlyList<LightingDeviceProfile> profiles = _lightingController.AvailableProfiles;
        if (!string.IsNullOrWhiteSpace(deviceKey))
        {
            LightingDeviceProfile? exact = profiles.FirstOrDefault(profile =>
                string.Equals(profile.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            LightingDeviceProfile? migrated = profiles.FirstOrDefault(profile =>
                profile.DeviceKey.EndsWith($"|{deviceKey}", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BuildLegacyProfileKey(profile), deviceKey, StringComparison.OrdinalIgnoreCase));
            if (migrated is not null)
            {
                return migrated;
            }
        }

        return profiles.FirstOrDefault();
    }

    private static LightingSnapshot NormalizeSnapshot(LightingDeviceProfile profile, LightingSnapshot snapshot)
    {
        Dictionary<int, ZoneLightingState> existing = snapshot.ZoneStates.ToDictionary(static zone => zone.ZoneId);
        List<ZoneLightingState> zones = profile.Zones
            .Select(zone => existing.TryGetValue(zone.ZoneId, out ZoneLightingState? state)
                ? NormalizeZoneState(profile, state with { ZoneId = zone.ZoneId })
                : CreateDefaultZoneState(zone.ZoneId))
            .OrderBy(static zone => zone.ZoneId)
            .ToList();

        return new LightingSnapshot(
            snapshot.Enabled,
            Math.Clamp(snapshot.Brightness, 0, 100),
            snapshot.KeepAlive,
            profile.DeviceKey,
            zones);
    }

    private static ZoneLightingState NormalizeZoneState(LightingDeviceProfile profile, ZoneLightingState state)
    {
        LightingEffect effect = LightingEffectCatalog.NormalizeEffect(profile, state.Effect);
        return state with
        {
            Effect = effect,
            SecondaryColor = effect == LightingEffect.Morph ? state.SecondaryColor : null,
        };
    }

    private static LightingSnapshot CreateDefaultSnapshot(LightingDeviceProfile? profile) =>
        new(
            true,
            100,
            true,
            profile?.DeviceKey,
            profile?.Zones.Select(static zone => CreateDefaultZoneState(zone.ZoneId)).ToArray() ?? []);

    private static ZoneLightingState CreateDefaultZoneState(int zoneId) =>
        new(zoneId, LightingEffect.Static, new RgbColor(255, 255, 255), null, 50, true);

    private static string BuildLegacyProfileKey(LightingDeviceProfile profile) =>
        $"{profile.VendorId:X4}:{profile.ProductId:X4}:{SanitizeKey(profile.SurfaceName)}";

    private static string SanitizeKey(string value)
    {
        char[] chars = value
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_')
            .ToArray();

        return new string(chars).Trim('_');
    }
}
