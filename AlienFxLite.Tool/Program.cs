using System.Globalization;
using AlienFxLite.Contracts;

namespace AlienFxLite.Tool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        AlienFxLiteServiceClient client = new();

        try
        {
            return await DispatchAsync(client, args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(AlienFxLiteServiceClient client, string[] args)
    {
        if (args.Length == 1 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            StatusSnapshot status = await client.GetStatusAsync().ConfigureAwait(false);
            PrintStatus(status);
            return 0;
        }

        if (args.Length == 2 && args[0].Equals("service", StringComparison.OrdinalIgnoreCase) && args[1].Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            PingResponse response = await client.PingAsync().ConfigureAwait(false);
            Console.WriteLine($"Service version: {response.ServiceVersion}");
            Console.WriteLine($"Service time:    {response.ServiceTime:O}");
            return 0;
        }

        if (args.Length == 2 && args[0].Equals("fans", StringComparison.OrdinalIgnoreCase))
        {
            FanControlMode mode = args[1].ToLowerInvariant() switch
            {
                "auto" => FanControlMode.Auto,
                "max" => FanControlMode.Max,
                _ => throw new InvalidOperationException("Use 'fans auto' or 'fans max'."),
            };

            FanStatus status = await client.SetFanModeAsync(new SetFanModeRequest(mode)).ConfigureAwait(false);
            Console.WriteLine($"Fan mode: {status.Mode}");
            Console.WriteLine($"RPM:      {(status.Rpm.Count > 0 ? string.Join(", ", status.Rpm) : "n/a")}");
            return 0;
        }

        if (args.Length >= 2 && args[0].Equals("lights", StringComparison.OrdinalIgnoreCase) && args[1].Equals("apply", StringComparison.OrdinalIgnoreCase))
        {
            SetLightingStateRequest request = ParseLightApplyRequest(args.Skip(2).ToArray());
            LightingSnapshot snapshot = await client.SetLightingStateAsync(request).ConfigureAwait(false);
            Console.WriteLine($"Brightness: {snapshot.Brightness}");
            Console.WriteLine($"KeepAlive:  {snapshot.KeepAlive}");
            Console.WriteLine($"Zones:      {string.Join(", ", snapshot.ZoneStates.Select(static zone => zone.Zone))}");
            return 0;
        }

        PrintUsage();
        return 1;
    }

    private static SetLightingStateRequest ParseLightApplyRequest(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Lighting arguments must be provided as --name value pairs.");
            }

            options[args[i]] = args[i + 1];
        }

        if (!options.TryGetValue("--zones", out string? zonesValue) || string.IsNullOrWhiteSpace(zonesValue))
        {
            throw new InvalidOperationException("--zones is required.");
        }

        if (!options.TryGetValue("--effect", out string? effectValue) || string.IsNullOrWhiteSpace(effectValue))
        {
            throw new InvalidOperationException("--effect is required.");
        }

        string? primaryValue = options.GetValueOrDefault("--primary") ?? options.GetValueOrDefault("--color");
        if (string.IsNullOrWhiteSpace(primaryValue))
        {
            throw new InvalidOperationException("--primary is required.");
        }

        LightingEffect effect = Enum.Parse<LightingEffect>(effectValue, ignoreCase: true);
        RgbColor primary = ParseColor(primaryValue);
        RgbColor? secondary = options.TryGetValue("--secondary", out string? secondaryValue) ? ParseColor(secondaryValue) : null;
        int speed = options.TryGetValue("--speed", out string? speedValue) ? int.Parse(speedValue, CultureInfo.InvariantCulture) : 50;
        int? brightness = options.TryGetValue("--brightness", out string? brightnessValue) ? int.Parse(brightnessValue, CultureInfo.InvariantCulture) : null;
        bool? keepAlive = options.TryGetValue("--keepalive", out string? keepAliveValue) ? bool.Parse(keepAliveValue) : null;
        bool? enabled = options.TryGetValue("--enabled", out string? enabledValue) ? bool.Parse(enabledValue) : null;

        List<LightingZone> zones = zonesValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseZone)
            .ToList();

        return new SetLightingStateRequest(zones, effect, primary, secondary, speed, brightness, keepAlive, enabled);
    }

    private static LightingZone ParseZone(string value) =>
        value.ToLowerInvariant() switch
        {
            "left" or "kb-left" => LightingZone.KbLeft,
            "center" or "kb-center" or "middle" => LightingZone.KbCenter,
            "right" or "kb-right" => LightingZone.KbRight,
            "numpad" or "kb-numpad" => LightingZone.KbNumPad,
            _ => throw new InvalidOperationException($"Unknown zone '{value}'."),
        };

    private static RgbColor ParseColor(string value)
    {
        string normalized = value.Trim().TrimStart('#');
        if (normalized.Length != 6)
        {
            throw new InvalidOperationException($"Color '{value}' must be in RRGGBB format.");
        }

        return new RgbColor(
            byte.Parse(normalized[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static void PrintStatus(StatusSnapshot status)
    {
        Console.WriteLine($"Lighting enabled: {status.Lighting.Enabled}");
        Console.WriteLine($"Brightness:       {status.Lighting.Brightness}");
        Console.WriteLine($"KeepAlive:        {status.Lighting.KeepAlive}");
        Console.WriteLine($"Lighting device:  {(status.Devices.LightingAvailable ? status.Devices.LightingDevice : "unavailable")}");
        Console.WriteLine($"Fan provider:     {(status.Devices.FanAvailable ? status.Devices.FanProvider : "unavailable")}");
        Console.WriteLine($"Fan mode:         {status.Fan.Mode}");
        Console.WriteLine($"Fan RPM:          {(status.Fan.Rpm.Count > 0 ? string.Join(", ", status.Fan.Rpm) : "n/a")}");
        Console.WriteLine("Zones:");
        foreach (ZoneLightingState zone in status.Lighting.ZoneStates.OrderBy(static item => item.Zone))
        {
            Console.WriteLine($"  - {zone.Zone}: {zone.Effect} #{zone.PrimaryColor.R:X2}{zone.PrimaryColor.G:X2}{zone.PrimaryColor.B:X2}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AlienFxLite.Tool usage:");
        Console.WriteLine("  status");
        Console.WriteLine("  service ping");
        Console.WriteLine("  fans auto");
        Console.WriteLine("  fans max");
        Console.WriteLine("  lights apply --zones left,center --effect static --primary FF5500 [--secondary 0000FF] [--speed 50] [--brightness 100] [--keepalive true]");
    }
}
