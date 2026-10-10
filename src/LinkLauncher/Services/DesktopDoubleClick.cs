using System;
using System.Runtime.InteropServices;
using System.Threading;
using Accessibility;

namespace LinkLauncher.Services;

/// <summary>
/// デスクトップのアイコン一覧上で、指定点が空白かを調べます。
/// UIA/MSAAの照会を含むため、呼び出し元はUI threadや入力hookではなくMTA workerにします。
/// </summary>
public static class DesktopDoubleClick
{
    private const int RoleSystemList = 0x21;

    /// <summary>
    /// 指定した物理画面座標がShellのデスクトップ一覧上の空白ならtrueを返します。
    /// 不明なwindow、アクセシビリティ照会の失敗、STA threadではfalseを返します。
    /// </summary>
    public static bool IsBlankAt(int x, int y)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA) return false;
        return IsBlankAt(x, y, NativeDesktopDoubleClickApi.Instance);
    }

    internal static bool IsBlankAt(int x, int y, IDesktopDoubleClickApi api)
    {
        try
        {
            if (!api.IsDesktopViewAtPhysicalPoint(x, y)) return false;
            return api.TryGetAccessibleRoleAtPhysicalPoint(x, y, out int role)
                && role == RoleSystemList;
        }
        catch
        {
            // 入力起点の照会で不確実な場合は起動しません。
            return false;
        }
    }
}

/// <summary>
/// 低レベルhookで使う、単純な左ダブルクリック状態です。OS APIや入力生成は行いません。
/// </summary>
internal sealed class DesktopDoubleClickState
{
    private enum ClickPhase
    {
        Idle,
        FirstDown,
        AwaitingSecondDown,
        SecondDown
    }

    private ClickPhase _phase;
    private int _firstDownX;
    private int _firstDownY;
    private uint _firstDownTime;
    private uint _doubleClickMilliseconds;
    private int _toleranceX;
    private int _toleranceY;
    private int _currentDownX;
    private int _currentDownY;

    public bool IsPressed => _phase is ClickPhase.FirstDown or ClickPhase.SecondDown;

    /// <summary>
    /// 新しい左DOWNを記録します。既存の1回目クリックに合う2回目ならtrueです。
    /// timeはGetTickCount系の32-bit millisecond tickを渡します。
    /// </summary>
    public bool Down(int x, int y, uint time, uint doubleClickMilliseconds, int toleranceX, int toleranceY)
    {
        toleranceX = Math.Max(0, toleranceX);
        toleranceY = Math.Max(0, toleranceY);

        bool isSecondDown = _phase == ClickPhase.AwaitingSecondDown
            && IsWithinDoubleClickTime(time)
            && IsWithinRectangle(x, y, _firstDownX, _firstDownY,
                Math.Min(_toleranceX, toleranceX), Math.Min(_toleranceY, toleranceY));

        if (isSecondDown)
        {
            _phase = ClickPhase.SecondDown;
            _currentDownX = x;
            _currentDownY = y;
            _toleranceX = Math.Min(_toleranceX, toleranceX);
            _toleranceY = Math.Min(_toleranceY, toleranceY);
            return true;
        }

        _phase = ClickPhase.FirstDown;
        _firstDownX = x;
        _firstDownY = y;
        _firstDownTime = time;
        _doubleClickMilliseconds = doubleClickMilliseconds;
        _toleranceX = toleranceX;
        _toleranceY = toleranceY;
        _currentDownX = x;
        _currentDownY = y;
        return false;
    }

    /// <summary>
    /// 左ボタン押下中の移動が許容矩形を越えたら候補を破棄します。
    /// </summary>
    public void Move(int x, int y, bool leftButtonDown)
    {
        if (!leftButtonDown || (_phase != ClickPhase.FirstDown && _phase != ClickPhase.SecondDown)) return;
        if (!IsWithinRectangle(x, y, _currentDownX, _currentDownY, _toleranceX, _toleranceY)) Cancel();
    }

    /// <summary>
    /// LEFT UPを記録し、2回目のクリックが完了した場合だけtrueを返します。
    /// </summary>
    public bool Up(int x, int y)
    {
        if (_phase == ClickPhase.FirstDown)
        {
            if (!IsWithinRectangle(x, y, _currentDownX, _currentDownY, _toleranceX, _toleranceY))
            {
                Cancel();
                return false;
            }

            _phase = ClickPhase.AwaitingSecondDown;
            return false;
        }

        if (_phase == ClickPhase.SecondDown)
        {
            bool completed = IsWithinRectangle(x, y, _currentDownX, _currentDownY, _toleranceX, _toleranceY);
            Cancel();
            return completed;
        }

        return false;
    }

    public void Cancel() => _phase = ClickPhase.Idle;

    private bool IsWithinDoubleClickTime(uint time)
    {
        uint elapsed = unchecked(time - _firstDownTime);
        return elapsed <= _doubleClickMilliseconds;
    }

    private static bool IsWithinRectangle(int x, int y, int centerX, int centerY, int toleranceX, int toleranceY)
    {
        return Math.Abs((long)x - centerX) <= toleranceX
            && Math.Abs((long)y - centerY) <= toleranceY;
    }
}

internal interface IDesktopDoubleClickApi
{
    bool IsDesktopViewAtPhysicalPoint(int x, int y);
    bool TryGetAccessibleRoleAtPhysicalPoint(int x, int y, out int role);
}

internal sealed class NativeDesktopDoubleClickApi : IDesktopDoubleClickApi
{
    private const int GaRoot = 2;
    private const uint CoInitMultithreaded = 0x0;
    private static readonly NativeDesktopDoubleClickApi Shared = new();

    public static NativeDesktopDoubleClickApi Instance => Shared;

    private NativeDesktopDoubleClickApi() { }

    public bool IsDesktopViewAtPhysicalPoint(int x, int y)
    {
        IntPtr target = DesktopHitTest.WindowAtPhysicalPoint(x, y);
        if (target == IntPtr.Zero) return false;

        IntPtr shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero) return false;
        if (GetWindowThreadProcessId(shellWindow, out uint shellProcessId) == 0 || shellProcessId == 0) return false;
        if (GetWindowThreadProcessId(target, out uint targetProcessId) == 0 || targetProcessId != shellProcessId) return false;

        IntPtr root = GetAncestor(target, GaRoot);
        if (root == IntPtr.Zero) return false;
        bool rootIsShellDesktop = root == shellWindow;
        bool rootIsWorker = !rootIsShellDesktop
            && HasClassName(root, "WorkerW")
            && GetWindowThreadProcessId(root, out uint rootProcessId) != 0
            && rootProcessId == shellProcessId;
        if (!rootIsShellDesktop && !rootIsWorker) return false;

        bool foundShellView = false;
        bool foundListView = false;
        IntPtr current = target;
        for (int depth = 0; current != IntPtr.Zero && depth < 32; depth++)
        {
            if (HasClassName(current, "SHELLDLL_DefView")) foundShellView = true;
            if (HasClassName(current, "SysListView32")) foundListView = true;
            if (current == root) break;
            current = GetParent(current);
        }

        return foundShellView && foundListView;
    }

    public bool TryGetAccessibleRoleAtPhysicalPoint(int x, int y, out int role)
    {
        role = 0;
        int initializeResult = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        bool initializedHere = initializeResult == 0 || initializeResult == 1;
        if (!initializedHere) return false;

        try
        {
            int result = AccessibleObjectFromPoint(new NativePoint { X = x, Y = y },
                out IAccessible? accessible, out object childId);
            if (result < 0 || accessible is null) return false;

            try
            {
                object? roleValue = accessible.get_accRole(childId);
                if (roleValue is int roleNumber)
                {
                    role = roleNumber;
                    return true;
                }

                return false;
            }
            finally
            {
                if (Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible);
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static bool HasClassName(IntPtr window, string expected)
    {
        char[] className = new char[128];
        int length = GetClassName(window, className, className.Length);
        return length > 0 && string.Equals(new string(className, 0, length), expected, StringComparison.Ordinal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr window, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(
        NativePoint point,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible,
        [MarshalAs(UnmanagedType.Struct)] out object childId);
}
