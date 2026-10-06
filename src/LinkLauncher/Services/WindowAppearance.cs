using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LinkLauncher.Services;

internal static class WindowAppearance
{
    public static void Bind(Window window)
    {
        void Update()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = ThemeManager.IsDark ? 1 : 0;
            // Windows paints the standard caption; unsupported builds keep their default.
            if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
        Action changed = Update;
        window.SourceInitialized += (_, _) => Update();
        ThemeManager.ThemeChanged += changed;
        window.Closed += (_, _) => ThemeManager.ThemeChanged -= changed;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
