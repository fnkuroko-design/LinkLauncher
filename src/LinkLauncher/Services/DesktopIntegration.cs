using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Resources;
using System.Windows.Threading;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using Forms = System.Windows.Forms;

namespace LinkLauncher.Services;

/// <summary>
/// グローバルホットキー、マウス操作、および通知領域アイコンを管理します。
/// </summary>
public sealed class DesktopIntegration : IDisposable
{
    public static readonly string[] SupportedHotkeys =
    {
        "Ctrl + Alt + Space",
        "Ctrl + Shift + Space",
        "Alt + Space"
    };

    private const int WmHotkey = 0x0312;
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkControl = 0x11;
    private const uint VkSpace = 0x20;
    private const uint LlMouseFlagInjected = 0x00000001;
    private const uint LlMouseFlagLowerIlInjected = 0x00000002;
    private const ulong ReplayExtraInfo = 0x4C4C5232;
    private const uint MaxHotkeyId = 0xBFFF;

    private static int _nextHotkeyId;

    private readonly Window _window;
    private readonly Action _toggle;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly LowLevelMouseProc _mouseHookProc;
    private readonly uint? _processIntegrityLevel;
    private readonly Dictionary<int, string> _registeredHotkeys = new();
    private readonly DrawingIcon? _resourceTrayIcon;
    private readonly Stream? _trayIconStream;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _mouseHook;
    private int _currentHotkeyId;
    private string _hotkeyLabel = string.Empty;
    private bool _mouseChordEnabled;
    private bool _gestureEnabled;
    private readonly RightMouseGestureState _rightMouseGestureState = new();
    private int _forwardedRightDownCount;
    private bool _swallowPhysicalRightUp;
    private bool _disposed;

    public event Action<string>? Warning;

    public string HotkeyLabel => _hotkeyLabel;

    public DesktopIntegration(Window window, Action toggle, Action showSettings, Action exit)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        _showSettings = showSettings ?? throw new ArgumentNullException(nameof(showSettings));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _mouseHookProc = MouseHookCallback;
        _processIntegrityLevel = TryGetProcessIntegrityLevel(GetCurrentProcess(), out uint integrityLevel)
            ? integrityLevel
            : null;

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("開く", null, (_, _) => QueueUiAction(ShowWindow));
        _trayMenu.Items.Add("設定", null, (_, _) => QueueUiAction(() =>
        {
            ShowWindow();
            _showSettings();
        }));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("終了", null, (_, _) => QueueUiAction(_exit));

        Stream? trayIconStream = null;
        DrawingIcon? resourceTrayIcon = null;
        try
        {
            StreamResourceInfo? resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/LinkLauncher.ico", UriKind.Absolute));
            if (resource?.Stream is Stream stream)
            {
                try
                {
                    resourceTrayIcon = new DrawingIcon(stream);
                    trayIconStream = stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            resourceTrayIcon?.Dispose();
            trayIconStream?.Dispose();
        }

        _resourceTrayIcon = resourceTrayIcon;
        _trayIconStream = trayIconStream;
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _resourceTrayIcon ?? DrawingSystemIcons.Application,
            Text = "LinkLauncher",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => QueueUiAction(_toggle);

        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnWindowClosed;

        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            AttachWindowHook();
        }
    }

    /// <summary>
    /// グローバルホットキーとマウス操作を設定します。ホットキー登録に失敗した場合は、
    /// 以前の登録を維持します。
    /// </summary>
    public bool Configure(string hotkey, bool mouseChordEnabled, bool gestureEnabled)
    {
        if (_disposed)
        {
            return false;
        }

        if (!_window.Dispatcher.CheckAccess())
        {
            RaiseWarning("デスクトップ連携の設定はUIスレッドから呼び出してください。");
            return false;
        }

        if (!TryGetHotkeyModifiers(hotkey, out uint modifiers))
        {
            RaiseWarning($"未対応のホットキーです: {hotkey}");
            return false;
        }

        if (!AttachWindowHook())
        {
            RaiseWarning("ウィンドウのメッセージフックを設定できませんでした。");
            return false;
        }

        bool needsMouseHook = mouseChordEnabled || gestureEnabled;
        IntPtr stagedMouseHook = IntPtr.Zero;
        if (needsMouseHook && _mouseHook == IntPtr.Zero)
        {
            stagedMouseHook = SetWindowsHookEx(WhMouseLl, _mouseHookProc, GetModuleHandle(null), 0);
            if (stagedMouseHook == IntPtr.Zero)
            {
                RaiseWarning($"マウス操作フックを設定できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
                return false;
            }
        }

        bool hotkeyNeedsChange = _currentHotkeyId == 0
            || !string.Equals(_hotkeyLabel, hotkey, StringComparison.Ordinal);
        int stagedHotkeyId = 0;
        if (hotkeyNeedsChange)
        {
            stagedHotkeyId = NextHotkeyId();
            if (!RegisterHotKey(_hwnd, stagedHotkeyId, modifiers | ModNoRepeat, VkSpace))
            {
                int error = Marshal.GetLastWin32Error();
                RollbackStagedMouseHook(stagedMouseHook);
                RaiseWarning($"ホットキー「{hotkey}」を登録できませんでした。以前の設定を維持します (Win32: {error})。");
                return false;
            }
        }

        int previousHotkeyId = _currentHotkeyId;
        if (hotkeyNeedsChange && previousHotkeyId != 0 && !UnregisterHotKey(_hwnd, previousHotkeyId))
        {
            int error = Marshal.GetLastWin32Error();
            RollbackStagedHotkey(stagedHotkeyId, hotkey);
            RollbackStagedMouseHook(stagedMouseHook);
            RaiseWarning($"以前のホットキーを解除できませんでした。設定を維持します (Win32: {error})。");
            return false;
        }

        if (hotkeyNeedsChange)
        {
            if (previousHotkeyId != 0)
            {
                _registeredHotkeys.Remove(previousHotkeyId);
            }

            _registeredHotkeys[stagedHotkeyId] = hotkey;
            _currentHotkeyId = stagedHotkeyId;
            _hotkeyLabel = hotkey;
        }

        if (stagedMouseHook != IntPtr.Zero)
        {
            _mouseHook = stagedMouseHook;
        }

        bool mouseSettingsChanged = _mouseChordEnabled != mouseChordEnabled
            || _gestureEnabled != gestureEnabled;
        if (mouseSettingsChanged || !needsMouseHook)
        {
            bool hadPendingPress = _rightMouseGestureState.IsPending;
            if (hadPendingPress && !RestorePendingRightPress())
            {
                _swallowPhysicalRightUp = true;
            }
        }

        _mouseChordEnabled = mouseChordEnabled;
        _gestureEnabled = gestureEnabled;
        if (!needsMouseHook)
        {
            if (_mouseHook != IntPtr.Zero && !_swallowPhysicalRightUp)
            {
                if (UnhookWindowsHookEx(_mouseHook))
                {
                    _mouseHook = IntPtr.Zero;
                }
                else
                {
                    RaiseWarning($"マウス操作フックは無効化しましたが、OSから解除できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
                }
            }
        }

        return true;
    }

    /// <summary>
    /// カーソル位置をGetCursorPosの画面座標（ピクセル）で返します。
    /// WPFのDIP座標が必要な場合は、呼び出し側で変換してください。
    /// </summary>
    public static bool TryGetCursor(out System.Windows.Point point)
    {
        if (GetCursorPos(out POINT nativePoint))
        {
            point = new System.Windows.Point(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    public void Dispose()
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            try
            {
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Dispose));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher終了後はWindow.Closedからの後続処理も不要です。
            }

            return;
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnWindowClosed;

        if (_source != null)
        {
            _source.RemoveHook(WindowMessageHook);
            _source = null;
        }

        foreach (int id in _registeredHotkeys.Keys)
        {
            if (!UnregisterHotKey(_hwnd, id))
            {
                RaiseWarning($"終了時にホットキーを解除できませんでした (ID: {id}, Win32: {Marshal.GetLastWin32Error()})。");
            }
        }

        _registeredHotkeys.Clear();
        _currentHotkeyId = 0;
        _hotkeyLabel = string.Empty;

        RestorePendingRightPress();
        _swallowPhysicalRightUp = false;
        if (_mouseHook != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_mouseHook))
            {
                RaiseWarning($"マウスフックを解除できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
            }

            _mouseHook = IntPtr.Zero;
        }

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _trayMenu.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 多重破棄は無視します。
        }
        finally
        {
            _resourceTrayIcon?.Dispose();
            _trayIconStream?.Dispose();
        }

        Warning = null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        AttachWindowHook();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    private bool AttachWindowHook()
    {
        if (_source != null)
        {
            return true;
        }

        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        _source = HwndSource.FromHwnd(_hwnd);
        if (_source == null)
        {
            _hwnd = IntPtr.Zero;
            return false;
        }

        _source.AddHook(WindowMessageHook);
        return true;
    }

    private void RollbackStagedHotkey(int hotkeyId, string hotkey)
    {
        if (hotkeyId == 0)
        {
            return;
        }

        if (!UnregisterHotKey(_hwnd, hotkeyId))
        {
            _registeredHotkeys[hotkeyId] = hotkey;
            RaiseWarning($"一時ホットキーを解除できず、登録が残っています (ID: {hotkeyId}, Win32: {Marshal.GetLastWin32Error()})。");
        }
    }

    private void RollbackStagedMouseHook(IntPtr hook)
    {
        if (hook == IntPtr.Zero)
        {
            return;
        }

        if (!UnhookWindowsHookEx(hook))
        {
            _mouseHook = hook;
            RaiseWarning($"一時マウス操作フックを解除できませんでした。操作は無効のままですが、OSにフックが残っています (Win32: {Marshal.GetLastWin32Error()})。");
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey)
        {
            int id = unchecked((int)wParam.ToInt64());
            if (_registeredHotkeys.ContainsKey(id))
            {
                handled = true;
                QueueUiAction(_toggle);
            }
        }

        return IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        int message = unchecked((int)wParam.ToInt64());
        bool rightUpBelongsToPendingPress = false;
        try
        {
            bool featureEnabled = _mouseChordEnabled || _gestureEnabled;
            if (code >= 0
                && _mouseHook != IntPtr.Zero
                && (featureEnabled || _swallowPhysicalRightUp || _rightMouseGestureState.IsPending))
            {
                if (message == WmMouseMove && !_rightMouseGestureState.IsPending)
                {
                    return CallNextHookSafely(code, wParam, lParam);
                }

                if (message == WmMouseMove || message == WmRButtonDown || message == WmRButtonUp)
                {
                    MSLLHOOKSTRUCT mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (IsOwnReplayEvent(mouse))
                    {
                        if (message == WmRButtonDown || message == WmRButtonUp)
                        {
                            RecordForwardedRightButtonEvent(message);
                        }

                        return CallNextHookSafely(code, wParam, lParam);
                    }

                    if (IsInjectedMouseEvent(mouse))
                    {
                        if (message == WmRButtonDown)
                        {
                            RecordForwardedRightButtonEvent(message);
                        }
                        else if (message == WmRButtonUp)
                        {
                            if (_forwardedRightDownCount > 0)
                            {
                                RecordForwardedRightButtonEvent(message);
                            }
                            else if (_rightMouseGestureState.IsPending)
                            {
                                // 保留中の物理Downに対する、別プロセス発の孤立Upは対象アプリへ渡しません。
                                return new IntPtr(1);
                            }
                        }

                        return CallNextHookSafely(code, wParam, lParam);
                    }

                    if (message == WmRButtonUp && _swallowPhysicalRightUp)
                    {
                        _swallowPhysicalRightUp = false;
                        QueueUiAction(UnhookMouseHookIfUnused);
                        return new IntPtr(1);
                    }

                    if (message == WmRButtonDown)
                    {
                        _swallowPhysicalRightUp = false;
                        if (_rightMouseGestureState.IsPending)
                        {
                            bool hadPendingPress = _rightMouseGestureState.IsPending;
                            if (hadPendingPress && !RestorePendingRightPress())
                            {
                                _swallowPhysicalRightUp = true;
                            }
                        }

                        if (!featureEnabled || !CanSafelyInterceptAt(mouse.Point))
                        {
                            RecordForwardedRightButtonEvent(message);
                            return CallNextHookSafely(code, wParam, lParam);
                        }

                        if (!_rightMouseGestureState.TryBegin(
                            mouse.Point.X,
                            mouse.Point.Y,
                            GetTickCount64(),
                            (GetAsyncKeyState(VkControl) & 0x8000) != 0))
                        {
                            RecordForwardedRightButtonEvent(message);
                            return CallNextHookSafely(code, wParam, lParam);
                        }

                        // Downを保留し、成立したジェスチャーでは通常の右クリックを発生させません。
                        return new IntPtr(1);
                    }

                    if (message == WmMouseMove
                        && _rightMouseGestureState.ShouldRestoreOnMove(
                            mouse.Point.X,
                            mouse.Point.Y,
                            GetTickCount64(),
                            _mouseChordEnabled,
                            _gestureEnabled))
                    {
                        uint sent = RestorePendingPressForDrag(mouse.Point);
                        if (sent >= 2)
                        {
                            _rightMouseGestureState.Cancel();
                            if (sent == 3)
                            {
                                return new IntPtr(1);
                            }

                            if (sent == 2)
                            {
                                // Downまでは挿入済みです。終点Moveを追加して成功すれば物理Moveを置き換えます。
                                if (TryCreateAbsoluteMouseMove(mouse.Point, out INPUT endMove)
                                    && SendInput(1, new[] { endMove }, Marshal.SizeOf<INPUT>()) == 1)
                                {
                                    return new IntPtr(1);
                                }

                                QueueWarning($"右ドラッグ終点の再生に失敗しました (Win32: {Marshal.GetLastWin32Error()})。");
                            }
                        }
                    }

                    if (message == WmRButtonUp)
                    {
                        if (!_rightMouseGestureState.IsPending)
                        {
                            RecordForwardedRightButtonEvent(message);
                            return CallNextHookSafely(code, wParam, lParam);
                        }

                        if (_forwardedRightDownCount > 0)
                        {
                            // 先にOSへ渡した外部Downを物理Upで閉じ、押下状態を残しません。
                            _rightMouseGestureState.Cancel();
                            RecordForwardedRightButtonEvent(message);
                            return CallNextHookSafely(code, wParam, lParam);
                        }

                        rightUpBelongsToPendingPress = true;
                        int startX = _rightMouseGestureState.StartX;
                        int startY = _rightMouseGestureState.StartY;
                        RightMouseReleaseAction action = _rightMouseGestureState.Release(
                            mouse.Point.X,
                            mouse.Point.Y,
                            GetTickCount64(),
                            _mouseChordEnabled,
                            _gestureEnabled);

                        if (action == RightMouseReleaseAction.Recognized)
                        {
                            QueueUiAction(_toggle);
                            return new IntPtr(1);
                        }

                        if (action == RightMouseReleaseAction.ReplayClick)
                        {
                            if (!CanSafelyInjectAt(mouse.Point))
                            {
                                QueueWarning("通常の右クリックを復元できません。対象ウィンドウの権限を確認できないため、入力を注入しませんでした。");
                                return new IntPtr(1);
                            }

                            bool safeStart = CanSafelyInjectAt(new POINT { X = startX, Y = startY });
                            if (!safeStart)
                            {
                                QueueWarning("通常の右クリックを復元できません。開始位置の対象権限を確認できないため、入力を注入しませんでした。");
                                return new IntPtr(1);
                            }

                            RightPressReplayResult replayResult = ReplayUnrecognizedRightPress(
                                new POINT { X = startX, Y = startY },
                                mouse.Point);
                            if (replayResult == RightPressReplayResult.ConsumePhysicalUp)
                            {
                                return new IntPtr(1);
                            }

                            // 一部Downが挿入されUpの注入に失敗した場合は物理Upで閉じます。
                            return CallNextHookSafely(code, wParam, lParam);
                        }

                        return new IntPtr(1);
                    }

                }
            }
        }
        catch (Exception exception)
        {
            if (message == WmRButtonDown && _rightMouseGestureState.Cancel())
            {
                // この例外経路ではまだ物理Downを抑止していないため、そのままOSへ渡します。
            }
            else if (message == WmRButtonUp)
            {
                bool hadPendingDown = _rightMouseGestureState.Cancel();
                if (hadPendingDown || rightUpBelongsToPendingPress)
                {
                    if (rightUpBelongsToPendingPress && SendRightButtonUp() == 1)
                    {
                        QueueWarning($"マウス操作の復元処理でエラーが発生しました: {exception.Message}");
                        return new IntPtr(1);
                    }

                    if (hadPendingDown)
                    {
                        // DownをOSへ渡していないため、このUpも抑止して不対イベントを防ぎます。
                        QueueWarning($"マウス操作フックでエラーが発生しました: {exception.Message}");
                        return new IntPtr(1);
                    }
                }
            }

            QueueWarning($"マウス操作フックでエラーが発生しました: {exception.Message}");
        }

        return CallNextHookSafely(code, wParam, lParam);
    }

    private bool RestorePendingRightPress()
    {
        if (!_rightMouseGestureState.IsPending)
        {
            return true;
        }

        if (!GetCursorPos(out POINT currentPoint) || !CanSafelyInjectAt(currentPoint))
        {
            _rightMouseGestureState.Cancel();
            QueueWarning("保留中の右ボタンDownを復元できません。対象ウィンドウの権限を確認できないため、入力を注入しませんでした。");
            return false;
        }

        _rightMouseGestureState.Cancel();
        uint sent = SendRightButtonDown();
        if (sent == 1)
        {
            return true;
        }

        QueueWarning(
            $"保留中の右ボタンDownを復元できませんでした (送信数: {sent}, Win32: {Marshal.GetLastWin32Error()})。"
            + "Windowsの入力制限などによりSendInputが拒否された可能性があります。");
        return false;
    }

    private uint RestorePendingPressForDrag(POINT currentPoint)
    {
        int startX = _rightMouseGestureState.StartX;
        int startY = _rightMouseGestureState.StartY;
        if (!CanSafelyInjectAt(currentPoint)
            || !TryCreateAbsoluteMouseMove(new POINT { X = startX, Y = startY }, out INPUT startMove)
            || !TryCreateAbsoluteMouseMove(currentPoint, out INPUT endMove))
        {
            QueueWarning("右ドラッグを復元できません。対象ウィンドウまたは座標を安全に確認できませんでした。");
            return 0;
        }

        INPUT[] inputs =
        {
            startMove,
            CreateMouseInput(MouseEventRightDown),
            endMove
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            QueueWarning($"右ドラッグの復元が一部失敗しました (送信数: {sent}, Win32: {error})。");
        }

        return sent;
    }

    private static uint SendRightButtonDown()
    {
        INPUT[] inputs = { CreateMouseInput(MouseEventRightDown) };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private uint SendRightButtonUp()
    {
        INPUT[] inputs = { CreateMouseInput(MouseEventRightUp) };
        uint sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            int error = Marshal.GetLastWin32Error();
            // UIPIによる拒否は戻り値とGetLastErrorだけでは特定できません。
            // 呼び出し元は物理Upを最後の回復手段に使います。
            QueueWarning($"復元した右ボタンDownのUpを送信できませんでした (Win32: {error})。");
        }

        return sent;
    }

    private RightPressReplayResult ReplayUnrecognizedRightPress(POINT startPoint, POINT endPoint)
    {
        bool hasMovement = startPoint.X != endPoint.X || startPoint.Y != endPoint.Y;
        if (!hasMovement)
        {
            return ReplaySimpleRightClick();
        }

        if (!TryCreateAbsoluteMouseMove(startPoint, out INPUT startMove)
            || !TryCreateAbsoluteMouseMove(endPoint, out INPUT endMove))
        {
            QueueWarning("通常の右ドラッグを復元できません。画面座標を再生できませんでした。");
            return ReplaySimpleRightClick();
        }

        INPUT[] inputs =
        {
            startMove,
            CreateMouseInput(MouseEventRightDown),
            endMove,
            CreateMouseInput(MouseEventRightUp)
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            QueueWarning($"通常の右ドラッグを一部復元できませんでした (送信数: {sent}, Win32: {error})。");
        }

        if (sent == inputs.Length)
        {
            return RightPressReplayResult.ConsumePhysicalUp;
        }

        if (sent == 3)
        {
            // Downと終点Moveが挿入済みなので、Upを別キューへ追加して物理Upとの競合を避けます。
            return SendRightButtonUp() == 1
                ? RightPressReplayResult.ConsumePhysicalUp
                : RightPressReplayResult.PassPhysicalUp;
        }

        if (sent == 2)
        {
            INPUT[] finish = { endMove, CreateMouseInput(MouseEventRightUp) };
            uint finishSent = SendInput((uint)finish.Length, finish, Marshal.SizeOf<INPUT>());
            if (finishSent == finish.Length)
            {
                return RightPressReplayResult.ConsumePhysicalUp;
            }

            if (finishSent == 1)
            {
                return SendRightButtonUp() == 1
                    ? RightPressReplayResult.ConsumePhysicalUp
                    : RightPressReplayResult.PassPhysicalUp;
            }

            // 最初の2件でDownはすでに挿入済みです。物理Upでボタンを閉じます。
            return RightPressReplayResult.PassPhysicalUp;
        }

        if (sent == 1)
        {
            // Downは未挿入です。終点へ戻してから通常クリックを再生します。
            uint restored = SendInput(1, new[] { endMove }, Marshal.SizeOf<INPUT>());
            if (restored != 1)
            {
                QueueWarning($"マウスカーソル位置の復元に失敗しました (Win32: {Marshal.GetLastWin32Error()})。");
            }
        }

        return ReplaySimpleRightClick();
    }

    private RightPressReplayResult ReplaySimpleRightClick()
    {
        INPUT[] inputs =
        {
            CreateMouseInput(MouseEventRightDown),
            CreateMouseInput(MouseEventRightUp)
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            return RightPressReplayResult.ConsumePhysicalUp;
        }

        int error = Marshal.GetLastWin32Error();
        QueueWarning($"通常の右クリックを復元できませんでした (送信数: {sent}, Win32: {error})。");
        if (sent == 1)
        {
            return SendRightButtonUp() == 1
                ? RightPressReplayResult.ConsumePhysicalUp
                : RightPressReplayResult.PassPhysicalUp;
        }

        // Downが挿入されていないので、保留していた物理Upは流しません。
        return RightPressReplayResult.ConsumePhysicalUp;
    }

    private static INPUT CreateMouseInput(uint flags, int dx = 0, int dy = 0)
    {
        return new INPUT
        {
            Type = InputMouse,
            Data = new INPUTUNION
            {
                Mouse = new MOUSEINPUT
                {
                    Dx = dx,
                    Dy = dy,
                    Flags = flags,
                    ExtraInfo = new UIntPtr(ReplayExtraInfo)
                }
            }
        };
    }

    private static bool IsOwnReplayEvent(MSLLHOOKSTRUCT mouse)
    {
        return mouse.ExtraInfo.ToUInt64() == ReplayExtraInfo
            && (mouse.Flags & (LlMouseFlagInjected | LlMouseFlagLowerIlInjected)) != 0;
    }

    private static bool IsInjectedMouseEvent(MSLLHOOKSTRUCT mouse)
    {
        return (mouse.Flags & (LlMouseFlagInjected | LlMouseFlagLowerIlInjected)) != 0;
    }

    private void RecordForwardedRightButtonEvent(int message)
    {
        if (message == WmRButtonDown)
        {
            if (_forwardedRightDownCount < int.MaxValue)
            {
                _forwardedRightDownCount++;
            }
        }
        else if (message == WmRButtonUp && _forwardedRightDownCount > 0)
        {
            _forwardedRightDownCount--;
        }
    }

    private bool CanSafelyInterceptAt(POINT point)
    {
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out uint processId);
        return processId != 0
            && processId != (uint)Environment.ProcessId
            && CanSafelyInjectIntoProcess(processId);
    }

    private bool CanSafelyInjectAt(POINT point)
    {
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out uint processId);
        return processId != 0 && CanSafelyInjectIntoProcess(processId);
    }

    private bool CanSafelyInjectIntoProcess(uint processId)
    {
        if (_processIntegrityLevel is not uint ownIntegrityLevel
            || !TryGetProcessIntegrityLevel(processId, out uint targetIntegrityLevel))
        {
            return false;
        }

        return targetIntegrityLevel <= ownIntegrityLevel;
    }

    private static bool TryCreateAbsoluteMouseMove(POINT point, out INPUT input)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 1 || height <= 1)
        {
            input = default;
            return false;
        }

        int normalizedX = (int)Math.Clamp(((long)point.X - left) * 65535 / (width - 1), 0, 65535);
        int normalizedY = (int)Math.Clamp(((long)point.Y - top) * 65535 / (height - 1), 0, 65535);
        input = CreateMouseInput(
            MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
            normalizedX,
            normalizedY);
        return true;
    }

    private void UnhookMouseHookIfUnused()
    {
        if (_mouseChordEnabled || _gestureEnabled || _swallowPhysicalRightUp || _mouseHook == IntPtr.Zero)
        {
            return;
        }

        if (UnhookWindowsHookEx(_mouseHook))
        {
            _mouseHook = IntPtr.Zero;
        }
        else
        {
            RaiseWarning($"マウス操作フックを解除できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
        }
    }

    private void ShowWindow()
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
    }

    private void QueueUiAction(Action action)
    {
        try
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher終了中の通知は破棄します。
        }
    }

    private void QueueWarning(string message)
    {
        try
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => RaiseWarning(message)));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher終了後は通知先がありません。
        }
    }

    private void RaiseWarning(string message)
    {
        try
        {
            Warning?.Invoke(message);
        }
        catch
        {
            // 通知先の例外をネイティブコールバックや終了処理へ伝播させません。
        }
    }

    private static bool TryGetHotkeyModifiers(string hotkey, out uint modifiers)
    {
        modifiers = hotkey switch
        {
            "Ctrl + Alt + Space" => ModControl | ModAlt,
            "Ctrl + Shift + Space" => ModControl | ModShift,
            "Alt + Space" => ModAlt,
            _ => 0
        };

        return modifiers != 0;
    }

    private static int NextHotkeyId()
    {
        return (int)(((uint)Interlocked.Increment(ref _nextHotkeyId) - 1) % MaxHotkeyId) + 1;
    }

    private static IntPtr CallNextHookSafely(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    private enum RightPressReplayResult
    {
        ConsumePhysicalUp,
        PassPhysicalUp
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(uint virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private static bool TryGetProcessIntegrityLevel(uint processId, out uint integrityLevel)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            integrityLevel = 0;
            return false;
        }

        try
        {
            return TryGetProcessIntegrityLevel(process, out integrityLevel);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool TryGetProcessIntegrityLevel(IntPtr process, out uint integrityLevel)
    {
        integrityLevel = 0;
        if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
        {
            return false;
        }

        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out uint bufferLength);
            if (bufferLength == 0)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bufferLength));
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, bufferLength, out _))
                {
                    return false;
                }

                TOKEN_MANDATORY_LABEL label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
                IntPtr subAuthorityCountPointer = GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCountPointer == IntPtr.Zero)
                {
                    return false;
                }

                byte subAuthorityCount = Marshal.ReadByte(subAuthorityCountPointer);
                if (subAuthorityCount == 0)
                {
                    return false;
                }

                IntPtr lastSubAuthority = GetSidSubAuthority(label.Label.Sid, (uint)(subAuthorityCount - 1));
                if (lastSubAuthority == IntPtr.Zero)
                {
                    return false;
                }

                integrityLevel = unchecked((uint)Marshal.ReadInt32(lastSubAuthority));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
