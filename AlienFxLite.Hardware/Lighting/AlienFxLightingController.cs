using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Hid;

namespace AlienFxLite.Hardware.Lighting;

public sealed class AlienFxLightingController : IDisposable
{
    private const byte SavedLightingBlockId = 0x61;

    private readonly object _sync = new();
    private readonly AlienFxMappingCatalog _catalog = AlienFxMappingCatalog.LoadDefault();
    private AlienFxV4Device? _device;
    private LightingDeviceProfile? _profile;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return _device is not null && _profile is not null;
            }
        }
    }

    public LightingDeviceProfile? CurrentProfile
    {
        get
        {
            lock (_sync)
            {
                return _profile;
            }
        }
    }

    public string? DeviceDescription
    {
        get
        {
            lock (_sync)
            {
                if (_device is null || _profile is null)
                {
                    return null;
                }

                return $"{_profile.DisplayName} [{_device.DevicePath}]";
            }
        }
    }

    public bool Probe(out string? error)
    {
        lock (_sync)
        {
            return EnsureConnected(out error);
        }
    }

    public bool Apply(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
            if (TryProgramSnapshot(snapshot, resetDevice: true, forceUpdate: false, out error))
            {
                return true;
            }

            ResetConnection();
            return TryProgramSnapshot(snapshot, resetDevice: true, forceUpdate: false, out error);
        }
    }

    public bool Maintain(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
            return TryProgramSnapshot(snapshot, resetDevice: false, forceUpdate: true, out error);
        }
    }

    public bool PersistDefaultState(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
            return TryPersistDefaultState(snapshot, out error);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            ResetConnection();
        }
    }

    private bool TryProgramSnapshot(LightingSnapshot snapshot, bool resetDevice, bool forceUpdate, out string? error)
    {
        error = null;
        if (!EnsureConnected(out error))
        {
            return false;
        }

        if (_device is null || _profile is null)
        {
            error = "Lighting device is unavailable.";
            return false;
        }

        if (resetDevice && !_device.Reset(out error))
        {
            return false;
        }

        Dictionary<int, LightingZoneDefinition> zonesById = _profile.Zones.ToDictionary(zone => zone.ZoneId);
        bool needsUpdate = forceUpdate;
        foreach (IGrouping<RgbColor, ZoneLightingState> staticGroup in snapshot.ZoneStates
                     .Where(static state => state.Effect == LightingEffect.Static)
                     .GroupBy(static state => state.PrimaryColor))
        {
            byte[] lightIds = staticGroup
                .SelectMany(zoneState => zonesById.TryGetValue(zoneState.ZoneId, out LightingZoneDefinition? zone)
                    ? zone.LightIds
                    : [])
                .Distinct()
                .ToArray();

            if (lightIds.Length == 0)
            {
                continue;
            }

            if (!_device.ApplyStaticZones(staticGroup.Key, lightIds, out error))
            {
                return false;
            }
        }

        foreach (ZoneLightingState zoneState in snapshot.ZoneStates.OrderBy(static state => state.ZoneId))
        {
            if (zoneState.Effect == LightingEffect.Static ||
                !zonesById.TryGetValue(zoneState.ZoneId, out LightingZoneDefinition? zoneDefinition))
            {
                continue;
            }

            foreach (byte lightId in zoneDefinition.LightIds)
            {
                if (!_device.ApplyLight(zoneState, lightId, out error))
                {
                    return false;
                }
            }

            needsUpdate = true;
        }

        if (needsUpdate && !_device.UpdateColors(out error))
        {
            return false;
        }

        int brightness = snapshot.Enabled ? snapshot.Brightness : 0;
        byte[] allLightIds = _profile.Zones.SelectMany(static zone => zone.LightIds).Distinct().ToArray();
        return _device.SetBrightness(brightness, allLightIds, out error);
    }

    private bool EnsureConnected(out string? error)
    {
        error = null;
        if (_device is not null && _profile is not null)
        {
            return true;
        }

        foreach (HidDeviceInfo deviceInfo in HidNative.EnumerateDevices())
        {
            if (deviceInfo.OutputReportLength != 34)
            {
                continue;
            }

            LightingDeviceProfile? profile = _catalog.FindProfile(deviceInfo.VendorId, deviceInfo.ProductId);
            if (profile is null)
            {
                continue;
            }

            AlienFxV4Device? device = AlienFxV4Device.Open(deviceInfo, out error);
            if (device is null)
            {
                continue;
            }

            _device = device;
            _profile = profile;
            error = null;
            return true;
        }

        error = "No supported AlienFX API v4 lighting device was found.";
        return false;
    }

    private bool TryPersistDefaultState(LightingSnapshot snapshot, out string? error)
    {
        error = null;
        if (!EnsureConnected(out error))
        {
            return false;
        }

        if (_device is null || _profile is null)
        {
            error = "Lighting device is unavailable.";
            return false;
        }

        if (!_device.BeginSavedLightingBlock(SavedLightingBlockId, out error))
        {
            return false;
        }

        Dictionary<int, LightingZoneDefinition> zonesById = _profile.Zones.ToDictionary(zone => zone.ZoneId);
        foreach (ZoneLightingState zoneState in snapshot.ZoneStates.OrderBy(static state => state.ZoneId))
        {
            if (!zonesById.TryGetValue(zoneState.ZoneId, out LightingZoneDefinition? zoneDefinition))
            {
                continue;
            }

            bool success = zoneState.Effect == LightingEffect.Static
                ? _device.ApplyStaticZones(zoneState.PrimaryColor, zoneDefinition.LightIds, out error)
                : PersistAnimatedZone(zoneState, zoneDefinition.LightIds, out error);

            if (!success)
            {
                return false;
            }
        }

        if (!_device.CommitSavedLightingBlock(SavedLightingBlockId, out error))
        {
            return false;
        }

        _device.SetStartupLightingBlock(SavedLightingBlockId, out _);
        return true;
    }

    private void ResetConnection()
    {
        _device?.Dispose();
        _device = null;
        _profile = null;
    }

    private bool PersistAnimatedZone(ZoneLightingState zoneState, IReadOnlyList<byte> lightIds, out string? error)
    {
        error = null;
        if (_device is null)
        {
            error = "Lighting device is unavailable.";
            return false;
        }

        foreach (byte lightId in lightIds)
        {
            if (!_device.ApplyLight(zoneState, lightId, out error))
            {
                return false;
            }
        }

        return _device.UpdateColors(out error);
    }
}
