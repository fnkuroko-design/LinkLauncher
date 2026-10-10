using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Resources;
using System.Windows.Threading;
using LinkLauncher.Models;
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
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkLeftButton = 0x01;
    private const uint VkRightButton = 0x02;
    private const uint VkMiddleButton = 0x04;
    private const uint VkXButton1 = 0x05;
    private const uint VkXButton2 = 0x06;
    private const uint VkSpace = 0x20;
    private const uint LlMouseFlagInjected = 0x00000001;
    private const uint LlMouseFlagLowerIlInjected = 0x00000002;
    private const ulong ReplayExtraInfo = 0x4C4C5232;
    private const uint MaxHotkeyId = 0xBFFF;

    private static int _nextHotkeyId;
    private static readonly HashSet<LowLevelMouseProc> ActiveMouseHookCallbacks = new();

    private readonly Window _window;
    private readonly uint _processId = (uint)Environment.ProcessId;
    private readonly Action _toggle;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly Action? _showLauncher;
    private readonly Func<Action?>? _captureMouseActivationCompletion;
    private readonly Func<Func<bool>, bool>? _restoreSourceMenuFocus;
    private readonly MouseActivationCompletion _mouseActivationCompletion = new();
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
    private MouseActivationPattern _mousePattern;
    private readonly MouseActivationState _mouseActivationState = new();
    private NativeMouseChord? _nativeMouseChord;
    private uint _nativeChordSequence;
    private readonly DesktopDoubleClickState _desktopDoubleClickState = new();
    private POINT _desktopFirstPoint;
    private IntPtr _desktopFirstWindow;
    private IntPtr _desktopSecondWindow;
    private bool _desktopFirstClickWasVisible;
    private POINT _desktopRequestPoint;
    private int _desktopToleranceX, _desktopToleranceY;
    private long _desktopRequestTicket;
    private bool _desktopHitTestPending;
    private bool _swallowMiddleUp;
    private bool _swallowRightUp;
    private bool _swallowLeftUp;
    private int _swallowXButtonUp;
    private bool _dismissOnExternalClick;
    private bool _mouseActivationCallbackQueued;
    private long _mouseActivationTicket;
    private bool _disposed;

    public event Action<string>? Warning;

    /// <summary>
    /// 同一プロセス外のマウスボタンDownをUIスレッドへ通知します。入力は消費しません。
    /// </summary>
    public event Action? ExternalButtonDown;

    public string HotkeyLabel => _hotkeyLabel;

    public DesktopIntegration(Window window, Action toggle, Action showSettings, Action exit,
        Action? showLauncher = null, Func<Action?>? captureMouseActivationCompletion = null,
        Func<Func<bool>, bool>? restoreSourceMenuFocus = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        _showSettings = showSettings ?? throw new ArgumentNullException(nameof(showSettings));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _showLauncher = showLauncher;
        _captureMouseActivationCompletion = captureMouseActivationCompletion;
        _restoreSourceMenuFocus = restoreSourceMenuFocus;
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
        _window.IsVisibleChanged += OnLauncherVisibilityChanged;
        _window.PreviewKeyDown += OnLauncherKeyDown;

        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            AttachWindowHook();
        }
    }

    /// <summary>
    /// グローバルホットキー、マウス操作、および外部クリック監視を設定します。
    /// ホットキー登録に失敗した場合は、以前の登録を維持します。
    /// </summary>
    public bool Configure(string hotkey, MouseActivationPattern mousePattern, bool dismissOnExternalClick = false)
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

        bool needsMouseHook = mousePattern != MouseActivationPattern.None
            || dismissOnExternalClick
            || HasSuppressedButtonUps
            || _mouseActivationState.IsPending;
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

        NativeMouseChord? stagedNativeChord = null;
        if (mousePattern == MouseActivationPattern.RightThenLeft && _nativeMouseChord is null
            && !NativeMouseChord.TryStart(_hwnd, out stagedNativeChord, out string nativeError))
        {
            RollbackStagedHotkey(stagedHotkeyId, hotkey);
            RollbackStagedMouseHook(stagedMouseHook);
            RaiseWarning($"右＋左の入力仲介を開始できませんでした。以前の設定を維持します: {nativeError}");
            return false;
        }

        int previousHotkeyId = _currentHotkeyId;
        if (hotkeyNeedsChange && previousHotkeyId != 0 && !UnregisterHotKey(_hwnd, previousHotkeyId))
        {
            int error = Marshal.GetLastWin32Error();
            RollbackStagedHotkey(stagedHotkeyId, hotkey);
            RollbackStagedMouseHook(stagedMouseHook);
            stagedNativeChord?.Dispose();
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
            ActiveMouseHookCallbacks.Add(_mouseHookProc);
        }

        bool mouseSettingsChanged = _mousePattern != mousePattern;
        if (mouseSettingsChanged)
        {
            CancelMouseActivationCompletion();
            CancelDesktopDoubleClick();
        }
        if (mouseSettingsChanged && _mouseActivationState.IsPending)
        {
            MouseActivationButton pendingButton = _mouseActivationState.Button;
            if (!RestorePendingButtonDown())
            {
                SetSuppressedUp(pendingButton);
            }
        }

        _mousePattern = mousePattern;
        _dismissOnExternalClick = dismissOnExternalClick;
        if (stagedNativeChord is not null) _nativeMouseChord = stagedNativeChord;
        if (mousePattern != MouseActivationPattern.RightThenLeft)
        {
            _nativeMouseChord?.Dispose();
            _nativeMouseChord = null;
        }
        if (_mouseHook != IntPtr.Zero && !ShouldKeepMouseHook)
        {
            if (UnhookWindowsHookEx(_mouseHook))
            {
                _mouseHook = IntPtr.Zero;
                ActiveMouseHookCallbacks.Remove(_mouseHookProc);
            }
            else
            {
                RaiseWarning($"マウス操作フックは無効化しましたが、OSから解除できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
            }
        }

        return true;
    }

    /// <summary>
    /// カーソル位置を物理画面座標（ピクセル）で返します。
    /// WPFのDIP座標が必要な場合は、呼び出し側で変換してください。
    /// </summary>
    public static bool TryGetCursor(out System.Windows.Point point)
    {
        if (GetPhysicalCursorPos(out POINT nativePoint))
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
        CancelMouseActivationCompletion();
        CancelDesktopDoubleClick();
        _nativeMouseChord?.Dispose();
        _nativeMouseChord = null;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnWindowClosed;
        _window.IsVisibleChanged -= OnLauncherVisibilityChanged;
        _window.PreviewKeyDown -= OnLauncherKeyDown;

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

        if (_mouseActivationState.IsPending)
        {
            MouseActivationButton pendingButton = _mouseActivationState.Button;
            if (!RestorePendingButtonDown())
            {
                SetSuppressedUp(pendingButton);
            }
        }

        if (_mouseHook != IntPtr.Zero && !HasSuppressedButtonUps)
        {
            if (!UnhookWindowsHookEx(_mouseHook))
            {
                RaiseWarning($"マウスフックを解除できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
            }
            else
            {
                _mouseHook = IntPtr.Zero;
                ActiveMouseHookCallbacks.Remove(_mouseHookProc);
            }
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
        ExternalButtonDown = null;
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
            ActiveMouseHookCallbacks.Add(_mouseHookProc);
            RaiseWarning($"一時マウス操作フックを解除できませんでした。操作は無効のままですが、OSにフックが残っています (Win32: {Marshal.GetLastWin32Error()})。");
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMouseChord.NotificationMessage)
        {
            handled = true;
            uint sequence = unchecked((uint)wParam.ToInt64());
            int notification = unchecked((int)lParam.ToInt64());
            if (_disposed || _mousePattern != MouseActivationPattern.RightThenLeft || _nativeMouseChord is null)
                return IntPtr.Zero;
            if (notification == NativeMouseChord.BeginNotification && _nativeMouseChord.TryTakeRequest(sequence))
                return BeginNativeMouseActivation(sequence);
            if (notification == NativeMouseChord.MenuFocusNotification && sequence == _nativeChordSequence
                && _window.IsVisible && _restoreSourceMenuFocus is not null)
            {
                CancelMouseActivationCompletion();
                try
                {
                    bool restored = _restoreSourceMenuFocus(_nativeMouseChord.RestoreMenuFocus);
                    return restored ? new IntPtr(1) : IntPtr.Zero;
                }
                catch (Exception exception)
                {
                    QueueWarning($"元のメニュー取消後の前面復帰に失敗しました: {exception.Message}");
                    return IntPtr.Zero;
                }
            }
            if (notification == NativeMouseChord.CompleteNotification && sequence == _nativeChordSequence
                && _nativeMouseChord.PendingButtons == 0)
            {
                Action? completion = _mouseActivationCompletion.Release();
                if (completion is not null) QueueMouseActivationCompletion(_mouseActivationTicket, completion);
            }
            return IntPtr.Zero;
        }
        if (message == WmHotkey)
        {
            int id = unchecked((int)wParam.ToInt64());
            if (_registeredHotkeys.ContainsKey(id))
            {
                handled = true;
                _nativeMouseChord?.ClearMenuGuard();
                CancelMouseActivationCompletion();
                CancelDesktopDoubleClick();
                QueueUiAction(_toggle);
            }
        }

        return IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        int message = unchecked((int)wParam.ToInt64());
        if (code < 0 || _mouseHook == IntPtr.Zero)
        {
            return CallNextHookSafely(code, wParam, lParam);
        }

        if (_disposed)
        {
            TryFinishDisposedMouseHook();
            if (_mouseHook == IntPtr.Zero)
            {
                return CallNextHookSafely(code, wParam, lParam);
            }
        }
        else if (!ShouldKeepMouseHook)
        {
            UnhookMouseHookIfUnused();
            if (_mouseHook == IntPtr.Zero)
            {
                return CallNextHookSafely(code, wParam, lParam);
            }
        }

        if (!ShouldInspectMouseMessage(message))
        {
            return CallNextHookSafely(code, wParam, lParam);
        }

        bool pendingBeforeEvent = _mouseActivationState.IsPending;
        MouseActivationButton pendingButtonBeforeEvent = pendingBeforeEvent
            ? _mouseActivationState.Button
            : default;
        bool pendingUpBeingProcessed = pendingBeforeEvent
            && ((message == WmMButtonUp && pendingButtonBeforeEvent == MouseActivationButton.Middle)
                || (message == WmRButtonUp && pendingButtonBeforeEvent == MouseActivationButton.Right));
        bool pendingDownPassed = _mouseActivationState.IsDownPassed;
        try
        {
            MSLLHOOKSTRUCT mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (IsOwnReplayEvent(mouse) || IsInjectedMouseEvent(mouse))
            {
                if (IsButtonDownMessage(message) || IsButtonUpMessage(message)
                    || message is WmMouseWheel or WmMouseHWheel) CancelDesktopDoubleClick();
                return CallNextHookSafely(code, wParam, lParam);
            }

            bool freshButtonDown = IsButtonDownMessage(message);
            if (freshButtonDown)
            {
                CancelMouseActivationCompletion();
            }
            if (freshButtonDown || message is WmMouseWheel or WmMouseHWheel)
                _nativeMouseChord?.ClearMenuGuard();

            // 右＋左は通常のWH_MOUSEでウィンドウ宛ての通知だけを仲介する。
            // Windowsの入力列とグローバルボタン状態を更新するLL入力はすべて通す。
            if (_nativeMouseChord?.IsGestureActive == true && !freshButtonDown)
                return CallNextHookSafely(code, wParam, lParam);

            if (TryConsumeSuppressedUp(message, mouse.MouseData))
            {
                if (!HasSuppressedButtonUps)
                {
                    Action? completion = _mouseActivationCompletion.Release();
                    if (!_disposed && completion != null)
                        QueueMouseActivationCompletion(_mouseActivationTicket, completion);
                }
                if (_disposed)
                {
                    TryFinishDisposedMouseHook();
                }
                else
                {
                    QueueUiAction(UnhookMouseHookIfUnused);
                }

                return new IntPtr(1);
            }

            if (_disposed)
            {
                TryFinishDisposedMouseHook();
                return CallNextHookSafely(code, wParam, lParam);
            }

            if (_mousePattern == MouseActivationPattern.DesktopDoubleClick)
                ObserveDesktopDoubleClick(message, mouse);

            if (message is WmMouseWheel or WmMouseHWheel && _mouseActivationState.IsDownPassed)
            {
                _mouseActivationState.Cancel();
            }

            if (_dismissOnExternalClick
                && _window.IsVisible
                && IsButtonDownMessage(message)
                && IsOutsideProcessWindow(mouse.Point)
                && !IsActivationGestureCandidate(message, mouse.MouseData))
            {
                QueueExternalButtonDown();
            }

            if (message == WmMouseMove)
            {
                if (!_mouseActivationState.ShouldRestoreOnMove(mouse.Point.X, mouse.Point.Y))
                {
                    return CallNextHookSafely(code, wParam, lParam);
                }

                MouseActivationButton button = _mouseActivationState.Button;
                if (_mouseActivationState.IsDownPassed)
                {
                    POINT downPoint = new() { X = _mouseActivationState.StartX, Y = _mouseActivationState.StartY };
                    _mouseActivationState.Cancel();
                    QueueExternalButtonDownIfOutside(downPoint);
                    return CallNextHookSafely(code, wParam, lParam);
                }
                POINT pressPoint = new() { X = _mouseActivationState.StartX, Y = _mouseActivationState.StartY };
                MouseDragRestoreResult restore = RestorePendingPressForDrag(mouse.Point, button);
                _mouseActivationState.Cancel();
                if (!restore.ButtonDownInserted)
                {
                    SetSuppressedUp(button);
                }

                QueueExternalButtonDownIfOutside(pressPoint);

                return restore.CurrentMoveInserted
                    ? new IntPtr(1)
                    : CallNextHookSafely(code, wParam, lParam);
            }

            if (IsButtonDownMessage(message))
            {
                return HandlePhysicalButtonDown(code, wParam, lParam, message, mouse);
            }

            if (IsButtonUpMessage(message))
            {
                return HandlePhysicalButtonUp(code, wParam, lParam, message, mouse);
            }
        }
        catch (Exception exception)
        {
            _mouseActivationState.Cancel();
            if (pendingUpBeingProcessed && !pendingDownPassed)
            {
                // DownをOSへ渡していないため、現在のUpだけを抑止します。
                QueueWarning($"マウス操作を復元できませんでした: {exception.Message}");
                return new IntPtr(1);
            }

            if (pendingBeforeEvent && !pendingDownPassed)
            {
                SetSuppressedUp(pendingButtonBeforeEvent);
            }

            QueueWarning($"マウス操作フックでエラーが発生しました: {exception.Message}");
        }

        return CallNextHookSafely(code, wParam, lParam);
    }

    private IntPtr HandlePhysicalButtonDown(
        int code,
        IntPtr wParam,
        IntPtr lParam,
        int message,
        MSLLHOOKSTRUCT mouse)
    {
        if (_mouseActivationState.IsPending)
        {
            MouseActivationButton primaryButton = _mouseActivationState.Button;
            MouseActivationButton secondButton = GetActivationButton(message, mouse.MouseData);
            if (_mouseActivationState.ShouldRestoreOnMove(mouse.Point.X, mouse.Point.Y))
            {
                MouseDragRestoreResult restore = RestorePendingPressForDrag(mouse.Point, primaryButton);
                _mouseActivationState.Cancel();
                if (!restore.ButtonDownInserted)
                {
                    SetSuppressedUp(primaryButton);
                }

                return CallNextHookSafely(code, wParam, lParam);
            }

            if (_mouseActivationState.IsExpectedChord(_mousePattern, secondButton))
            {
                if (!AreOtherMouseButtonsDownExcept(primaryButton, secondButton)
                    && CanSafelyInterceptAt(mouse.Point))
                {
                    _mouseActivationState.Cancel();
                    SetSuppressedUp(primaryButton);
                    SetSuppressedUp(secondButton);
                    QueueMouseActivation();
                    return new IntPtr(1);
                }

                if (!RestorePendingButtonDown())
                {
                    SetSuppressedUp(primaryButton);
                }

                QueueExternalButtonDownIfOutside(mouse.Point);

                return CallNextHookSafely(code, wParam, lParam);
            }

            if (!RestorePendingButtonDown())
            {
                SetSuppressedUp(primaryButton);
            }

            return CallNextHookSafely(code, wParam, lParam);
        }

        if (message == WmMButtonDown)
        {
            if (_mousePattern == MouseActivationPattern.MiddleClick)
            {
                if (!AreOtherMouseButtonsDownExcept(MouseActivationButton.Middle)
                    && CanSafelyInterceptAt(mouse.Point))
                {
                    _swallowMiddleUp = true;
                    QueueMouseActivation();
                    return new IntPtr(1);
                }

                QueueExternalButtonDownIfOutside(mouse.Point);
                return CallNextHookSafely(code, wParam, lParam);
            }

            if (_mousePattern == MouseActivationPattern.MiddleThenRight)
            {
                IntPtr result = BeginPendingPress(code, wParam, lParam, mouse, MouseActivationButton.Middle);
                if (!_mouseActivationState.IsPending)
                {
                    QueueExternalButtonDownIfOutside(mouse.Point);
                }

                return result;
            }

            return CallNextHookSafely(code, wParam, lParam);
        }

        if (message == WmRButtonDown && _mousePattern == MouseActivationPattern.RightThenLeft)
        {
            // 最初の右DOWNを含め、LLフックでは右＋左の入力を保留・再送しない。
            return CallNextHookSafely(code, wParam, lParam);
        }

        if (message == WmXButtonDown)
        {
            int buttonId = GetXButtonId(mouse.MouseData);
            bool selected = (_mousePattern == MouseActivationPattern.XButton1 && buttonId == 1)
                || (_mousePattern == MouseActivationPattern.XButton2 && buttonId == 2);
            MouseActivationButton activationButton = buttonId == 1
                ? MouseActivationButton.XButton1
                : MouseActivationButton.XButton2;
            if (selected
                && !AreOtherMouseButtonsDownExcept(activationButton)
                && CanSafelyInterceptAt(mouse.Point))
            {
                _swallowXButtonUp = buttonId;
                QueueMouseActivation();
                return new IntPtr(1);
            }

            if (selected)
            {
                QueueExternalButtonDownIfOutside(mouse.Point);
            }
        }

        return CallNextHookSafely(code, wParam, lParam);
    }

    private IntPtr BeginPendingPress(
        int code,
        IntPtr wParam,
        IntPtr lParam,
        MSLLHOOKSTRUCT mouse,
        MouseActivationButton button)
    {
        if (AreOtherMouseButtonsDownExcept(button) || !CanSafelyInterceptAt(mouse.Point))
        {
            return CallNextHookSafely(code, wParam, lParam);
        }

        return _mouseActivationState.TryBegin(button, mouse.Point.X, mouse.Point.Y)
            ? new IntPtr(1)
            : CallNextHookSafely(code, wParam, lParam);
    }

    private IntPtr HandlePhysicalButtonUp(
        int code,
        IntPtr wParam,
        IntPtr lParam,
        int message,
        MSLLHOOKSTRUCT mouse)
    {
        if (!_mouseActivationState.IsPending
            || _mouseActivationState.Button != GetActivationButton(message, mouse.MouseData))
        {
            return CallNextHookSafely(code, wParam, lParam);
        }

        MouseActivationButton button = _mouseActivationState.Button;
        POINT downPoint = new() { X = _mouseActivationState.StartX, Y = _mouseActivationState.StartY };
        MouseActivationReleaseAction action = _mouseActivationState.Release();
        if (button == MouseActivationButton.Right)
        {
            QueueExternalButtonDownIfOutside(downPoint);
        }
        if (action == MouseActivationReleaseAction.ReplayClick)
        {
            IntPtr result = ReplayPendingClick(code, wParam, lParam, mouse.Point, button);
            QueueExternalButtonDownIfOutside(mouse.Point);
            return result;
        }

        return CallNextHookSafely(code, wParam, lParam);
    }

    private IntPtr ReplayPendingClick(
        int code,
        IntPtr wParam,
        IntPtr lParam,
        POINT point,
        MouseActivationButton button)
    {
        if (!CanSafelyInjectAt(point))
        {
            QueueWarning("通常のクリックを復元できません。対象ウィンドウの権限を確認できないため、入力を注入しませんでした。");
            return new IntPtr(1);
        }

        INPUT[] inputs =
        {
            CreateMouseInput(GetMouseButtonFlag(button, isDown: true)),
            CreateMouseInput(GetMouseButtonFlag(button, isDown: false))
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            return new IntPtr(1);
        }

        QueueWarning($"通常のクリックを一部復元できませんでした (送信数: {sent}, Win32: {Marshal.GetLastWin32Error()})。");
        // Downが挿入済みなら物理Upで閉じ、DownがなければUpも抑止します。
        return sent > 0
            ? CallNextHookSafely(code, wParam, lParam)
            : new IntPtr(1);
    }

    private bool RestorePendingButtonDown()
    {
        if (!_mouseActivationState.IsPending)
        {
            return true;
        }

        MouseActivationButton button = _mouseActivationState.Button;
        bool downPassed = _mouseActivationState.IsDownPassed;
        _mouseActivationState.Cancel();
        if (downPassed) return true;
        if (!GetPhysicalCursorPos(out POINT currentPoint) || !CanSafelyInjectAt(currentPoint))
        {
            QueueWarning("保留中のボタンDownを復元できません。対象ウィンドウの権限を確認できないため、入力を注入しませんでした。");
            return false;
        }

        if (SendMouseButtonDown(button))
        {
            return true;
        }

        QueueWarning($"保留中のボタンDownを復元できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
        return false;
    }

    private MouseDragRestoreResult RestorePendingPressForDrag(POINT currentPoint, MouseActivationButton button)
    {
        POINT startPoint = new() { X = _mouseActivationState.StartX, Y = _mouseActivationState.StartY };
        if (!CanSafelyInjectAt(startPoint)
            || !CanSafelyInjectAt(currentPoint)
            || !TryCreateAbsoluteMouseMove(startPoint, out INPUT startMove)
            || !TryCreateAbsoluteMouseMove(currentPoint, out INPUT endMove))
        {
            QueueWarning("マウスドラッグを復元できません。対象ウィンドウまたは座標を安全に確認できませんでした。");
            return default;
        }

        INPUT[] inputs =
        {
            startMove,
            CreateMouseInput(GetMouseButtonFlag(button, isDown: true)),
            endMove
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            return new MouseDragRestoreResult(ButtonDownInserted: true, CurrentMoveInserted: true);
        }

        int error = Marshal.GetLastWin32Error();
        if (sent == 2)
        {
            uint endSent = SendInput(1, new[] { endMove }, Marshal.SizeOf<INPUT>());
            if (endSent == 1)
            {
                QueueWarning($"マウスドラッグ終点を別送信で復元しました (Win32: {error})。");
                return new MouseDragRestoreResult(ButtonDownInserted: true, CurrentMoveInserted: true);
            }

            QueueWarning($"マウスドラッグ終点を物理移動へ引き継ぎます (送信数: {sent}, Win32: {Marshal.GetLastWin32Error()})。");
            return new MouseDragRestoreResult(ButtonDownInserted: true, CurrentMoveInserted: false);
        }

        if (sent == 1)
        {
            uint restored = SendInput(1, new[] { endMove }, Marshal.SizeOf<INPUT>());
            if (restored == 1)
            {
                QueueWarning($"マウスドラッグDownの挿入前に中断したため、終点カーソルを復元しました (Win32: {error})。");
                return new MouseDragRestoreResult(ButtonDownInserted: false, CurrentMoveInserted: true);
            }
        }

        QueueWarning($"マウスドラッグを復元できませんでした (送信数: {sent}, Win32: {Marshal.GetLastWin32Error()})。");
        return default;
    }

    private bool SendMouseButtonDown(MouseActivationButton button)
    {
        INPUT[] inputs = { CreateMouseInput(GetMouseButtonFlag(button, isDown: true)) };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    private void SetSuppressedUp(MouseActivationButton button)
    {
        switch (button)
        {
            case MouseActivationButton.Middle:
                _swallowMiddleUp = true;
                break;
            case MouseActivationButton.Right:
                _swallowRightUp = true;
                break;
            case MouseActivationButton.Left:
                _swallowLeftUp = true;
                break;
            case MouseActivationButton.XButton1:
                _swallowXButtonUp = 1;
                break;
            case MouseActivationButton.XButton2:
                _swallowXButtonUp = 2;
                break;
        }
    }

    private bool TryConsumeSuppressedUp(int message, uint mouseData)
    {
        bool consumed = message switch
        {
            WmMButtonUp when _swallowMiddleUp => ClearMiddleUp(),
            WmRButtonUp when _swallowRightUp => ClearRightUp(),
            WmLButtonUp when _swallowLeftUp => ClearLeftUp(),
            WmXButtonUp when _swallowXButtonUp != 0 && GetXButtonId(mouseData) == _swallowXButtonUp
                => ClearXButtonUp(),
            _ => false
        };
        return consumed;
    }

    private bool ClearMiddleUp() { _swallowMiddleUp = false; return true; }
    private bool ClearRightUp() { _swallowRightUp = false; return true; }
    private bool ClearLeftUp() { _swallowLeftUp = false; return true; }
    private bool ClearXButtonUp() { _swallowXButtonUp = 0; return true; }

    private bool ShouldInspectMouseMessage(int message)
    {
        if (_disposed)
        {
            return _mouseHook != IntPtr.Zero;
        }

        if (_nativeMouseChord is not null &&
            (IsButtonDownMessage(message) || message is WmMouseWheel or WmMouseHWheel))
            return true;

        if (message == WmMouseMove)
        {
            return _mouseActivationState.IsPending || _desktopDoubleClickState.IsPressed || _desktopHitTestPending;
        }

        if (_mousePattern == MouseActivationPattern.DesktopDoubleClick
            && (IsButtonDownMessage(message) || IsButtonUpMessage(message)
                || message is WmMouseWheel or WmMouseHWheel)) return true;
        if (message is WmMouseWheel or WmMouseHWheel) return _mouseActivationState.IsDownPassed;

        if (_dismissOnExternalClick && _window.IsVisible && IsButtonDownMessage(message))
        {
            return true;
        }

        if ((_mouseActivationCompletion.IsPending || _mouseActivationCallbackQueued) && IsButtonDownMessage(message))
            return true;

        if (_mouseActivationState.IsPending && IsButtonDownMessage(message))
        {
            return true;
        }

        return message switch
        {
            WmMButtonDown => _mousePattern is MouseActivationPattern.MiddleClick or MouseActivationPattern.MiddleThenRight,
            WmMButtonUp => _swallowMiddleUp || (_mouseActivationState.IsPending && _mouseActivationState.Button == MouseActivationButton.Middle),
            WmRButtonDown => _mousePattern == MouseActivationPattern.RightThenLeft
                || (_mousePattern == MouseActivationPattern.MiddleThenRight
                    && _mouseActivationState.IsPending
                    && _mouseActivationState.Button == MouseActivationButton.Middle),
            WmRButtonUp => _swallowRightUp || (_mouseActivationState.IsPending && _mouseActivationState.Button == MouseActivationButton.Right),
            WmLButtonDown => _mousePattern == MouseActivationPattern.RightThenLeft && _mouseActivationState.IsPending,
            WmLButtonUp => _swallowLeftUp,
            WmXButtonDown => _mousePattern is MouseActivationPattern.XButton1 or MouseActivationPattern.XButton2,
            WmXButtonUp => _swallowXButtonUp != 0,
            _ => false
        };
    }

    private bool HasSuppressedButtonUps => _swallowMiddleUp || _swallowRightUp || _swallowLeftUp || _swallowXButtonUp != 0;
    private bool ShouldKeepMouseHook => _mousePattern != MouseActivationPattern.None
        || _dismissOnExternalClick
        || _mouseActivationState.IsPending
        || HasSuppressedButtonUps;

    private bool IsActivationGestureCandidate(int message, uint mouseData)
    {
        MouseActivationButton button = GetActivationButton(message, mouseData);
        if (_mouseActivationState.IsPending)
        {
            return _mouseActivationState.IsExpectedChord(_mousePattern, button);
        }

        return message switch
        {
            WmMButtonDown => _mousePattern is MouseActivationPattern.MiddleClick or MouseActivationPattern.MiddleThenRight,
            WmRButtonDown => _mousePattern == MouseActivationPattern.RightThenLeft,
            WmLButtonDown => _mousePattern == MouseActivationPattern.RightThenLeft
                && _nativeMouseChord?.HasCandidate == true,
            WmXButtonDown => (_mousePattern == MouseActivationPattern.XButton1 && button == MouseActivationButton.XButton1)
                || (_mousePattern == MouseActivationPattern.XButton2 && button == MouseActivationButton.XButton2),
            _ => false
        };
    }

    private void QueueExternalButtonDownIfOutside(POINT point)
    {
        if (_dismissOnExternalClick && _window.IsVisible && IsOutsideProcessWindow(point))
        {
            QueueExternalButtonDown();
        }
    }

    private bool IsOutsideProcessWindow(POINT point)
    {
        if (!_dismissOnExternalClick || !_window.IsVisible)
        {
            return false;
        }

        return DesktopHitTest.ProcessAtPhysicalPoint(point.X, point.Y) != _processId;
    }

    private void QueueExternalButtonDown()
    {
        void RaiseIfStillVisible()
        {
            if (_disposed || !_dismissOnExternalClick || !_window.IsVisible)
            {
                return;
            }

            try
            {
                ExternalButtonDown?.Invoke();
            }
            catch
            {
                // UI通知先の例外をフック処理へ伝播させません。
            }
        }

        if (!_dismissOnExternalClick || !_window.IsVisible)
        {
            return;
        }

        if (_window.Dispatcher.CheckAccess())
        {
            RaiseIfStillVisible();
            return;
        }

        try
        {
            _window.Dispatcher.Invoke(new Action(RaiseIfStillVisible), DispatcherPriority.Send);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher終了後の通知は破棄します。
        }
    }

    private static bool IsButtonDownMessage(int message) =>
        message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;

    private static bool IsButtonUpMessage(int message) =>
        message is WmLButtonUp or WmRButtonUp or WmMButtonUp or WmXButtonUp;

    private static int GetXButtonId(uint mouseData) => (int)((mouseData >> 16) & 0xFFFF);

    private static MouseActivationButton GetActivationButton(int message, uint mouseData) => message switch
    {
        WmMButtonDown or WmMButtonUp => MouseActivationButton.Middle,
        WmRButtonDown or WmRButtonUp => MouseActivationButton.Right,
        WmLButtonDown or WmLButtonUp => MouseActivationButton.Left,
        WmXButtonDown or WmXButtonUp => GetXButtonId(mouseData) == 1
            ? MouseActivationButton.XButton1
            : MouseActivationButton.XButton2,
        _ => default
    };

    private static uint GetMouseButtonFlag(MouseActivationButton button, bool isDown) => (button, isDown) switch
    {
        (MouseActivationButton.Middle, true) => MouseEventMiddleDown,
        (MouseActivationButton.Middle, false) => MouseEventMiddleUp,
        (MouseActivationButton.Right, true) => MouseEventRightDown,
        (MouseActivationButton.Right, false) => MouseEventRightUp,
        (MouseActivationButton.Left, true) => MouseEventLeftDown,
        (MouseActivationButton.Left, false) => MouseEventLeftUp,
        _ => 0
    };

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

    private static bool AreOtherMouseButtonsDownExcept(params MouseActivationButton[] ignoredButtons)
    {
        ReadOnlySpan<(MouseActivationButton Button, uint VirtualKey)> buttons =
        [
            (MouseActivationButton.Left, VkLeftButton),
            (MouseActivationButton.Right, VkRightButton),
            (MouseActivationButton.Middle, VkMiddleButton),
            (MouseActivationButton.XButton1, VkXButton1),
            (MouseActivationButton.XButton2, VkXButton2)
        ];

        foreach ((MouseActivationButton button, uint virtualKey) in buttons)
        {
            bool ignored = false;
            foreach (MouseActivationButton ignoredButton in ignoredButtons)
            {
                if (button == ignoredButton)
                {
                    ignored = true;
                    break;
                }
            }

            if (!ignored && (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreOtherMouseButtonsDownExcept(MouseActivationButton first, MouseActivationButton second)
    {
        return AreOtherMouseButtonsDownExcept(new[] { first, second });
    }

    private static bool AreOtherMouseButtonsDownExcept(MouseActivationButton button)
    {
        return AreOtherMouseButtonsDownExcept(new[] { button });
    }

    private void TryFinishDisposedMouseHook()
    {
        if (!_disposed || HasSuppressedButtonUps || _mouseHook == IntPtr.Zero)
        {
            return;
        }

        if (UnhookWindowsHookEx(_mouseHook))
        {
            _mouseHook = IntPtr.Zero;
            ActiveMouseHookCallbacks.Remove(_mouseHookProc);
        }
        else
        {
            RaiseWarning($"終了後のマウスフック解除に失敗しました (Win32: {Marshal.GetLastWin32Error()})。");
        }
    }

    private readonly record struct MouseDragRestoreResult(bool ButtonDownInserted, bool CurrentMoveInserted);

    private bool CanSafelyInterceptAt(POINT point)
    {
        uint processId = DesktopHitTest.ProcessAtPhysicalPoint(point.X, point.Y);
        return processId != 0
            && processId != (uint)Environment.ProcessId
            && CanSafelyInjectIntoProcess(processId);
    }

    private bool CanSafelyInjectAt(POINT point)
    {
        uint processId = DesktopHitTest.ProcessAtPhysicalPoint(point.X, point.Y);
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
        if (ShouldKeepMouseHook || _mouseHook == IntPtr.Zero)
        {
            return;
        }

        if (UnhookWindowsHookEx(_mouseHook))
        {
            _mouseHook = IntPtr.Zero;
            ActiveMouseHookCallbacks.Remove(_mouseHookProc);
        }
        else
        {
            RaiseWarning($"マウス操作フックを解除できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
        }
    }

    private void ShowWindow()
    {
        if (_showLauncher != null)
        {
            _showLauncher();
            return;
        }

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

    private void QueueMouseActivation()
    {
        long ticket = _mouseActivationTicket = _mouseActivationCompletion.Begin();
        if (!HasSuppressedButtonUps && _nativeMouseChord?.IsGestureActive != true)
            _mouseActivationCompletion.Release();
        QueueUiAction(() =>
        {
            if (_disposed) return;
            // Cancel invalidates only the follow-up, not the gesture's accepted toggle.
            _toggle();
            Action? completion = _mouseActivationCompletion.Capture(ticket, _captureMouseActivationCompletion?.Invoke());
            if (completion != null) QueueMouseActivationCompletion(ticket, completion);
        });
    }


    private IntPtr BeginNativeMouseActivation(uint sequence)
    {
        _nativeChordSequence = sequence;
        long ticket = _mouseActivationTicket = _mouseActivationCompletion.Begin();
        _mouseActivationCallbackQueued = false;
        try
        {
            // 呼び出し元のWH_MOUSE callbackへ表示結果を返すため、この通知内で表示する。
            // 元のforegroundプロセスがその後SetForegroundWindowを要求する。
            _toggle();
            if (_nativeMouseChord?.PendingButtons == 0) _mouseActivationCompletion.Release();
            Action? completion = _mouseActivationCompletion.Capture(ticket, _captureMouseActivationCompletion?.Invoke());
            if (completion is not null) QueueMouseActivationCompletion(ticket, completion);
            return new IntPtr(_window.IsVisible ? 1 : 2);
        }
        catch (Exception exception)
        {
            CancelMouseActivationCompletion();
            QueueWarning($"右＋左での呼び出しに失敗しました: {exception.Message}");
            return IntPtr.Zero;
        }
    }

    private void ObserveDesktopDoubleClick(int message, MSLLHOOKSTRUCT mouse)
    {
        if (message == WmMouseMove)
        {
            _desktopDoubleClickState.Move(mouse.Point.X, mouse.Point.Y, _desktopDoubleClickState.IsPressed);
            if (_desktopHitTestPending && !IsWithinDesktopRequest(mouse.Point)) _desktopRequestTicket++;
            return;
        }

        if (message == WmLButtonDown)
        {
            _desktopRequestTicket++;
            if (AreOtherMouseButtonsDownExcept(MouseActivationButton.Left))
            {
                _desktopDoubleClickState.Cancel();
                return;
            }
            _desktopToleranceX = Math.Max(1, GetSystemMetrics(SmCxDoubleClick) / 2);
            _desktopToleranceY = Math.Max(1, GetSystemMetrics(SmCyDoubleClick) / 2);
            IntPtr hit = DesktopHitTest.WindowAtPhysicalPoint(mouse.Point.X, mouse.Point.Y);
            if (_desktopDoubleClickState.Down(mouse.Point.X, mouse.Point.Y, mouse.Time,
                GetDoubleClickTime(), _desktopToleranceX, _desktopToleranceY))
                _desktopSecondWindow = hit;
            else
            {
                _desktopFirstPoint = mouse.Point;
                _desktopFirstWindow = hit;
                _desktopFirstClickWasVisible = _window.IsVisible;
                _desktopSecondWindow = IntPtr.Zero;
            }
            return;
        }

        if (message == WmLButtonUp)
        {
            if (_desktopDoubleClickState.Up(mouse.Point.X, mouse.Point.Y))
                QueueDesktopDoubleClick(mouse.Point);
            return;
        }

        if (IsButtonDownMessage(message) || IsButtonUpMessage(message)
            || message is WmMouseWheel or WmMouseHWheel) CancelDesktopDoubleClick();
    }

    private void CancelDesktopDoubleClick()
    {
        _desktopDoubleClickState.Cancel();
        _desktopRequestTicket++;
    }

    private bool IsWithinDesktopRequest(POINT point) =>
        Math.Abs((long)point.X - _desktopRequestPoint.X) <= _desktopToleranceX
        && Math.Abs((long)point.Y - _desktopRequestPoint.Y) <= _desktopToleranceY;

    private void QueueDesktopDoubleClick(POINT point)
    {
        if (_desktopHitTestPending || _desktopFirstWindow == IntPtr.Zero
            || _desktopFirstWindow != _desktopSecondWindow) return;
        POINT first = _desktopFirstPoint;
        IntPtr view = _desktopSecondWindow;
        long request = _desktopRequestTicket;
        bool wasVisible = _desktopFirstClickWasVisible;
        _desktopRequestPoint = point;
        _desktopHitTestPending = true;
        // MSAAの跨プロセス照会は入力フック/UIスレッドで待たず、同時に1件だけ行う。
        _ = Task.Run(() =>
        {
            bool blank = false;
            try
            {
                blank = DesktopHitTest.WindowAtPhysicalPoint(first.X, first.Y) == view
                    && DesktopHitTest.WindowAtPhysicalPoint(point.X, point.Y) == view
                    && DesktopDoubleClick.IsBlankAt(first.X, first.Y)
                    && DesktopDoubleClick.IsBlankAt(point.X, point.Y);
            }
            catch
            {
                // 不明なデスクトップや照会失敗は、通常のクリックだけを維持する。
            }
            QueueUiAction(() =>
            {
                _desktopHitTestPending = false;
                if (_disposed || !blank || request != _desktopRequestTicket
                    || _mousePattern != MouseActivationPattern.DesktopDoubleClick
                    || wasVisible != _window.IsVisible
                    || AreOtherMouseButtonsDownExcept()
                    || !GetPhysicalCursorPos(out POINT current) || !IsWithinDesktopRequest(current)
                    || DesktopHitTest.WindowAtPhysicalPoint(point.X, point.Y) != view) return;
                QueueMouseActivation();
            });
        });
    }

    private void QueueMouseActivationCompletion(long ticket, Action completion)
    {
        _mouseActivationCallbackQueued = true;
        QueueUiAction(() =>
        {
            if (_disposed || !_mouseActivationCompletion.IsCurrent(ticket)) return;
            _mouseActivationCallbackQueued = false;
            completion();
        });
    }

    private void OnLauncherVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!_window.IsVisible) _nativeMouseChord?.ClearMenuGuard();
    }

    private void OnLauncherKeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        _nativeMouseChord?.ClearMenuGuard();
        CancelMouseActivationCompletion();
    }

    private void CancelMouseActivationCompletion()
    {
        _mouseActivationCompletion.Cancel();
        _mouseActivationCallbackQueued = false;
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
    private static extern bool GetPhysicalCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

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
