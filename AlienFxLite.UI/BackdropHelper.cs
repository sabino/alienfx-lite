using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AlienFxLite.UI;

internal static class BackdropHelper
{
    private const int DwmaUseImmersiveDarkMode = 20;
    private const int DwmaWindowCornerPreference = 33;
    private const int DwmaSystemBackdropType = 38;

    private const int CornerRound = 2;
    private const int BackdropMainWindow = 2;

    public static void TryApply(Window window)
    {
        WindowInteropHelper helper = new(window);
        if (helper.Handle == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        int corner = CornerRound;
        int backdrop = BackdropMainWindow;

        DwmSetWindowAttribute(helper.Handle, DwmaUseImmersiveDarkMode, ref enabled, sizeof(int));
        DwmSetWindowAttribute(helper.Handle, DwmaWindowCornerPreference, ref corner, sizeof(int));
        DwmSetWindowAttribute(helper.Handle, DwmaSystemBackdropType, ref backdrop, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
