using AlienFxLite.Contracts;

namespace AlienFxLite.Hardware.Lighting;

public sealed class AlienFxLightingController : IDisposable
{
    private const ushort VendorId = 0x187C;
    private const ushort ProductId = 0x0550;

    private static readonly IReadOnlyDictionary<LightingZone, byte> ZoneIds = new Dictionary<LightingZone, byte>
    {
        [LightingZone.KbLeft] = 0,
        [LightingZone.KbCenter] = 1,
        [LightingZone.KbRight] = 2,
        [LightingZone.KbNumPad] = 3,
    };

    private static readonly byte[] AllLightIds = [0, 1, 2, 3];

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
                .Select(static state => ZoneIds.TryGetValue(state.Zone, out byte zoneId) ? zoneId : (byte?)null)
                .Where(static zoneId => zoneId.HasValue)
                .Select(static zoneId => zoneId!.Value)
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

            if (!ZoneIds.TryGetValue(zoneState.Zone, out byte zoneId))
            {
                continue;
            }

            if (!_device.ApplyZone(zoneState.Zone, zoneState, zoneId, out error))
            {
                return false;
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

    private void ResetConnection()
    {
        _device?.Dispose();
        _device = null;
    }
}
