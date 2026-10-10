using System;
using System.Runtime.InteropServices;

namespace LinkLauncher.Services;

/// <summary>
/// 低レベルマウスフックと同じ物理画面座標で、入力先のプロセスを調べます。
/// </summary>
public static class DesktopHitTest
{
    private static readonly IntPtr PerMonitorV2 = new(-4);

    public static uint ProcessAtPhysicalPoint(int x, int y)
    {
        IntPtr target = WindowAtPhysicalPoint(x, y);
        if (target == IntPtr.Zero) return 0;
        return GetWindowThreadProcessId(target, out uint processId) != 0 ? processId : 0;
    }

    public static IntPtr WindowAtPhysicalPoint(int x, int y)
    {
        // 照会中だけDPIコンテキストも揃える。別のコンテキストから呼ばれても、
        // サブ画面の物理座標が仮想化されないようにし、呼び出し側の設定は戻す。
        IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorV2);
        try
        {
            return WindowFromPhysicalPoint(new POINT { X = x, Y = y });
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPhysicalPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
