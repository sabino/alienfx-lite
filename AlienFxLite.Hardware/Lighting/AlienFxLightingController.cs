using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Hid;
using AlienFxLite.Hardware.Native;

namespace AlienFxLite.Hardware.Lighting;

public sealed class AlienFxLightingController : IDisposable
{
    private const byte SavedLightingBlockId = 0x61;

    private readonly object _sync = new();
    private readonly AlienFxMappingCatalog _catalog = AlienFxMappingCatalog.LoadDefault();
    private Dictionary<string, LightingDeviceProfile> _profilesByKey = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, NativeLightingDevice> _nativeByProfileKey = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AlienFxV4Device> _v4DevicesByPath = new(StringComparer.OrdinalIgnoreCase);
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

            if (profile.ApiVersion == 4)
            {
                if (TryApplyV4Snapshot(profile, snapshot, resetDevice: true, forceUpdate: false, out error))
                {
                    return true;
                }

                string? v4Error = error;
                bool nativeResult = TryApplyNativeSnapshot(profile, snapshot, persistDefault: false, out error);
                if (!nativeResult && !string.IsNullOrWhiteSpace(v4Error))
                {
                    error = $"{v4Error} Native fallback: {error}";
                }

                return nativeResult;
            }

            return TryApplyNativeSnapshot(profile, snapshot, persistDefault: false, out error);
        }
    }

    public bool Maintain(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
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

            if (profile.ApiVersion == 4)
            {
                if (TryApplyV4Snapshot(profile, snapshot, resetDevice: false, forceUpdate: true, out error))
                {
                    return true;
                }

                string? v4Error = error;
                bool nativeResult = TryApplyNativeSnapshot(profile, snapshot, persistDefault: false, out error);
                if (!nativeResult && !string.IsNullOrWhiteSpace(v4Error))
                {
                    error = $"{v4Error} Native fallback: {error}";
                }

                return nativeResult;
            }

            return TryApplyNativeSnapshot(profile, snapshot, persistDefault: false, out error);
        }
    }

    public bool PersistDefaultState(LightingSnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
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

            if (!profile.SupportsPersistence)
            {
                error = null;
                return true;
            }

            if (profile.ApiVersion == 4)
            {
                if (TryPersistV4DefaultState(profile, snapshot, out error))
                {
                    return true;
                }

                string? v4Error = error;
                bool nativeResult = TryApplyNativeSnapshot(profile, snapshot, persistDefault: true, out error);
                if (!nativeResult && !string.IsNullOrWhiteSpace(v4Error))
                {
                    error = $"{v4Error} Native fallback: {error}";
                }

                return nativeResult;
            }

            return TryApplyNativeSnapshot(profile, snapshot, persistDefault: true, out error);
        }
    }

    public void Dispose()
    {
        foreach (AlienFxV4Device device in _v4DevicesByPath.Values)
        {
            device.Dispose();
        }

        _v4DevicesByPath.Clear();
    }

    private bool TryApplyNativeSnapshot(LightingDeviceProfile profile, LightingSnapshot snapshot, bool persistDefault, out string? error)
    {
        error = null;

        if (!_nativeByProfileKey.TryGetValue(profile.DeviceKey, out NativeLightingDevice? nativeDevice))
        {
            error = "The selected AlienFX hardware profile is no longer available.";
            return false;
        }

        if (TryApplyGlobalEffect(profile, nativeDevice, snapshot, out error))
        {
            _currentProfile = profile;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
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

            RgbColor primaryColor = ScaleColor(zoneState.PrimaryColor, zoneState.Brightness);
            RgbColor? secondaryColor = zoneState.SecondaryColor is { } secondary
                ? ScaleColor(secondary, zoneState.Brightness)
                : null;

            byte actionType = zoneState.Effect switch
            {
                LightingEffect.Static => 0,
                LightingEffect.Pulse => 1,
                LightingEffect.Morph => 2,
                LightingEffect.Breathing => 3,
                LightingEffect.Spectrum => 4,
                LightingEffect.Rainbow => 5,
                _ => 0,
            };

            foreach (byte lightId in zone.LightIds.Distinct())
            {
                actions.Add(new NativeLightingActionRequest(
                    lightId,
                    actionType,
                    zoneState.Speed,
                    primaryColor,
                    secondaryColor));
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

        try
        {
            AlienFxNativeBridge.ApplyLighting(
                nativeDevice.DeviceId,
                actions,
                brightnessLightIds,
                brightness,
                persistDefault && profile.SupportsPersistence,
                includePowerLights: profile.Zones.Any(static zone => zone.IsPowerOrIndicator));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        _currentProfile = profile;
        error = null;
        return true;
    }

    private bool TryApplyV4Snapshot(
        LightingDeviceProfile profile,
        LightingSnapshot snapshot,
        bool resetDevice,
        bool forceUpdate,
        out string? error)
    {
        error = null;
        LightingSnapshot normalized = NormalizeSnapshot(profile, snapshot with { DeviceKey = profile.DeviceKey });
        if (!TryGetV4Device(profile, out AlienFxV4Device? device, out error) || device is null)
        {
            return false;
        }

        if (resetDevice && !device.Reset(out error))
        {
            return false;
        }

        Dictionary<int, LightingZoneDefinition> zonesById = profile.Zones.ToDictionary(static zone => zone.ZoneId);
        bool needsUpdate = forceUpdate;
        foreach (IGrouping<RgbColor, ZoneLightingState> staticGroup in normalized.ZoneStates
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

            if (!device.ApplyStaticZones(staticGroup.Key, lightIds, out error))
            {
                return false;
            }

            needsUpdate = true;
        }

        foreach (ZoneLightingState zoneState in normalized.ZoneStates.OrderBy(static state => state.ZoneId))
        {
            if (zoneState.Effect == LightingEffect.Static ||
                !zonesById.TryGetValue(zoneState.ZoneId, out LightingZoneDefinition? zoneDefinition))
            {
                continue;
            }

            foreach (byte lightId in zoneDefinition.LightIds.Distinct())
            {
                if (!device.ApplyLight(zoneState, lightId, out error))
                {
                    return false;
                }
            }

            needsUpdate = true;
        }

        if (needsUpdate && !device.UpdateColors(out error))
        {
            return false;
        }

        int brightness = normalized.Enabled ? normalized.Brightness : 0;
        byte[] allLightIds = profile.Zones.SelectMany(static zone => zone.LightIds).Distinct().ToArray();
        if (!device.SetBrightness(brightness, allLightIds, out error))
        {
            return false;
        }

        _currentProfile = profile;
        return true;
    }

    private bool TryPersistV4DefaultState(LightingDeviceProfile profile, LightingSnapshot snapshot, out string? error)
    {
        error = null;
        LightingSnapshot normalized = NormalizeSnapshot(profile, snapshot with { DeviceKey = profile.DeviceKey });
        if (!TryGetV4Device(profile, out AlienFxV4Device? device, out error) || device is null)
        {
            return false;
        }

        if (!device.BeginSavedLightingBlock(SavedLightingBlockId, out error))
        {
            return false;
        }

        Dictionary<int, LightingZoneDefinition> zonesById = profile.Zones.ToDictionary(static zone => zone.ZoneId);
        foreach (ZoneLightingState zoneState in normalized.ZoneStates.OrderBy(static state => state.ZoneId))
        {
            if (!zonesById.TryGetValue(zoneState.ZoneId, out LightingZoneDefinition? zoneDefinition))
            {
                continue;
            }

            bool success = zoneState.Effect == LightingEffect.Static
                ? device.ApplyStaticZones(zoneState.PrimaryColor, zoneDefinition.LightIds.Distinct().ToArray(), out error)
                : PersistAnimatedV4Zone(device, zoneState, zoneDefinition.LightIds.Distinct().ToArray(), out error);

            if (!success)
            {
                return false;
            }
        }

        if (!device.CommitSavedLightingBlock(SavedLightingBlockId, out error))
        {
            return false;
        }

        device.SetStartupLightingBlock(SavedLightingBlockId, out _);
        return true;
    }

    private static bool TryApplyGlobalEffect(
        LightingDeviceProfile profile,
        NativeLightingDevice nativeDevice,
        LightingSnapshot snapshot,
        out string? error)
    {
        error = null;
        if (nativeDevice.ApiVersion != 5)
        {
            return false;
        }

        ZoneLightingState[] enabledStates = snapshot.ZoneStates
            .Where(static state => state.Enabled)
            .OrderBy(static state => state.ZoneId)
            .ToArray();

        ZoneLightingState? animated = enabledStates.FirstOrDefault(static state => LightingEffectCatalog.IsAnimated(state.Effect));
        if (animated is null)
        {
            return false;
        }

        if (enabledStates.Length != profile.Zones.Count)
        {
            error = "API v5 animation effects apply to the whole surface. Select every zone before applying this effect.";
            return false;
        }

        bool consistent = enabledStates.All(state =>
            state.Effect == animated.Effect &&
            state.Speed == animated.Speed &&
            state.PrimaryColor.Equals(animated.PrimaryColor) &&
            Nullable.Equals(state.SecondaryColor, animated.SecondaryColor));

        if (!consistent)
        {
            error = "API v5 animation effects require one shared effect, speed, and color set across the whole surface.";
            return false;
        }

        if (!LightingEffectCatalog.SupportsEffect(profile, animated.Effect))
        {
            error = $"The selected effect '{animated.Effect}' is not supported by this API v5 surface.";
            return false;
        }

        int brightness = profile.SupportsBrightness
            ? snapshot.Enabled ? Math.Clamp(snapshot.Brightness, 0, 100) : 0
            : -1;

        try
        {
            AlienFxNativeBridge.ApplyGlobalEffect(
                nativeDevice.DeviceId,
                animated.Effect,
                animated.Speed,
                animated.PrimaryColor,
                animated.SecondaryColor,
                brightness);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    private static bool PersistAnimatedV4Zone(
        AlienFxV4Device device,
        ZoneLightingState zoneState,
        IReadOnlyList<byte> lightIds,
        out string? error)
    {
        error = null;
        foreach (byte lightId in lightIds)
        {
            if (!device.ApplyLight(zoneState, lightId, out error))
            {
                return false;
            }
        }

        return device.UpdateColors(out error);
    }

    private bool RefreshProfiles(out string? error)
    {
        error = null;

        List<NativeLightingDevice> nativeDevices = [];
        string? nativeEnumerationError = null;
        try
        {
            nativeDevices = AlienFxNativeBridge.EnumerateDevices().ToList();
        }
        catch (Exception ex)
        {
            nativeEnumerationError = ex.Message;
        }

        IReadOnlyList<NativeLightingDevice> hidFallbackDevices = EnumerateFallbackHidDevices(nativeDevices);
        List<NativeLightingDevice> discoveredDevices = nativeDevices
            .Concat(hidFallbackDevices)
            .ToList();

        Dictionary<string, LightingDeviceProfile> nextProfiles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NativeLightingDevice> nextNative = new(StringComparer.OrdinalIgnoreCase);
        List<LightingDeviceProfile> nextOrderedProfiles = [];
        int duplicateDeviceCount = discoveredDevices
            .GroupBy(static device => (device.VendorId, device.ProductId))
            .Count(static group => group.Count() > 1);

        foreach (NativeLightingDevice nativeDevice in discoveredDevices)
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
                    ApiVersion = nativeDevice.ApiVersion,
                    Protocol = AlienFxNativeBridge.GetProtocolLabel(nativeDevice.ApiVersion),
                    SupportsBrightness = nativeDevice.SupportsBrightness,
                    SupportsPersistence = nativeDevice.SupportsPersistence,
                    SupportsGlobalEffects = nativeDevice.SupportsGlobalEffects,
                    SupportedEffects = LightingCapabilityResolver.GetSupportedEffects(nativeDevice.ApiVersion),
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
            error = nativeEnumerationError ?? "No supported AlienFX lighting devices were found.";
            return false;
        }

        string? preferredKey = _currentProfile?.DeviceKey;
        _profilesByKey = nextProfiles;
        _nativeByProfileKey = nextNative;
        _availableProfiles = nextOrderedProfiles;
        _currentProfile = ResolveProfile(preferredKey)
            ?? _availableProfiles.FirstOrDefault();
        error = null;

        return true;
    }

    private IReadOnlyList<NativeLightingDevice> EnumerateFallbackHidDevices(IReadOnlyList<NativeLightingDevice> nativeDevices)
    {
        HashSet<string> knownPaths = nativeDevices
            .Select(static device => device.DevicePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return HidNative.EnumerateDevices()
            .Where(static device => device.OutputReportLength == 34)
            .Where(device => !knownPaths.Contains(device.DevicePath))
            .Where(device => _catalog.FindProfiles(device.VendorId, device.ProductId).Count > 0)
            .Select(device => new NativeLightingDevice(
                $"hid::{device.VendorId:X4}:{device.ProductId:X4}:{device.DevicePath}",
                $"HID VID_{device.VendorId:X4}&PID_{device.ProductId:X4}",
                device.DevicePath,
                device.VendorId,
                device.ProductId,
                4,
                SupportsBrightness: true,
                SupportsPersistence: true,
                SupportsGlobalEffects: false))
            .ToArray();
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
            string normalizedKey = ExtractTemplateKey(deviceKey);
            LightingDeviceProfile? migrated = _profilesByKey
                .Values
                .FirstOrDefault(profile =>
                    string.Equals(ExtractTemplateKey(profile.DeviceKey), normalizedKey, StringComparison.OrdinalIgnoreCase) ||
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

    private bool TryGetV4Device(LightingDeviceProfile profile, out AlienFxV4Device? device, out string? error)
    {
        error = null;
        device = null;

        if (!_nativeByProfileKey.TryGetValue(profile.DeviceKey, out NativeLightingDevice? nativeDevice) ||
            string.IsNullOrWhiteSpace(nativeDevice.DevicePath))
        {
            ResetV4Connections();
            error = $"No exact AlienFX API v4 HID path is available for '{profile.DisplayName}'.";
            return false;
        }

        if (_v4DevicesByPath.TryGetValue(nativeDevice.DevicePath, out AlienFxV4Device? existing))
        {
            device = existing;
            return true;
        }

        HidDeviceInfo? candidate = HidNative.EnumerateDevices()
            .FirstOrDefault(deviceInfo => string.Equals(deviceInfo.DevicePath, nativeDevice.DevicePath, StringComparison.OrdinalIgnoreCase));

        if (candidate is null)
        {
            ResetV4Connections();
            error = $"The AlienFX API v4 HID path '{nativeDevice.DevicePath}' is no longer present.";
            return false;
        }

        AlienFxV4Device? opened = AlienFxV4Device.Open(candidate, out error);
        if (opened is null)
        {
            return false;
        }

        _v4DevicesByPath[candidate.DevicePath] = opened;
        device = opened;
        return true;
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

        if (profile.ApiVersion == 5)
        {
            ZoneLightingState? animated = zones.FirstOrDefault(static zone => LightingEffectCatalog.IsAnimated(zone.Effect));
            if (animated is not null)
            {
                zones = profile.Zones
                    .Select(zone => animated with { ZoneId = zone.ZoneId, Enabled = true })
                    .OrderBy(static zone => zone.ZoneId)
                    .ToList();
            }
        }

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
            SecondaryColor = LightingEffectCatalog.SupportsSecondaryColor(effect) ? state.SecondaryColor : null,
            Speed = Math.Clamp(state.Speed, 0, 100),
            Brightness = Math.Clamp(state.Brightness, 0, 100),
            Palette = LightingEffectCatalog.SupportsPalette(effect)
                ? NormalizePalette(state.Palette)
                : null,
        };
    }

    private static ZoneLightingState CreateDefaultZoneState(int zoneId) =>
        new(zoneId, LightingEffect.Static, new RgbColor(255, 255, 255), null, 50, true, 100, null);

    private static IReadOnlyList<RgbColor> NormalizePalette(IReadOnlyList<RgbColor>? palette)
    {
        IReadOnlyList<RgbColor> normalized = palette is { Count: > 0 }
            ? palette
            : LightingEffectCatalog.DefaultSpectrumPalette;

        return normalized
            .Take(7)
            .ToArray();
    }

    private static RgbColor ScaleColor(RgbColor color, int brightness)
    {
        double factor = Math.Clamp(brightness, 0, 100) / 100d;
        return new RgbColor(
            (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));
    }

    private void ResetV4Connections()
    {
        foreach (AlienFxV4Device device in _v4DevicesByPath.Values)
        {
            device.Dispose();
        }

        _v4DevicesByPath.Clear();
    }

    private static string BuildLegacyProfileKey(LightingDeviceProfile profile) =>
        $"{profile.VendorId:X4}:{profile.ProductId:X4}:{SanitizeKey(profile.SurfaceName)}";

    private static string ExtractTemplateKey(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return string.Empty;
        }

        int separator = deviceKey.LastIndexOf('|');
        return separator >= 0 && separator + 1 < deviceKey.Length
            ? deviceKey[(separator + 1)..]
            : deviceKey;
    }

    private static string SanitizeKey(string value)
    {
        char[] chars = value
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_')
            .ToArray();

        return new string(chars).Trim('_');
    }
}
