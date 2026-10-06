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
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkControl = 0x11;
    private const uint VkSpace = 0x20;
    private const uint LlMouseFlagInjected = 0x00000001;
    private const uint LlMouseFlagLowerIlInjected = 0x00000002;
    private const int GestureMinimumRise = 70;
    private const int ChordMaximumDrift = 12;
    private const ulong GestureMaximumDurationMs = 1400;
    private const uint MaxHotkeyId = 0xBFFF;

    private static int _nextHotkeyId;

    private readonly Window _window;
    private readonly Action _toggle;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly LowLevelMouseProc _mouseHookProc;
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
    private bool _rightPressTracked;
    private bool _rightPressStartedWithControl;
    private POINT _rightPressStart;
    private ulong _rightPressStartTick;
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

        _mouseChordEnabled = mouseChordEnabled;
        _gestureEnabled = gestureEnabled;
        if (!needsMouseHook)
        {
            _rightPressTracked = false;
            if (_mouseHook != IntPtr.Zero)
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

        if (_mouseHook != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_mouseHook))
            {
                RaiseWarning($"マウスフックを解除できませんでした (Win32: {Marshal.GetLastWin32Error()})。");
            }

            _mouseHook = IntPtr.Zero;
        }

        _rightPressTracked = false;

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
        try
        {
            if (code >= 0 && _mouseHook != IntPtr.Zero && (_mouseChordEnabled || _gestureEnabled))
            {
                int message = unchecked((int)wParam.ToInt64());
                if (message == WmRButtonDown || message == WmRButtonUp)
                {
                    MSLLHOOKSTRUCT mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    uint injectedFlags = LlMouseFlagInjected | LlMouseFlagLowerIlInjected;
                    if ((mouse.Flags & injectedFlags) != 0)
                    {
                        return CallNextHookSafely(code, wParam, lParam);
                    }

                    if (message == WmRButtonDown)
                    {
                        if (IsOwnProcessWindow(mouse.Point))
                        {
                            _rightPressTracked = false;
                            return CallNextHookSafely(code, wParam, lParam);
                        }

                        _rightPressStart = mouse.Point;
                        _rightPressStartTick = GetTickCount64();
                        _rightPressStartedWithControl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
                        _rightPressTracked = true;
                    }
                    else if (_rightPressTracked)
                    {
                        _rightPressTracked = false;
                        ulong duration = GetTickCount64() - _rightPressStartTick;
                        int horizontalDrift = Math.Abs(mouse.Point.X - _rightPressStart.X);
                        int upwardDistance = _rightPressStart.Y - mouse.Point.Y;

                        bool chordRecognized = _mouseChordEnabled
                            && _rightPressStartedWithControl
                            && duration <= GestureMaximumDurationMs
                            && horizontalDrift <= ChordMaximumDrift
                            && Math.Abs(mouse.Point.Y - _rightPressStart.Y) <= ChordMaximumDrift;

                        bool gestureRecognized = _gestureEnabled
                            && duration <= GestureMaximumDurationMs
                            && upwardDistance >= GestureMinimumRise
                            && upwardDistance > horizontalDrift * 1.5;

                        if (chordRecognized || gestureRecognized)
                        {
                            QueueUiAction(_toggle);
                            return new IntPtr(1);
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            QueueWarning($"マウス操作フックでエラーが発生しました: {exception.Message}");
        }

        return CallNextHookSafely(code, wParam, lParam);
    }

    private bool IsOwnProcessWindow(POINT point)
    {
        IntPtr targetWindow = WindowFromPoint(point);
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(targetWindow, out uint processId);
        return processId == (uint)Environment.ProcessId;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(uint virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
