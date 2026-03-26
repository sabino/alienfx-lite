using System.Management;

namespace AlienFxLite.Hardware.Fans;

public sealed class AwccFanController : IDisposable
{
    private static readonly string[] CommandList =
    [
        "Thermal_Information",
        "Thermal_Control",
        "GameShiftStatus",
        "GetFanSensors",
        "GetThermalInfo2",
        "SetThermalControl2",
        "TccControl",
        "MemoryOCControl",
    ];

    private static readonly byte[,] FunctionIds =
    {
        { 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 0, 3, 0, 6, 6, 6, 6, 7, 7 },
        { 5, 5, 5, 5, 5, 5, 6, 6, 2, 2, 5, 4, 5, 6, 6, 6, 6, 7, 7 },
    };

    private static readonly byte[] DeviceControls =
    [
        3,  // getPowerId
        5,  // getFanRpm
        6,  // getFanPercent
        0xC,// getFanBoost
        4,  // getTemp
        0xB,// getPowerMode
        2,  // setFanBoost
        1,  // setPowerMode
        2,  // getGMode
        1,  // setGMode
        2,  // getSystemId
        2,  // getFanSensor
        9,  // getMaxRpm
        1,  // getMaxTcc
        2,  // getMaxOffset
        3,  // getCurrentOffset
        4,  // setCurrentOffset
        2,  // getXmp
        3,  // setXmp
    ];

    private const int GetPowerId = 0;
    private const int GetFanRpm = 1;
    private const int GetFanBoost = 3;
    private const int GetPowerMode = 5;
    private const int SetFanBoost = 6;
    private const int SetPowerMode = 7;
    private const int GetSystemId = 10;

    private readonly object _sync = new();
    private readonly List<FanInfo> _fans = [];
    private readonly List<int> _availablePowerModes = [];

    private ManagementScope? _scope;
    private ManagementObject? _instance;
    private int _sysType = -1;

    public bool IsAvailable { get; private set; }

    public int SystemId { get; private set; }

    public IReadOnlyList<int> AvailablePowerModes => _availablePowerModes;

    public int FanCount => _fans.Count;

    public bool Probe(out string? error)
    {
        lock (_sync)
        {
            return TryProbe(out error);
        }
    }

    public int? GetCurrentPowerValue()
    {
        lock (_sync)
        {
            if (!EnsureAvailable(out _))
            {
                return null;
            }

            int value = CallMethod(GetPowerMode);
            return value >= 0 ? value : null;
        }
    }

    public int? GetDefaultAutomaticPowerValue()
    {
        lock (_sync)
        {
            return _availablePowerModes.FirstOrDefault(static value => value > 0);
        }
    }

    public IReadOnlyList<int> GetFanRpms()
    {
        lock (_sync)
        {
            if (!EnsureAvailable(out _))
            {
                return [];
            }

            return _fans.Select(fan => CallMethod(GetFanRpm, fan.Id)).ToArray();
        }
    }

    public IReadOnlyList<int> GetRawBoosts()
    {
        lock (_sync)
        {
            if (!EnsureAvailable(out _))
            {
                return [];
            }

            return _fans.Select(fan => CallMethod(GetFanBoost, fan.Id)).ToArray();
        }
    }

    public bool SetAutomatic(int? preferredRawPowerValue, out string? error)
    {
        lock (_sync)
        {
            error = null;
            if (!EnsureAvailable(out error))
            {
                return false;
            }

            List<int> candidates = [];
            if (preferredRawPowerValue is > 0)
            {
                candidates.Add(preferredRawPowerValue.Value);
            }

            foreach (int availableMode in _availablePowerModes.Where(static value => value > 0))
            {
                if (!candidates.Contains(availableMode))
                {
                    candidates.Add(availableMode);
                }
            }

            if (candidates.Count == 0)
            {
                error = "No automatic fan power mode was detected.";
                return false;
            }

            foreach (int powerValue in candidates)
            {
                int result = CallMethod(SetPowerMode, (byte)powerValue);
                if (result >= 0)
                {
                    return true;
                }
            }

            error = $"Failed to restore BIOS-controlled fan mode (tried {string.Join(", ", candidates)}).";
            return false;
        }
    }

    public bool SetMaxAll(out string? error)
    {
        lock (_sync)
        {
            if (!EnsureAvailable(out error))
            {
                return false;
            }

            return SetManualRawInternal(Enumerable.Repeat(100, _fans.Count).ToArray(), out error);
        }
    }

    public bool SetManualRaw(IReadOnlyList<int> rawBoostPerFan, out string? error)
    {
        lock (_sync)
        {
            if (!EnsureAvailable(out error))
            {
                return false;
            }

            return SetManualRawInternal(rawBoostPerFan, out error);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _instance?.Dispose();
            _scope = null;
            _instance = null;
            _sysType = -1;
            _fans.Clear();
            _availablePowerModes.Clear();
            IsAvailable = false;
        }
    }

    private bool SetManualRawInternal(IReadOnlyList<int> rawBoostPerFan, out string? error)
    {
        error = null;
        if (rawBoostPerFan.Count != _fans.Count)
        {
            error = $"Expected {_fans.Count} raw fan values, received {rawBoostPerFan.Count}.";
            return false;
        }

        if (CallMethod(SetPowerMode, 0) < 0)
        {
            error = "Failed to unlock manual fan control.";
            return false;
        }

        for (int fanIndex = 0; fanIndex < _fans.Count; fanIndex++)
        {
            int raw = Math.Clamp(rawBoostPerFan[fanIndex], 0, 100);
            if (CallMethod(SetFanBoost, _fans[fanIndex].Id, (byte)raw) < 0)
            {
                error = $"Failed to set boost for fan #{fanIndex}.";
                return false;
            }
        }

        return true;
    }

    private bool EnsureAvailable(out string? error)
    {
        if (IsAvailable && _instance is not null)
        {
            error = null;
            return true;
        }

        return TryProbe(out error);
    }

    private bool TryProbe(out string? error)
    {
        error = null;

        Dispose();

        try
        {
            _scope = new ManagementScope(@"\\.\ROOT\WMI");
            _scope.Connect();

            using ManagementClass managementClass = new(_scope, new ManagementPath("AWCCWmiMethodFunction"), null);
            ManagementObjectCollection instances = managementClass.GetInstances();
            _instance = instances.Cast<ManagementObject>().FirstOrDefault();
            if (_instance is null)
            {
                error = "AWCCWmiMethodFunction instance not found.";
                return false;
            }

            for (int candidateSysType = 0; candidateSysType < 2; candidateSysType++)
            {
                string methodName = CommandList[FunctionIds[candidateSysType, GetPowerId]];
                try
                {
                    using ManagementBaseObject inParams = _instance.GetMethodParameters(methodName);
                    if (inParams is not null)
                    {
                        _sysType = candidateSysType;
                        break;
                    }
                }
                catch (ManagementException)
                {
                }
            }

            if (_sysType < 0)
            {
                error = "No supported AWCC thermal method set was detected.";
                return false;
            }

            SystemId = CallMethod(GetSystemId, 2);
            _availablePowerModes.Add(0);

            int index = 0;
            while (true)
            {
                int functionId = CallMethod(GetPowerId, (byte)index);
                if (functionId <= 0)
                {
                    break;
                }

                int valueKind = functionId & 0xff;
                if (functionId > 0x8f)
                {
                    _availablePowerModes.Add(valueKind);
                }
                else if (functionId <= 0x8f)
                {
                    _fans.Add(new FanInfo((byte)valueKind, 0));
                }

                index++;
            }

            IsAvailable = _fans.Count > 0;
            if (!IsAvailable)
            {
                error = "No controllable fans were detected through AWCC WMI.";
            }

            return IsAvailable;
        }
        catch (ManagementException ex)
        {
            error = $"WMI error: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Access denied: {ex.Message}";
            return false;
        }
    }

    private int CallMethod(int commandIndex, byte arg1 = 0, byte arg2 = 0)
    {
        if (_instance is null || _sysType < 0)
        {
            return -1;
        }

        string methodName = CommandList[FunctionIds[_sysType, commandIndex]];
        try
        {
            using ManagementBaseObject inParams = _instance.GetMethodParameters(methodName);
            uint packedArgs = (uint)(DeviceControls[commandIndex] | (arg1 << 8) | (arg2 << 16));
            inParams["arg2"] = packedArgs;

            using ManagementBaseObject? outParams = _instance.InvokeMethod(methodName, inParams, null);
            if (outParams is null)
            {
                return -1;
            }

            object? result = outParams["argr"];
            return NormalizeResult(result);
        }
        catch (ManagementException)
        {
            IsAvailable = false;
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            IsAvailable = false;
            return -1;
        }
    }

    private static int NormalizeResult(object? result)
    {
        return result switch
        {
            null => -1,
            int value => value,
            uint value => unchecked((int)value),
            long value => unchecked((int)value),
            ulong value => unchecked((int)value),
            short value => value,
            ushort value => value,
            byte value => value,
            sbyte value => value,
            _ => Convert.ToInt32(result),
        };
    }

    private sealed record FanInfo(byte Id, byte Type);
}
