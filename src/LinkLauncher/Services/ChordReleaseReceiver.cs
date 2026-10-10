using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LinkLauncher.Services;

/// <summary>
/// Chordで開始した後、nativeの左右ボタンUPを小さな受信windowで受け取ります。
/// UI threadで生成、使用、破棄してください。入力の生成やカーソル移動は行いません。
/// </summary>
public sealed class ChordReleaseReceiver : IDisposable
{
    internal const uint WatchdogTimerId = 0x43485244;
    internal const uint WatchdogMilliseconds = 2000;

    private enum Phase
    {
        Idle,
        Arming,
        Active,
        Finishing,
        Disposed
    }

    private readonly IChordReleaseReceiverPlatform _platform;
    private readonly int _ownerThreadId;
    private readonly IntPtr _window;
    private Phase _phase;
    private bool _leftUpReceived;
    private bool _rightUpReceived;
    private bool _timerMayBeArmed;
    private bool _disposed;
    private bool _notifying;
    private IntPtr _previousForeground;
    private long _sessionSequence;
    private long _completedSession;
    private bool _completedSuccessfully;

    /// <summary>
    /// 現セッションの両UP受領でtrue、中断・失敗・timeout・Disposeでfalseを一度通知します。
    /// </summary>
    public event Action<bool>? Completed;

    /// <summary>
    /// setup中の再入と、左右UPの両方をまだ受信していないセッションを示します。
    /// trueだけではnative foreground/capture成立を意味せず、TryBeginの戻り値で確認します。
    /// </summary>
    public bool IsActive
    {
        get
        {
            VerifyOwnerThread();
            return _phase is Phase.Arming or Phase.Active;
        }
    }

    /// <summary>
    /// UI threadに受信windowを非表示で作成します。
    /// </summary>
    public ChordReleaseReceiver()
        : this(new Win32ChordReleaseReceiverPlatform())
    {
    }

    internal ChordReleaseReceiver(IChordReleaseReceiverPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _ownerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            _window = _platform.CreateWindow(HandleWindowMessage);
            if (_window == IntPtr.Zero)
            {
                throw new InvalidOperationException("Chord release receiver windowを作成できませんでした。");
            }
        }
        catch
        {
            try { _platform.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 指定した物理座標に受信windowを表示し、foregroundとcaptureを確認して受信を開始します。
    /// foreground/captureを確認できない場合はfalseを返し、left DOWNの消費を許可しません。
    /// </summary>
    public bool TryBegin(int x, int y)
    {
        VerifyOwnerThread();
        if (_disposed || _notifying || _phase != Phase.Idle)
        {
            return false;
        }

        long session = unchecked(++_sessionSequence);
        if (session == 0)
        {
            session = unchecked(++_sessionSequence);
        }

        _leftUpReceived = false;
        _rightUpReceived = false;
        _timerMayBeArmed = false;
        _previousForeground = IntPtr.Zero;

        // Show/foreground/capture APIs can reenter the message hook. Arm the protocol first
        // so any UP received during setup is retained and evaluated after verification.
        _phase = Phase.Arming;
        bool established = false;

        try
        {
            _previousForeground = _platform.GetForegroundWindow();
            if (!_platform.ShowAtPhysicalPoint(x, y))
            {
                Finish(false);
                return false;
            }

            if (_phase != Phase.Arming)
            {
                return BeginResult(session, established);
            }

            // SetForegroundWindow is subject to Windows foreground-lock rules. Its return
            // value is advisory; the observed foreground HWND is the acceptance criterion.
            _platform.RequestForeground(_window);
            if (_phase != Phase.Arming)
            {
                return BeginResult(session, established);
            }

            if (_platform.GetForegroundWindow() != _window)
            {
                Finish(false);
                return false;
            }

            _platform.RequestCapture(_window);
            if (_phase != Phase.Arming)
            {
                return BeginResult(session, established);
            }

            if (_platform.GetCapture() != _window)
            {
                Finish(false);
                return false;
            }

            if (_platform.GetForegroundWindow() != _window)
            {
                Finish(false);
                return false;
            }

            established = true;
            _phase = Phase.Active;

            // Both UPs may have been delivered reentrantly while setup APIs ran. In that
            // case the physical protocol is already complete and no watchdog is needed.
            if (_leftUpReceived && _rightUpReceived)
            {
                Finish(true);
                return BeginResult(session, established);
            }

            // Mark before calling the platform because message dispatch can reenter.
            _timerMayBeArmed = true;
            bool timerStarted = _platform.StartWatchdog(WatchdogTimerId, WatchdogMilliseconds);
            if (_phase != Phase.Active)
            {
                return BeginResult(session, established);
            }

            if (!timerStarted)
            {
                Finish(false);
                return false;
            }

            return true;
        }
        catch
        {
            if (_phase is Phase.Arming or Phase.Active)
            {
                Finish(false);
            }

            return BeginResult(session, established);
        }
    }

    /// <summary>
    /// 現セッションを中断し、自分が所有するcaptureだけを解放します。
    /// </summary>
    public void Dispose()
    {
        VerifyOwnerThread();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_phase is Phase.Arming or Phase.Active)
        {
            Finish(false);
        }

        _phase = Phase.Disposed;
        try { _platform.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }

    private bool BeginResult(long session, bool established)
    {
        if (!established)
        {
            return false;
        }

        if (_phase == Phase.Active && _sessionSequence == session)
        {
            return true;
        }

        return _completedSession == session && _completedSuccessfully;
    }

    private IntPtr? HandleWindowMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case ChordReleaseReceiverMessages.WmLButtonUp:
                case ChordReleaseReceiverMessages.WmNcLButtonUp:
                    ReceiveUp(left: true);
                    return IntPtr.Zero;

                case ChordReleaseReceiverMessages.WmRButtonUp:
                case ChordReleaseReceiverMessages.WmNcRButtonUp:
                    ReceiveUp(left: false);
                    return IntPtr.Zero;

                case ChordReleaseReceiverMessages.WmLButtonDown:
                case ChordReleaseReceiverMessages.WmLButtonDoubleClick:
                case ChordReleaseReceiverMessages.WmRButtonDown:
                case ChordReleaseReceiverMessages.WmRButtonDoubleClick:
                case ChordReleaseReceiverMessages.WmNcLButtonDown:
                case ChordReleaseReceiverMessages.WmNcLButtonDoubleClick:
                case ChordReleaseReceiverMessages.WmNcRButtonDown:
                case ChordReleaseReceiverMessages.WmNcRButtonDoubleClick:
                case ChordReleaseReceiverMessages.WmContextMenu:
                    // The receiver must not perform its own click/default context-menu action.
                    return IntPtr.Zero;

                case ChordReleaseReceiverMessages.WmMouseActivate:
                    return new IntPtr(ChordReleaseReceiverMessages.MaActivate);

                case ChordReleaseReceiverMessages.WmNcHitTest:
                    return new IntPtr(ChordReleaseReceiverMessages.HtClient);

                case ChordReleaseReceiverMessages.WmTimer:
                    if (unchecked((uint)wParam.ToInt64()) == WatchdogTimerId && _phase == Phase.Active)
                    {
                        Finish(false);
                        return IntPtr.Zero;
                    }

                    return null;

                case ChordReleaseReceiverMessages.WmCaptureChanged:
                    if ((_phase is Phase.Arming or Phase.Active) && _platform.GetCapture() != _window)
                    {
                        Finish(false);
                    }

                    return IntPtr.Zero;

                case ChordReleaseReceiverMessages.WmShowWindow:
                    if (wParam == IntPtr.Zero && (_phase is Phase.Arming or Phase.Active))
                    {
                        Finish(false);
                    }

                    return null;

                case ChordReleaseReceiverMessages.WmDestroy:
                    if (_phase is Phase.Arming or Phase.Active)
                    {
                        Finish(false);
                    }

                    return null;

                default:
                    return null;
            }
        }
        catch
        {
            // Do not let an exception cross the native WndProc boundary. If receiver state
            // cannot be observed reliably, abort this session without manufacturing UPs.
            if (_phase is Phase.Arming or Phase.Active)
            {
                Finish(false);
            }

            return IsButtonMessage(message) ? IntPtr.Zero : null;
        }
    }

    private void ReceiveUp(bool left)
    {
        if (_phase is not (Phase.Arming or Phase.Active))
        {
            return;
        }

        if (left)
        {
            _leftUpReceived = true;
        }
        else
        {
            _rightUpReceived = true;
        }

        if (_phase == Phase.Active && _leftUpReceived && _rightUpReceived)
        {
            Finish(true);
        }
    }

    private void Finish(bool succeeded)
    {
        if (_phase is not (Phase.Arming or Phase.Active))
        {
            return;
        }

        long session = _sessionSequence;
        _phase = Phase.Finishing;

        if (_timerMayBeArmed)
        {
            _timerMayBeArmed = false;
            try { _platform.StopWatchdog(WatchdogTimerId); } catch { }
        }

        try
        {
            if (_platform.GetCapture() == _window)
            {
                _platform.ReleaseCapture();
            }
        }
        catch
        {
            // Cleanup remains best-effort; never release a capture not verified as ours.
        }

        try { _platform.Hide(); } catch { }

        // Hiding/aborting can naturally restore the previous foreground. Restore it only
        // when our short-lived receiver still owns the foreground, avoiding steals from a
        // newer user action.
        try
        {
            if (_previousForeground != IntPtr.Zero && _platform.GetForegroundWindow() == _window)
            {
                _platform.RequestForeground(_previousForeground);
            }
        }
        catch { }

        _completedSession = session;
        _completedSuccessfully = succeeded;
        _leftUpReceived = false;
        _rightUpReceived = false;
        _previousForeground = IntPtr.Zero;
        _phase = _disposed ? Phase.Disposed : Phase.Idle;
        NotifyCompleted(succeeded);
    }

    private void NotifyCompleted(bool succeeded)
    {
        Action<bool>? handlers = Completed;
        if (handlers is null)
        {
            return;
        }

        _notifying = true;
        try
        {
            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                if (subscriber is Action<bool> handler)
                {
                    try { handler(succeeded); } catch { }
                }
            }
        }
        finally
        {
            _notifying = false;
        }
    }

    private void VerifyOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId || !_platform.IsOwnerThread)
        {
            throw new InvalidOperationException("ChordReleaseReceiverは作成したUI threadで使用してください。");
        }
    }

    private static bool IsButtonMessage(int message) => message is
        ChordReleaseReceiverMessages.WmLButtonDown or
        ChordReleaseReceiverMessages.WmLButtonUp or
        ChordReleaseReceiverMessages.WmLButtonDoubleClick or
        ChordReleaseReceiverMessages.WmRButtonDown or
        ChordReleaseReceiverMessages.WmRButtonUp or
        ChordReleaseReceiverMessages.WmRButtonDoubleClick or
        ChordReleaseReceiverMessages.WmNcLButtonDown or
        ChordReleaseReceiverMessages.WmNcLButtonUp or
        ChordReleaseReceiverMessages.WmNcLButtonDoubleClick or
        ChordReleaseReceiverMessages.WmNcRButtonDown or
        ChordReleaseReceiverMessages.WmNcRButtonUp or
        ChordReleaseReceiverMessages.WmNcRButtonDoubleClick;
}

internal interface IChordReleaseReceiverPlatform : IDisposable
{
    bool IsOwnerThread { get; }
    IntPtr CreateWindow(Func<int, IntPtr, IntPtr, IntPtr?> messageHandler);
    IntPtr GetForegroundWindow();
    IntPtr GetCapture();
    bool ShowAtPhysicalPoint(int x, int y);
    void RequestForeground(IntPtr window);
    void RequestCapture(IntPtr window);
    bool StartWatchdog(uint timerId, uint milliseconds);
    void StopWatchdog(uint timerId);
    void ReleaseCapture();
    void Hide();
}

internal static class ChordReleaseReceiverMessages
{
    internal const int WmDestroy = 0x0002;
    internal const int WmShowWindow = 0x0018;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmContextMenu = 0x007B;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmCaptureChanged = 0x0215;
    internal const int WmTimer = 0x0113;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmLButtonDoubleClick = 0x0203;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmRButtonDoubleClick = 0x0206;
    internal const int WmNcLButtonDown = 0x00A1;
    internal const int WmNcLButtonUp = 0x00A2;
    internal const int WmNcLButtonDoubleClick = 0x00A3;
    internal const int WmNcRButtonDown = 0x00A4;
    internal const int WmNcRButtonUp = 0x00A5;
    internal const int WmNcRButtonDoubleClick = 0x00A6;
    internal const int MaActivate = 1;
    internal const int HtClient = 1;
}

internal sealed class Win32ChordReleaseReceiverPlatform : IChordReleaseReceiverPlatform
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwHide = 0;

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private Func<int, IntPtr, IntPtr, IntPtr?>? _messageHandler;
    private IntPtr _window;

    public bool IsOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    public IntPtr CreateWindow(Func<int, IntPtr, IntPtr, IntPtr?> messageHandler)
    {
        if (!IsOwnerThread)
        {
            throw new InvalidOperationException("受信windowは作成元threadで作成してください。");
        }

        if (_source is not null)
        {
            throw new InvalidOperationException("受信windowはすでに作成されています。");
        }

        _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
        var parameters = new HwndSourceParameters("LinkLauncher.ChordReleaseReceiver")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow,
            PositionX = 0,
            PositionY = 0,
            Width = 1,
            Height = 1
        };

        // Unowned WS_POPUP is a hidden 1x1 receiver, not an overlay or a MainWindow-owned popup.
        _source = new HwndSource(parameters);
        _hook = WindowProcedure;
        _source.AddHook(_hook);
        _window = _source.Handle;
        return _window;
    }

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public IntPtr GetCapture() => NativeMethods.GetCapture();

    public bool ShowAtPhysicalPoint(int x, int y)
    {
        EnsureOwnerThread();
        return NativeMethods.SetWindowPos(
            _window,
            IntPtr.Zero,
            x,
            y,
            1,
            1,
            SwpShowWindow | SwpNoOwnerZOrder);
    }

    public void RequestForeground(IntPtr window)
    {
        EnsureOwnerThread();
        _ = NativeMethods.SetForegroundWindow(window);
    }

    public void RequestCapture(IntPtr window)
    {
        EnsureOwnerThread();
        _ = NativeMethods.SetCapture(window);
    }

    public bool StartWatchdog(uint timerId, uint milliseconds)
    {
        EnsureOwnerThread();
        return NativeMethods.SetTimer(_window, new UIntPtr(timerId), milliseconds, IntPtr.Zero) != UIntPtr.Zero;
    }

    public void StopWatchdog(uint timerId)
    {
        EnsureOwnerThread();
        NativeMethods.KillTimer(_window, new UIntPtr(timerId));
    }

    public void ReleaseCapture()
    {
        EnsureOwnerThread();
        if (NativeMethods.GetCapture() == _window)
        {
            NativeMethods.ReleaseCapture();
        }
    }

    public void Hide()
    {
        EnsureOwnerThread();
        if (_window != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_window, SwHide);
        }
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        if (_source is null)
        {
            return;
        }

        if (_hook is not null)
        {
            _source.RemoveHook(_hook);
            _hook = null;
        }

        _source.Dispose();
        _source = null;
        _messageHandler = null;
        _window = IntPtr.Zero;
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            IntPtr? result = _messageHandler?.Invoke(message, wParam, lParam);
            if (result.HasValue)
            {
                handled = true;
                return result.Value;
            }
        }
        catch
        {
            // Never let managed exceptions cross the WndProc boundary.
        }

        handled = false;
        return IntPtr.Zero;
    }

    private void EnsureOwnerThread()
    {
        if (!IsOwnerThread)
        {
            throw new InvalidOperationException("受信windowは作成元UI threadで操作してください。");
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr SetCapture(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern UIntPtr SetTimer(IntPtr window, UIntPtr timerId, uint milliseconds, IntPtr timerProc);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool KillTimer(IntPtr window, UIntPtr timerId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);
    }
}
