using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LinkLauncher.Services;

internal static class Program
{
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr DpiAwarenessContextUnaware = new(-1);
    private static readonly IntPtr DpiAwarenessContextSystemAware = new(-2);
    private static readonly IntPtr DpiAwarenessContextPerMonitorV2 = new(-4);
    private static readonly WindowProcedure WindowProc = DefWindowProc;

    [STAThread]
    private static int Main()
    {
        string className = "LinkLauncherDesktopHitTestChecks_" + Guid.NewGuid().ToString("N");
        IntPtr module = GetModuleHandle(null);
        bool registered = false;
        var windows = new List<TestWindow>();
        try
        {
            Require(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DpiAwarenessContextPerMonitorV2),
                "検証プロセスの初期thread DPI contextがPerMonitorV2ではありません。");
            Console.WriteLine("PASS 初期thread DPI context = PerMonitorV2");

            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProc),
                Instance = module,
                ClassName = className
            };
            Require(RegisterClassEx(ref windowClass) != 0, "検証用Win32 classを登録できません。");
            registered = true;

            List<MonitorBounds> monitors = EnumerateMonitors();
            Require(monitors.Count > 0, "ディスプレイを列挙できません。");
            foreach (MonitorBounds monitor in monitors)
            {
                int width = monitor.Right - monitor.Left;
                int height = monitor.Bottom - monitor.Top;
                Require(width > 2 && height > 2, "有効なサイズのディスプレイがありません。");
                int x = monitor.Left + width / 2;
                int y = monitor.Top + height / 2;
                IntPtr hwnd = CreateWindowEx(
                    WsExNoActivate | WsExToolWindow | WsExTopmost,
                    className,
                    "DesktopHitTestChecks",
                    WsPopup | WsVisible,
                    x - Math.Min(48, width / 2),
                    y - Math.Min(48, height / 2),
                    Math.Min(96, width),
                    Math.Min(96, height),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    module,
                    IntPtr.Zero);
                Require(hwnd != IntPtr.Zero, "検証用Win32 windowを作成できません。");
                int outsideX = monitor.Left + Math.Min(8, width - 1);
                int outsideY = monitor.Top + Math.Min(8, height - 1);
                int windowLeft = x - Math.Min(48, width / 2);
                int windowTop = y - Math.Min(48, height / 2);
                windows.Add(new TestWindow(hwnd, x, y, outsideX, outsideY,
                    windowLeft, windowTop, windowLeft + Math.Min(96, width), windowTop + Math.Min(96, height)));
            }

            for (int i = 0; i < windows.Count; i++)
            {
                TestWindow window = windows[i];
                (int outsideX, int outsideY) = FindOutsidePoint(monitors[i], windows);
                windows[i] = window with { OutsideX = outsideX, OutsideY = outsideY };
            }

            CheckHits("PerMonitorV2", windows, compareLegacy: false);
            CheckWithTemporaryContext("SystemAware", DpiAwarenessContextSystemAware, windows, compareLegacy: true);
            CheckWithTemporaryContext("Unaware", DpiAwarenessContextUnaware, windows, compareLegacy: false);
            Console.WriteLine($"PASS DesktopHitTestChecks ({windows.Count} monitor(s), 3 DPI context(s))");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.Message);
            return 1;
        }
        finally
        {
            foreach (TestWindow window in windows)
                if (window.Handle != IntPtr.Zero) DestroyWindow(window.Handle);
            if (registered) UnregisterClass(className, module);
        }
    }

    private static void CheckWithTemporaryContext(string name, IntPtr context, List<TestWindow> windows, bool compareLegacy)
    {
        IntPtr original = GetThreadDpiAwarenessContext();
        IntPtr previous = SetThreadDpiAwarenessContext(context);
        Require(previous != IntPtr.Zero, name + " contextへ変更できません。");
        try
        {
            Require(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), context), name + " contextへ切り替わりません。");
            CheckHits(name, windows, compareLegacy);
        }
        finally
        {
            Require(SetThreadDpiAwarenessContext(original) != IntPtr.Zero, "thread DPI contextを元に戻せません。");
        }
    }

    private static void CheckHits(string name, List<TestWindow> windows, bool compareLegacy)
    {
        IntPtr originalContext = GetThreadDpiAwarenessContext();
        int legacyDifferences = 0;
        foreach (TestWindow window in windows)
        {
            BringToFront(window.Handle);
            uint insideProcess = DesktopHitTest.ProcessAtPhysicalPoint(window.X, window.Y);
            if (insideProcess != (uint)Environment.ProcessId)
            {
                IntPtr original = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorV2);
                try
                {
                    GetWindowRect(window.Handle, out NativeRect rect);
                    Console.WriteLine($"INFO expected window={window.Handle}, physical rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}, caller result PID={insideProcess}, PMv2 result PID={DesktopHitTest.ProcessAtPhysicalPoint(window.X, window.Y)}");
                }
                finally { SetThreadDpiAwarenessContext(original); }
            }
            Require(insideProcess == (uint)Environment.ProcessId,
                $"{name}: ディスプレイ内の物理座標で自windowを検出できません ({window.X}, {window.Y})。");
            uint outsideProcess = DesktopHitTest.ProcessAtPhysicalPoint(window.OutsideX, window.OutsideY);
            Require(outsideProcess != (uint)Environment.ProcessId,
                $"{name}: window外の物理座標が自プロセスを指しています ({window.OutsideX}, {window.OutsideY})。");
            Require(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), originalContext),
                name + ": プロセス照会が呼び出し側のDPI contextを変更しています。");

            if (compareLegacy)
            {
                IntPtr legacyWindow = WindowFromPoint(new Point(window.X, window.Y));
                GetWindowThreadProcessId(legacyWindow, out uint legacyProcess);
                if (legacyProcess != (uint)Environment.ProcessId) legacyDifferences++;
            }
        }

        Console.WriteLine($"PASS {name}: 各ディスプレイの内外座標");
        if (compareLegacy)
            Console.WriteLine($"INFO 旧WindowFromPointの不一致: {legacyDifferences}/{windows.Count} monitor(s)");
    }

    private static void BringToFront(IntPtr hwnd)
    {
        Require(SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow), "検証用windowを前面にできません。");
    }

    private static (int X, int Y) FindOutsidePoint(MonitorBounds monitor, List<TestWindow> windows)
    {
        int[] xs = { monitor.Left + 1, monitor.Right - 2, monitor.Left + (monitor.Right - monitor.Left) / 4,
            monitor.Left + 3 * (monitor.Right - monitor.Left) / 4 };
        int[] ys = { monitor.Top + 1, monitor.Bottom - 2, monitor.Top + (monitor.Bottom - monitor.Top) / 4,
            monitor.Top + 3 * (monitor.Bottom - monitor.Top) / 4 };
        foreach (int y in ys)
        foreach (int x in xs)
        {
            bool insideTestWindow = false;
            foreach (TestWindow window in windows)
                if (x >= window.Left && x < window.Right && y >= window.Top && y < window.Bottom)
                {
                    insideTestWindow = true;
                    break;
                }
            if (!insideTestWindow) return (x, y);
        }
        throw new InvalidOperationException("自プロセスの検証用window外に座標を確保できません。");
    }

    private static List<MonitorBounds> EnumerateMonitors()
    {
        var monitors = new List<MonitorBounds>();
        MonitorEnumeration callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            monitors.Add(new MonitorBounds(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom));
            return true;
        };
        Require(EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero), "ディスプレイを列挙できません。");
        GC.KeepAlive(callback);
        return monitors;
    }

    private static IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcNative(hwnd, message, wParam, lParam);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom);
    private readonly record struct TestWindow(IntPtr Handle, int X, int Y, int OutsideX, int OutsideY,
        int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string? ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }

    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnumeration(IntPtr monitor, IntPtr deviceContext, IntPtr clip, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcNative(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr deviceContext,
        IntPtr clip, MonitorEnumeration callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
