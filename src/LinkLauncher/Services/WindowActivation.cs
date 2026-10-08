using System;
using System.Runtime.InteropServices;

namespace LinkLauncher.Services;

/// <summary>
/// Windowsの前面化制限を尊重しながら、対象ウィンドウのZ順と前面化を調整します。
/// </summary>
public static class WindowActivation
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// 前面ウィンドウ、所有モーダル、またはアプリ内ポップアップが自プロセスか確認します。
    /// </summary>
    public static bool IsProcessForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 指定したウィンドウが現在の前面ウィンドウか確認します。
    /// </summary>
    public static bool IsForegroundWindow(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero
            && IsWindow(hwnd)
            && GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// 所有する可視ポップアップがあればそれを優先し、前面にあるか確認します。
    /// </summary>
    public static bool IsTargetForeground(IntPtr owner)
    {
        if (owner == IntPtr.Zero || !IsWindow(owner))
        {
            return false;
        }

        return IsForegroundWindow(ResolveActivePopup(owner));
    }

    /// <summary>
    /// 対象をZ順の先頭へ移動してから前面化します。所有する可視ポップアップがあればそちらを優先します。
    /// 通常ウィンドウは一時的にTopmostへ移し、前面化後に通常帯の先頭へ戻します。
    /// </summary>
    public static bool TryActivate(IntPtr hwnd, bool isTopmost = false)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        IntPtr target = ResolveActivePopup(hwnd);
        if (target == IntPtr.Zero || !IsWindow(target))
        {
            return false;
        }

        bool preserveTopmost = isTopmost || IsTopmost(target);
        bool raised = SetWindowPos(target, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        if (!raised || !IsTopmost(target))
        {
            RestoreTopmostState(target, preserveTopmost);
            RequestForeground(target);
            return IsForegroundWindow(target);
        }

        RequestForeground(target);

        if (!preserveTopmost)
        {
            bool returnedToNormalBand = SetWindowPos(
                target,
                HwndNotTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate)
                && !IsTopmost(target);

            if (!returnedToNormalBand)
            {
                RestoreTopmostState(target, preserveTopmost);
            }
        }

        return IsForegroundWindow(target);
    }

    private static IntPtr ResolveActivePopup(IntPtr owner)
    {
        IntPtr popup = GetLastActivePopup(owner);
        if (popup == IntPtr.Zero || popup == owner || !IsWindow(popup) || !IsWindowVisible(popup))
        {
            return owner;
        }

        GetWindowThreadProcessId(popup, out uint popupProcessId);
        return popupProcessId == (uint)Environment.ProcessId ? popup : owner;
    }

    private static void RequestForeground(IntPtr target)
    {
        // SetForegroundWindowの戻り値だけではなく、目的のHWNDが実際に前面かで判定する。
        _ = SetForegroundWindow(target);
    }

    private static void RestoreTopmostState(IntPtr target, bool preserveTopmost)
    {
        if (!IsWindow(target) || IsTopmost(target) == preserveTopmost)
        {
            return;
        }

        SetWindowPos(
            target,
            preserveTopmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static bool IsTopmost(IntPtr hwnd) => (GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) != 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
}
