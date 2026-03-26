using System.Reflection;
using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Lighting;
using Microsoft.Win32.SafeHandles;

const uint GenericRead = 0x80000000;
const uint GenericWrite = 0x40000000;
const uint FileShareRead = 0x00000001;
const uint FileShareWrite = 0x00000002;
const uint OpenExisting = 3;

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern SafeFileHandle CreateFile(
    string fileName,
    uint desiredAccess,
    uint shareMode,
    IntPtr securityAttributes,
    uint creationDisposition,
    uint flagsAndAttributes,
    IntPtr templateFile);

[System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true)]
static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

[System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true)]
static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern bool DeviceIoControl(
    SafeFileHandle hDevice,
    uint dwIoControlCode,
    byte[] lpInBuffer,
    int nInBufferSize,
    byte[]? lpOutBuffer,
    int nOutBufferSize,
    out uint lpBytesReturned,
    IntPtr lpOverlapped);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern bool WriteFile(
    SafeFileHandle hFile,
    byte[] lpBuffer,
    int nNumberOfBytesToWrite,
    out uint lpNumberOfBytesWritten,
    IntPtr lpOverlapped);

const uint IoctlHidSetOutputReport = 0x000B0195;
const uint IoctlHidSetFeature = 0x000B0190;

static int ParseHexColor(string value)
{
    string trimmed = value.Trim().TrimStart('#');
    return Convert.ToInt32(trimmed, 16);
}

static RgbColor ToColor(string value)
{
    int rgb = ParseHexColor(value);
    return new RgbColor(
        (byte)((rgb >> 16) & 0xFF),
        (byte)((rgb >> 8) & 0xFF),
        (byte)(rgb & 0xFF));
}

static LightingSnapshot BuildSnapshot(
    LightingDeviceProfile profile,
    IReadOnlyCollection<int>? selectedZones,
    LightingEffect effect,
    RgbColor primary,
    RgbColor? secondary,
    int speed)
{
    List<ZoneLightingState> zones = profile.Zones
        .OrderBy(zone => zone.ZoneId)
        .Select(zone => new ZoneLightingState(
            zone.ZoneId,
            effect,
            primary,
            effect == LightingEffect.Morph ? secondary : null,
            speed,
            selectedZones is null || selectedZones.Count == 0 || selectedZones.Contains(zone.ZoneId)))
        .ToList();

    return new LightingSnapshot(
        Enabled: true,
        Brightness: 100,
        KeepAlive: true,
        DeviceKey: profile.DeviceKey,
        ZoneStates: zones);
}

static int RunRawV4Probe(RgbColor color)
{
    const ushort vendorId = 0x187C;
    const ushort productId = 0x0550;
    byte[] zoneIds = [0, 1, 2, 3];

    Assembly hardwareAssembly = typeof(AlienFxLightingController).Assembly;
    Type hidNativeType = hardwareAssembly.GetType("AlienFxLite.Hardware.Hid.HidNative", throwOnError: true)!;
    Type v4DeviceType = hardwareAssembly.GetType("AlienFxLite.Hardware.Lighting.AlienFxV4Device", throwOnError: true)!;

    MethodInfo enumerateDevices = hidNativeType.GetMethod("EnumerateDevices", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not resolve HID enumerator.");

    List<object> devices = (((IEnumerable<object>?)enumerateDevices.Invoke(null, null))
        ?? throw new InvalidOperationException("HID enumeration returned null."))
        .ToList();

    List<object> candidates = devices.Where(device =>
        Convert.ToUInt16(device.GetType().GetProperty("VendorId")!.GetValue(device)) == vendorId &&
        Convert.ToUInt16(device.GetType().GetProperty("ProductId")!.GetValue(device)) == productId &&
        Convert.ToInt16(device.GetType().GetProperty("OutputReportLength")!.GetValue(device)) == 34)
        .ToList();

    if (candidates.Count == 0)
    {
        Console.WriteLine("Raw v4 probe: no matching VID_187C/PID_0550 HID device found.");
        return 3;
    }

    Console.WriteLine($"Raw v4 HID candidates: {candidates.Count}");

    static bool InvokeWithOutString(object instance, string methodName, object?[] args, out string? error)
    {
        MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found on {instance.GetType().FullName}.");

        bool result = (bool)(method.Invoke(instance, args) ?? false);
        error = args[^1] as string;
        return result;
    }

    static void BruteForceResetReportIds(string devicePath)
    {
        byte[] resetRemoveCommand = [6, 0x03, 0x21, 0x00, 0x04, 0x00, 0xff];
        Console.WriteLine("  Report-ID brute force:");
        for (byte reportId = 0; reportId < 4; reportId++)
        {
            using SafeFileHandle handle = CreateFile(
                devicePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                Console.WriteLine($"    rid={reportId}: open failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                continue;
            }

            byte[] buffer = new byte[34];
            Array.Copy(resetRemoveCommand, buffer, resetRemoveCommand[0] + 1);
            buffer[0] = reportId;

            bool hidOk = HidD_SetOutputReport(handle, buffer, buffer.Length);
            int hidError = hidOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            bool ioctlOk = DeviceIoControl(handle, IoctlHidSetOutputReport, buffer, buffer.Length, null, 0, out _, IntPtr.Zero);
            int ioctlError = ioctlOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            bool featureOk = HidD_SetFeature(handle, buffer, buffer.Length);
            int featureError = featureOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            bool ioctlFeatureOk = DeviceIoControl(handle, IoctlHidSetFeature, buffer, buffer.Length, null, 0, out _, IntPtr.Zero);
            int ioctlFeatureError = ioctlFeatureOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            bool writeOk = WriteFile(handle, buffer, buffer.Length, out uint written, IntPtr.Zero);
            int writeError = writeOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            Console.WriteLine($"    rid={reportId}: hid={hidOk} err={hidError}; ioctl={ioctlOk} err={ioctlError}; feature={featureOk} err={featureError}; ioctlFeature={ioctlFeatureOk} err={ioctlFeatureError}; write={writeOk} err={writeError} bytes={written}");
        }
    }

    static object? TryOpenCurrent(Type v4DeviceType, MethodInfo open, object candidate, out string? error)
    {
        object?[] openArgs = [candidate, null];
        object? device = open.Invoke(null, openArgs);
        error = openArgs[1] as string;
        return device;
    }

    static object? TryOpenLegacy(Type v4DeviceType, object candidate, out string? error)
    {
        error = null;
        string devicePath = Convert.ToString(candidate.GetType().GetProperty("DevicePath")!.GetValue(candidate)) ?? string.Empty;
        short reportLength = Convert.ToInt16(candidate.GetType().GetProperty("OutputReportLength")!.GetValue(candidate));

        SafeFileHandle handle = CreateFile(
            devicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = $"CreateFile failed with Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.";
            return null;
        }

        return Activator.CreateInstance(v4DeviceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [handle, devicePath, (int)reportLength], null);
    }

    MethodInfo open = v4DeviceType.GetMethod("Open", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not resolve AlienFxV4Device.Open.");

    for (int index = 0; index < candidates.Count; index++)
    {
        object candidate = candidates[index];
        string devicePath = Convert.ToString(candidate.GetType().GetProperty("DevicePath")!.GetValue(candidate)) ?? string.Empty;
        Console.WriteLine($"[{index}] {devicePath}");

        object? device = TryOpenCurrent(v4DeviceType, open, candidate, out string? currentOpenError);
        string openMode = "current";
        if (device is null)
        {
            Console.WriteLine($"  Current open failed: {currentOpenError}");
            device = TryOpenLegacy(v4DeviceType, candidate, out string? legacyOpenError);
            openMode = "legacy";
            if (device is null)
            {
                Console.WriteLine($"  Legacy open failed: {legacyOpenError}");
                continue;
            }
        }

        using IDisposable disposable = (IDisposable)device;

        if (!InvokeWithOutString(device, "Reset", [null], out string? resetError))
        {
            Console.WriteLine($"  Reset failed ({openMode}): {resetError}");
            BruteForceResetReportIds(devicePath);

            if (!string.Equals(openMode, "legacy", StringComparison.Ordinal))
            {
                disposable.Dispose();
                device = TryOpenLegacy(v4DeviceType, candidate, out string? legacyOpenError);
                if (device is null)
                {
                    Console.WriteLine($"  Legacy open failed after reset failure: {legacyOpenError}");
                    continue;
                }

                using IDisposable legacyDisposable = (IDisposable)device;
                if (!InvokeWithOutString(device, "Reset", [null], out resetError))
                {
                    Console.WriteLine($"  Reset failed (legacy): {resetError}");
                    continue;
                }

                if (!InvokeWithOutString(device, "ApplyStaticZones", [color, zoneIds, null], out string? legacyApplyError))
                {
                    Console.WriteLine($"  Static apply failed (legacy): {legacyApplyError}");
                    continue;
                }

                if (!InvokeWithOutString(device, "UpdateColors", [null], out string? legacyUpdateError))
                {
                    Console.WriteLine($"  Update failed (legacy): {legacyUpdateError}");
                    continue;
                }

                if (!InvokeWithOutString(device, "SetBrightness", [100, zoneIds, null], out string? legacyBrightnessError))
                {
                    Console.WriteLine($"  Brightness failed (legacy): {legacyBrightnessError}");
                    continue;
                }

                Console.WriteLine($"  Success on candidate [{index}] using legacy open");
                return 0;
            }

            continue;
        }

        if (!InvokeWithOutString(device, "ApplyStaticZones", [color, zoneIds, null], out string? applyError))
        {
            Console.WriteLine($"  Static apply failed: {applyError}");
            continue;
        }

        if (!InvokeWithOutString(device, "UpdateColors", [null], out string? updateError))
        {
            Console.WriteLine($"  Update failed: {updateError}");
            continue;
        }

        if (!InvokeWithOutString(device, "SetBrightness", [100, zoneIds, null], out string? brightnessError))
        {
            Console.WriteLine($"  Brightness failed: {brightnessError}");
            continue;
        }

        Console.WriteLine($"  Success on candidate [{index}] using {openMode} open");
        return 0;
    }

    Console.WriteLine("Raw v4 probe failed on every matching HID collection.");
    return 4;
}

static int RunHidEnumerationDump()
{
    Assembly hardwareAssembly = typeof(AlienFxLightingController).Assembly;
    Type hidNativeType = hardwareAssembly.GetType("AlienFxLite.Hardware.Hid.HidNative", throwOnError: true)!;

    MethodInfo enumerateDevices = hidNativeType.GetMethod("EnumerateDevices", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not resolve HID enumerator.");

    List<object> devices = (((IEnumerable<object>?)enumerateDevices.Invoke(null, null))
        ?? throw new InvalidOperationException("HID enumeration returned null."))
        .ToList();

    Console.WriteLine($"HID devices opened by HidNative: {devices.Count}");
    foreach (object device in devices.OrderBy(device => Convert.ToUInt16(device.GetType().GetProperty("VendorId")!.GetValue(device)))
                                   .ThenBy(device => Convert.ToUInt16(device.GetType().GetProperty("ProductId")!.GetValue(device))))
    {
        Type type = device.GetType();
        ushort vendorId = Convert.ToUInt16(type.GetProperty("VendorId")!.GetValue(device));
        ushort productId = Convert.ToUInt16(type.GetProperty("ProductId")!.GetValue(device));
        int reportLength = Convert.ToInt32(type.GetProperty("OutputReportLength")!.GetValue(device));
        string path = Convert.ToString(type.GetProperty("DevicePath")!.GetValue(device)) ?? string.Empty;
        Console.WriteLine($"{vendorId:X4}:{productId:X4} report={reportLength} path={path}");
    }

    return 0;
}

if (args.Any(static arg => string.Equals(arg, "--rawv4", StringComparison.OrdinalIgnoreCase)))
{
    string? colorArg = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
    RgbColor color = colorArg is not null ? ToColor(colorArg) : new RgbColor(255, 255, 255);
    return RunRawV4Probe(color);
}

if (args.Any(static arg => string.Equals(arg, "--list-hid", StringComparison.OrdinalIgnoreCase)))
{
    return RunHidEnumerationDump();
}

using AlienFxLightingController controller = new();
if (!controller.Probe(out string? probeError))
{
    Console.WriteLine($"Probe failed: {probeError}");
    return 1;
}

Console.WriteLine("Detected profiles:");
foreach (LightingDeviceProfile profile in controller.AvailableProfiles)
{
    Console.WriteLine($"- {profile.DeviceKey}");
    Console.WriteLine($"  Name: {profile.DisplayName}");
    Console.WriteLine($"  HID:  {profile.HardwareDescription}");
    Console.WriteLine($"  API:  {profile.ApiVersion} ({profile.Protocol})");
    Console.WriteLine($"  Zones: {string.Join(", ", profile.Zones.OrderBy(zone => zone.ZoneId).Select(zone => $"{zone.ZoneId}:{zone.Name}"))}");
}

LightingDeviceProfile? selected = controller.AvailableProfiles.FirstOrDefault();
if (selected is null)
{
    Console.WriteLine("No profile selected.");
    return 1;
}

string? primaryArg = null;
string? secondaryArg = null;
string? effectArg = null;
string? speedArg = null;
string? zonesArg = null;
for (int index = 0; index < args.Length; index++)
{
    if (!args[index].StartsWith("--", StringComparison.Ordinal))
    {
        primaryArg ??= args[index];
        continue;
    }

    if (index + 1 >= args.Length)
    {
        continue;
    }

    switch (args[index])
    {
        case "--primary":
            primaryArg = args[index + 1];
            index++;
            break;
        case "--secondary":
            secondaryArg = args[index + 1];
            index++;
            break;
        case "--effect":
            effectArg = args[index + 1];
            index++;
            break;
        case "--speed":
            speedArg = args[index + 1];
            index++;
            break;
        case "--zones":
            zonesArg = args[index + 1];
            index++;
            break;
    }
}

LightingEffect effect = string.IsNullOrWhiteSpace(effectArg)
    ? LightingEffect.Static
    : Enum.Parse<LightingEffect>(effectArg, ignoreCase: true);
RgbColor primary = string.IsNullOrWhiteSpace(primaryArg) ? new RgbColor(255, 255, 255) : ToColor(primaryArg);
RgbColor? secondary = string.IsNullOrWhiteSpace(secondaryArg) ? null : ToColor(secondaryArg);
int speed = string.IsNullOrWhiteSpace(speedArg) ? 50 : int.Parse(speedArg);
HashSet<int>? selectedZones = string.IsNullOrWhiteSpace(zonesArg)
    ? null
    : zonesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(int.Parse)
        .ToHashSet();

LightingSnapshot snapshot = BuildSnapshot(selected, selectedZones, effect, primary, secondary, speed);
if (!controller.Apply(snapshot, out string? applyError))
{
    Console.WriteLine($"Apply failed: {applyError}");
    return 2;
}

Console.WriteLine($"Applied {effect} to {selected.DisplayName}.");
return 0;
