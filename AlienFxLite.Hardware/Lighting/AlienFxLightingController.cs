using AlienFxLite.Contracts;

namespace AlienFxLite.Hardware.Lighting;

public sealed class AlienFxLightingController : IDisposable
{
    private const ushort VendorId = 0x187C;
    private const ushort ProductId = 0x0550;
    private const byte SavedLightingBlockId = 0x61;

    private static readonly IReadOnlyDictionary<LightingZone, byte[]> ZoneLightIds = new Dictionary<LightingZone, byte[]>
    {
        [LightingZone.KbLeft] = [0, 1, 2, 12, 13, 14],
        [LightingZone.KbCenter] = [3, 4, 5, 15, 16, 17],
        [LightingZone.KbRight] = [6, 7, 8, 18, 19, 20],
        [LightingZone.KbNumPad] = [9, 10, 11, 21, 22, 23],
    };

    private static readonly byte[] AllLightIds = ZoneLightIds.Values.SelectMany(static ids => ids).Distinct().ToArray();

    private readonly object _sync = new();
    private AlienFxV4Device? _device;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return _device is not null;
            }
        }
    }

    public string? DeviceDescription
    {
        get
        {
            lock (_sync)
            {
                return _device?.DevicePath;
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
            if (TryApply(snapshot, out error))
            {
                return true;
            }

            ResetConnection();
            return TryApply(snapshot, out error);
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

    private bool TryApply(LightingSnapshot snapshot, out string? error)
    {
        error = null;
        if (!EnsureConnected(out error))
        {
            return false;
        }

        if (_device is null)
        {
            error = "Lighting device is unavailable.";
            return false;
        }

        if (!_device.Reset(out error))
        {
            return false;
        }

        bool needsUpdate = false;
        foreach (IGrouping<RgbColor, ZoneLightingState> staticGroup in snapshot.ZoneStates
                     .Where(static state => state.Effect == LightingEffect.Static)
                     .GroupBy(static state => state.PrimaryColor))
        {
            byte[] zoneIds = staticGroup
                .SelectMany(static state => ZoneLightIds.TryGetValue(state.Zone, out byte[]? lightIds) ? lightIds : [])
                .Distinct()
                .ToArray();

            if (zoneIds.Length == 0)
            {
                continue;
            }

            if (!_device.ApplyStaticZones(staticGroup.Key, zoneIds, out error))
            {
                return false;
            }
        }

        foreach (ZoneLightingState zoneState in snapshot.ZoneStates.OrderBy(static state => state.Zone))
        {
            if (zoneState.Effect == LightingEffect.Static)
            {
                continue;
            }

            if (!ZoneLightIds.TryGetValue(zoneState.Zone, out byte[]? lightIds))
            {
                continue;
            }

            foreach (byte lightId in lightIds)
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
        return _device.SetBrightness(brightness, AllLightIds, out error);
    }

    private bool EnsureConnected(out string? error)
    {
        error = null;
        if (_device is not null)
        {
            return true;
        }

        _device = AlienFxV4Device.Open(VendorId, ProductId, out error);
        return _device is not null;
    }

    private bool TryPersistDefaultState(LightingSnapshot snapshot, out string? error)
    {
        error = null;
        if (!EnsureConnected(out error))
        {
            return false;
        }

        if (_device is null)
        {
            error = "Lighting device is unavailable.";
            return false;
        }

        if (!_device.BeginSavedLightingBlock(SavedLightingBlockId, out error))
        {
            return false;
        }

        foreach (ZoneLightingState zoneState in snapshot.ZoneStates.OrderBy(static state => state.Zone))
        {
            if (!ZoneLightIds.TryGetValue(zoneState.Zone, out byte[]? lightIds))
            {
                continue;
            }

            bool success = zoneState.Effect == LightingEffect.Static
                ? _device.ApplyStaticZones(zoneState.PrimaryColor, lightIds, out error)
                : PersistAnimatedZone(zoneState, lightIds, out error);

            if (!success)
            {
                return false;
            }
        }

        return _device.CommitSavedLightingBlock(SavedLightingBlockId, out error);
    }

    private void ResetConnection()
    {
        _device?.Dispose();
        _device = null;
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
