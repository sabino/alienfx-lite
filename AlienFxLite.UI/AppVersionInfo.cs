using System.Reflection;

namespace AlienFxLite.UI;

internal static class AppVersionInfo
{
    private static readonly Assembly EntryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
    private static readonly Version CurrentVersionValue = EntryAssembly.GetName().Version ?? new Version(0, 1, 0, 0);

    public static string CurrentVersion => $"{CurrentVersionValue.Major}.{CurrentVersionValue.Minor}.{CurrentVersionValue.Build}";
}
