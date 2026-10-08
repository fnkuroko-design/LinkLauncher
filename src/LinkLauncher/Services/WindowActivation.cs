using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LinkLauncher.Services;

/// <summary>
/// Windowsの前面化制限を尊重しながら、対象ウィンドウの表示と入力キューの調整を行います。
/// </summary>
public static class WindowActivation
{
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private const int GwHwndPrev = 3;
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
    /// 対象を前面化します。所有する可視ポップアップがあればそちらを優先し、
    /// 最後のZ順変更が前面化に失敗した場合は変更前の位置へ戻します。
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

        if (SetForegroundWindow(target))
        {
            return true;
        }

        if (TryActivateWithAttachedInput(target))
        {
            return true;
        }

        return TryActivateWithRestoredZOrder(target, isTopmost);
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

    private static bool TryActivateWithAttachedInput(IntPtr target)
    {
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(target, out _);
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        List<(uint Attach, uint AttachTo)> attached = new(2);

        try
        {
            AttachIfDifferent(currentThread, targetThread, attached);
            AttachIfDifferent(currentThread, foregroundThread, attached);
            return SetForegroundWindow(target);
        }
        finally
        {
            for (int index = attached.Count - 1; index >= 0; index--)
            {
                (uint attach, uint attachTo) = attached[index];
                AttachThreadInput(attach, attachTo, false);
            }
        }
    }

    private static void AttachIfDifferent(
        uint currentThread,
        uint otherThread,
        List<(uint Attach, uint AttachTo)> attached)
    {
        if (otherThread == 0 || otherThread == currentThread)
        {
            return;
        }

        foreach ((uint attach, uint attachTo) in attached)
        {
            if ((attach == currentThread && attachTo == otherThread)
                || (attach == otherThread && attachTo == currentThread))
            {
                return;
            }
        }

        if (AttachThreadInput(currentThread, otherThread, true))
        {
            attached.Add((currentThread, otherThread));
        }
    }

    private static bool TryActivateWithRestoredZOrder(IntPtr target, bool isTopmost)
    {
        IntPtr previous = GetWindow(target, GwHwndPrev);
        bool targetWasTopmost = isTopmost || IsTopmost(target);
        bool moved = SetWindowPos(
            target,
            HwndTop,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

        if (!moved)
        {
            return false;
        }

        bool activated = false;
        try
        {
            activated = SetForegroundWindow(target);
            return activated;
        }
        finally
        {
            if (!activated)
            {
                RestoreZOrder(target, previous, targetWasTopmost);
            }
        }
    }

    private static void RestoreZOrder(IntPtr target, IntPtr previous, bool targetWasTopmost)
    {
        if (previous != IntPtr.Zero
            && previous != target
            && IsWindow(previous)
            && IsTopmost(previous) == targetWasTopmost)
        {
            if (SetWindowPos(target, previous, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate))
            {
                return;
            }
        }

        SetWindowPos(
            target,
            targetWasTopmost ? HwndTopmost : HwndNotTopmost,
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
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attachInput);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
