using System;
using System.Runtime.InteropServices;

namespace LinkLauncher.Services;

internal readonly record struct MousePressTarget(IntPtr Window, IntPtr Root, uint ProcessId, uint ThreadId)
{
    internal bool IsValid => Window != IntPtr.Zero && Root != IntPtr.Zero && ProcessId != 0 && ThreadId != 0;
}

internal interface IMouseCancellationPlatform
{
    IntPtr WindowAtPhysicalPoint(int x, int y);
    IntPtr RootWindow(IntPtr window);
    bool TryGetIdentity(IntPtr window, out uint processId, out uint threadId);
    bool TryGetCapture(uint threadId, out IntPtr window);
    bool NotifyCancelMode(IntPtr window);
}

/// <summary>
/// 呼び出しとして使った通常右押下に、標準操作のキャンセルを要求します。
/// 解放の注入や座標移動は行いません。アプリ独自の押下状態の終了は保証しません。
/// </summary>
internal static class MousePressCancellation
{
    private static readonly IMouseCancellationPlatform Win32 = new Win32MouseCancellationPlatform();

    internal static MousePressTarget Capture(int x, int y, uint ownProcessId) =>
        Capture(x, y, ownProcessId, Win32);

    internal static MousePressTarget Capture(int x, int y, uint ownProcessId, IMouseCancellationPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        IntPtr window = platform.WindowAtPhysicalPoint(x, y);
        if (!TryGetIdentity(platform, window, out uint process, out uint thread) || process == ownProcessId)
            return default;

        IntPtr root = platform.RootWindow(window);
        if (!TryGetIdentity(platform, root, out uint rootProcess, out _) || rootProcess != process)
            return default;

        return new MousePressTarget(window, root, process, thread);
    }

    internal static bool TryCancel(MousePressTarget target, int x, int y) =>
        TryCancel(target, x, y, Win32);

    internal static bool TryCancel(MousePressTarget target, int x, int y, IMouseCancellationPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (!target.IsValid || !MatchesOrigin(platform, target.Window, target) || !MatchesRoot(platform, target.Root, target))
            return false;

        IntPtr current = platform.WindowAtPhysicalPoint(x, y);
        if (current == IntPtr.Zero || platform.RootWindow(current) != target.Root ||
            !TryGetIdentity(platform, current, out uint currentProcess, out _) || currentProcess != target.ProcessId)
            return false;

        // GUIThreadInfo is queried for the original press thread, so its capture HWND must still match both IDs.
        if (!platform.TryGetCapture(target.ThreadId, out IntPtr capture)) return false;
        if (capture != IntPtr.Zero && !MatchesOrigin(platform, capture, target)) return false;

        if (capture != IntPtr.Zero && !platform.NotifyCancelMode(capture)) return false;
        if (target.Window != capture && !platform.NotifyCancelMode(target.Window)) return false;
        if (target.Root != target.Window && target.Root != capture && !platform.NotifyCancelMode(target.Root)) return false;
        return true;
    }

    private static bool MatchesOrigin(IMouseCancellationPlatform platform, IntPtr window, MousePressTarget target) =>
        TryGetIdentity(platform, window, out uint process, out uint thread) &&
        process == target.ProcessId && thread == target.ThreadId;

    private static bool MatchesRoot(IMouseCancellationPlatform platform, IntPtr root, MousePressTarget target) =>
        root == target.Root && TryGetIdentity(platform, root, out uint process, out _) && process == target.ProcessId;

    private static bool TryGetIdentity(IMouseCancellationPlatform platform, IntPtr window, out uint processId, out uint threadId)
    {
        if (window == IntPtr.Zero || !platform.TryGetIdentity(window, out processId, out threadId) ||
            processId == 0 || threadId == 0)
        {
            processId = 0;
            threadId = 0;
            return false;
        }

        return true;
    }

    private sealed class Win32MouseCancellationPlatform : IMouseCancellationPlatform
    {
        private const uint GaRoot = 2;
        private const uint WmCancelMode = 0x001F;

        public IntPtr WindowAtPhysicalPoint(int x, int y) => DesktopHitTest.WindowAtPhysicalPoint(x, y);

        public IntPtr RootWindow(IntPtr window) => window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, GaRoot);

        public bool TryGetIdentity(IntPtr window, out uint processId, out uint threadId)
        {
            if (window == IntPtr.Zero)
            {
                processId = 0;
                threadId = 0;
                return false;
            }

            threadId = GetWindowThreadProcessId(window, out processId);
            return threadId != 0 && processId != 0;
        }

        public bool TryGetCapture(uint threadId, out IntPtr window)
        {
            var info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref info))
            {
                window = IntPtr.Zero;
                return false;
            }

            window = info.Capture;
            return true;
        }

        // SendNotifyMessageの成功は要求送信の受付を示すだけで、処理完了やモード解除のACKではない。
        public bool NotifyCancelMode(IntPtr window) =>
            SendNotifyMessage(window, WmCancelMode, UIntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public RECT CaretRect;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}
