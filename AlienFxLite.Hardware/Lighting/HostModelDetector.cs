using System.Management;

namespace AlienFxLite.Hardware.Lighting;

internal static class HostModelDetector
{
    private static readonly Lazy<HostIdentity> Current = new(DetectCore);

    public static HostIdentity Detect() => Current.Value;

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        char[] chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();

        return new string(chars);
    }

    public static HashSet<string> Tokenize(params string?[] values)
    {
        HashSet<string> tokens = new(StringComparer.Ordinal);
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            List<char> buffer = [];
            foreach (char ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer.Add(char.ToLowerInvariant(ch));
                    continue;
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                tokens.Add(new string([.. buffer]));
                buffer.Clear();
            }

            if (buffer.Count > 0)
            {
                tokens.Add(new string([.. buffer]));
            }
        }

        return tokens;
    }

    private static HostIdentity DetectCore()
    {
        string manufacturer = string.Empty;
        string model = string.Empty;
        string productName = string.Empty;

        try
        {
            using ManagementObjectSearcher computerSystem = new("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject instance in computerSystem.Get().OfType<ManagementObject>())
            {
                manufacturer = ReadProperty(instance, "Manufacturer");
                model = ReadProperty(instance, "Model");
                break;
            }
        }
        catch
        {
        }

        try
        {
            using ManagementObjectSearcher product = new("SELECT Name FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject instance in product.Get().OfType<ManagementObject>())
            {
                productName = ReadProperty(instance, "Name");
                break;
            }
        }
        catch
        {
        }

        return new HostIdentity(manufacturer, model, productName);
    }

    private static string ReadProperty(ManagementBaseObject instance, string propertyName) =>
        Convert.ToString(instance[propertyName])?.Trim() ?? string.Empty;
}

internal sealed record HostIdentity(string Manufacturer, string Model, string ProductName)
{
    public string NormalizedManufacturerKey { get; } = HostModelDetector.NormalizeKey(Manufacturer);

    public string NormalizedModelKey { get; } = HostModelDetector.NormalizeKey(Model);

    public string NormalizedProductKey { get; } = HostModelDetector.NormalizeKey(ProductName);

    public HashSet<string> Tokens { get; } = HostModelDetector.Tokenize(Manufacturer, Model, ProductName);

    public bool IsDell => NormalizedManufacturerKey.Contains("dell", StringComparison.Ordinal) ||
                          Tokens.Contains("dell");

    public bool IsAlienware => Tokens.Contains("alienware");

    public bool IsDellGSeries => IsDell &&
                                 (Tokens.Contains("g3") || Tokens.Contains("g5") || Tokens.Contains("g7"));
}
