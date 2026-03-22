using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Native;

namespace AlienFxLite.Hardware.Lighting;

public sealed class AlienFxLightingController : IDisposable
{
    private readonly object _sync = new();
    private readonly AlienFxMappingCatalog _catalog = AlienFxMappingCatalog.LoadDefault();
    private Dictionary<string, LightingDeviceProfile> _profilesByKey = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, NativeLightingDevice> _nativeByProfileKey = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LightingDeviceProfile> _availableProfiles = [];
    private LightingDeviceProfile? _currentProfile;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return _profilesByKey.Count > 0;
            }
        }
    }

    public LightingDeviceProfile? CurrentProfile
    {
        get
        {
            lock (_sync)
            {
                return _currentProfile;
            }
        }
    }

    public IReadOnlyList<LightingDeviceProfile> AvailableProfiles
    {
        get
        {
            lock (_sync)
            {
                return _availableProfiles;
            }
        }
    }

    public string? DeviceDescription
    {
        get
        {
            lock (_sync)
            {
                return _currentProfile?.HardwareDescription;
            }
        }
    }

    public bool Probe(out string? error)
    {
        lock (_sync)
        {
            return RefreshProfiles(out error);
        }
    }

    public bool Apply(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
            return TryApplySnapshot(snapshot, persistDefault: false, out error);
        }
    }

    public bool Maintain(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
            return TryApplySnapshot(snapshot, persistDefault: false, out error);
        }
    }

    public bool PersistDefaultState(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
            LightingDeviceProfile? profile = ResolveProfile(snapshot.DeviceKey);
            if (profile is null)
            {
                return RefreshProfiles(out error) && TryApplySnapshot(snapshot, persistDefault: true, out error);
            }

            if (!profile.SupportsPersistence)
            {
                error = null;
                return true;
            }

            return TryApplySnapshot(snapshot, persistDefault: true, out error);
        }
    }

    public void Dispose()
    {
    }

    private bool TryApplySnapshot(LightingSnapshot snapshot, bool persistDefault, out string? error)
    {
        error = null;
        if (!RefreshProfiles(out error))
        {
            return false;
        }

        LightingDeviceProfile? profile = ResolveProfile(snapshot.DeviceKey);
        if (profile is null)
        {
            error = "No supported AlienFX lighting profile is currently available.";
            return false;
        }

        if (!_nativeByProfileKey.TryGetValue(profile.DeviceKey, out NativeLightingDevice? nativeDevice))
        {
            error = "The selected AlienFX hardware profile is no longer available.";
            return false;
        }

        Dictionary<int, LightingZoneDefinition> zonesById = profile.Zones.ToDictionary(static zone => zone.ZoneId);
        List<NativeLightingActionRequest> actions = [];
        foreach (ZoneLightingState zoneState in snapshot.ZoneStates.OrderBy(static state => state.ZoneId))
        {
            if (!zonesById.TryGetValue(zoneState.ZoneId, out LightingZoneDefinition? zone))
            {
                continue;
            }

            byte actionType = zoneState.Effect switch
            {
                LightingEffect.Static => 0,
                LightingEffect.Pulse => 1,
                LightingEffect.Morph => 2,
                _ => 0,
            };

            foreach (byte lightId in zone.LightIds.Distinct())
            {
                actions.Add(new NativeLightingActionRequest(
                    lightId,
                    actionType,
                    zoneState.Speed,
                    zoneState.PrimaryColor,
                    zoneState.SecondaryColor));
            }
        }

        if (actions.Count == 0)
        {
            error = "No valid lighting targets were resolved for the selected profile.";
            return false;
        }

        int brightness = profile.SupportsBrightness
            ? snapshot.Enabled ? Math.Clamp(snapshot.Brightness, 0, 100) : 0
            : -1;

        IReadOnlyList<byte> brightnessLightIds = profile.Zones
            .SelectMany(static zone => zone.LightIds)
            .Distinct()
            .ToArray();

        AlienFxNativeBridge.ApplyLighting(
            nativeDevice.DeviceId,
            actions,
            brightnessLightIds,
            brightness,
            persistDefault && profile.SupportsPersistence,
            includePowerLights: profile.Zones.Any(static zone => zone.IsPowerOrIndicator));

        _currentProfile = profile;
        error = null;
        return true;
    }

    private bool RefreshProfiles(out string? error)
    {
        error = null;

        IReadOnlyList<NativeLightingDevice> nativeDevices;
        try
        {
            nativeDevices = AlienFxNativeBridge.EnumerateDevices();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _profilesByKey = new Dictionary<string, LightingDeviceProfile>(StringComparer.OrdinalIgnoreCase);
            _nativeByProfileKey = new Dictionary<string, NativeLightingDevice>(StringComparer.OrdinalIgnoreCase);
            _availableProfiles = [];
            _currentProfile = null;
            return false;
        }

        Dictionary<string, LightingDeviceProfile> nextProfiles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NativeLightingDevice> nextNative = new(StringComparer.OrdinalIgnoreCase);
        List<LightingDeviceProfile> nextOrderedProfiles = [];
        int duplicateDeviceCount = nativeDevices
            .GroupBy(static device => (device.VendorId, device.ProductId))
            .Count(static group => group.Count() > 1);

        foreach (NativeLightingDevice nativeDevice in nativeDevices)
        {
            IReadOnlyList<LightingDeviceProfile> templates = _catalog.FindProfiles(nativeDevice.VendorId, nativeDevice.ProductId);
            if (templates.Count == 0)
            {
                continue;
            }

            bool includeHardwareName = duplicateDeviceCount > 0 || templates.Count > 1;
            foreach (LightingDeviceProfile template in templates)
            {
                string runtimeKey = $"{nativeDevice.DeviceId}|{template.DeviceKey}";
                string displayName = includeHardwareName
                    ? $"{template.DisplayName} [{nativeDevice.Description}]"
                    : template.DisplayName;

                LightingDeviceProfile runtimeProfile = template with
                {
                    DeviceKey = runtimeKey,
                    DisplayName = displayName,
                    Protocol = AlienFxNativeBridge.GetProtocolLabel(nativeDevice.ApiVersion),
                    SupportsBrightness = nativeDevice.SupportsBrightness,
                    SupportsPersistence = nativeDevice.SupportsPersistence,
                    SupportsGlobalEffects = nativeDevice.SupportsGlobalEffects,
                    HardwareId = nativeDevice.DeviceId,
                    HardwareDescription = nativeDevice.Description,
                };

                nextProfiles[runtimeKey] = runtimeProfile;
                nextNative[runtimeKey] = nativeDevice;
                nextOrderedProfiles.Add(runtimeProfile);
            }
        }

        if (nextProfiles.Count == 0)
        {
            _profilesByKey = nextProfiles;
            _nativeByProfileKey = nextNative;
            _availableProfiles = [];
            _currentProfile = null;
            error = "No supported AlienFX lighting devices were found.";
            return false;
        }

        string? preferredKey = _currentProfile?.DeviceKey;
        _profilesByKey = nextProfiles;
        _nativeByProfileKey = nextNative;
        _availableProfiles = nextOrderedProfiles;
        _currentProfile = ResolveProfile(preferredKey)
            ?? _availableProfiles.FirstOrDefault();

        return true;
    }

    private LightingDeviceProfile? ResolveProfile(string? deviceKey)
    {
        if (!string.IsNullOrWhiteSpace(deviceKey) &&
            _profilesByKey.TryGetValue(deviceKey, out LightingDeviceProfile? exact))
        {
            return exact;
        }

        if (!string.IsNullOrWhiteSpace(deviceKey))
        {
            LightingDeviceProfile? migrated = _profilesByKey
                .Values
                .FirstOrDefault(profile =>
                    profile.DeviceKey.EndsWith($"|{deviceKey}", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(BuildLegacyProfileKey(profile), deviceKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(profile.SurfaceName, deviceKey, StringComparison.OrdinalIgnoreCase));

            if (migrated is not null)
            {
                return migrated;
            }
        }

        return _currentProfile
            ?? _availableProfiles.FirstOrDefault();
    }

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
