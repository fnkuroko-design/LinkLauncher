using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LinkLauncher.Services;

/// <summary>
/// 物理入力を遮断せず、呼び出し入力の仲介と元の標準メニューの終了を管理します。
/// 入力の生成やカーソル移動は行いません。
/// </summary>
internal sealed class NativeMouseChord : IDisposable
{
    internal const int NotificationMessage = 0x8039;
    internal const int BeginNotification = 1;
    internal const int CompleteNotification = 2;
    internal const int MenuFocusNotification = 3;
    private const uint ReadyStatus = 53; // mouse + callwnd menu + WinEvent menu + enabled。

    // Unhook直後も既存callbackが終了処理中である可能性があるため、DLLはプロセスの寿命まで保持します。
    private static Bindings? _bindings;
    private readonly Bindings _api;
    private bool _disposed;

    private NativeMouseChord(Bindings api) => _api = api;

    internal uint PendingButtons => _disposed ? 0 : _api.PendingButtons();
    internal uint Sequence => _disposed ? 0 : _api.Sequence();
    internal uint Status => _disposed ? 0 : _api.Status();
    internal bool IsGestureActive => PendingButtons != 0;
    internal bool HasCandidate => !_disposed && _api.HasCandidate() != 0;

    internal static bool TryStart(IntPtr owner, out NativeMouseChord? controller, out string error)
    {
        controller = null;
        error = string.Empty;
        NativeMouseChord? created = null;
        try
        {
            if (!Environment.Is64BitProcess || owner == IntPtr.Zero)
                throw new InvalidOperationException("右＋左の入力仲介にはWindows x64版が必要です。");

            string directory = AppContext.BaseDirectory;
            string library = Path.Combine(directory, "LinkLauncher.MouseHook.x64.dll");
            if (!File.Exists(library)) throw new FileNotFoundException("入力仲介の補助ファイルがありません。", library);

            Bindings api = _bindings ??= new Bindings(library);
            created = new NativeMouseChord(api);
            if (api.Install(owner, (uint)Environment.ProcessId) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "64bit入力仲介を開始できませんでした。");

            if ((api.Status() & ReadyStatus) != ReadyStatus)
                throw new InvalidOperationException("入力仲介の開始を確認できませんでした。");

            controller = created;
            return true;
        }
        catch (Exception exception)
        {
            created?.Dispose();
            error = exception.Message;
            return false;
        }
    }

    internal bool TryTakeRequest(uint sequence) => !_disposed && _api.TakeRequest(sequence) != 0;

    internal void ClearMenuGuard()
    {
        if (!_disposed) _api.ClearMenuGuard();
    }

    internal bool RestoreMenuFocus()
    {
        uint result = _disposed ? 0 : _api.RestoreMenuFocus();
#if INPUT_PROBE
        ChordInputProbe.Record($"native menu focus result={result}");
#endif
        return (result & 4) != 0 && (result & 32) == 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _api.Stop(); } catch { }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int InstallFunction(IntPtr owner, uint processId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StopFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TakeFunction(uint sequence);

    private sealed class Bindings
    {
        internal readonly InstallFunction Install;
        internal readonly StopFunction Stop;
        internal readonly StopFunction ClearMenuGuard;
        internal readonly ReadFunction RestoreMenuFocus;
        internal readonly ReadFunction PendingButtons;
        internal readonly ReadFunction Sequence;
        internal readonly ReadFunction Status;
        internal readonly ReadFunction HasCandidate;
        internal readonly TakeFunction TakeRequest;

        internal Bindings(string path)
        {
            IntPtr library = NativeLibrary.Load(path);
            Install = Export<InstallFunction>(library, "BridgeInstall");
            Stop = Export<StopFunction>(library, "BridgeStop");
            ClearMenuGuard = Export<StopFunction>(library, "BridgeClearMenuGuard");
            RestoreMenuFocus = Export<ReadFunction>(library, "BridgeRestoreMenuFocus");
            PendingButtons = Export<ReadFunction>(library, "BridgePendingButtons");
            Sequence = Export<ReadFunction>(library, "BridgeSequence");
            Status = Export<ReadFunction>(library, "BridgeStatus");
            HasCandidate = Export<ReadFunction>(library, "BridgeHasCandidate");
            TakeRequest = Export<TakeFunction>(library, "BridgeTakeRequest");
        }

        private static T Export<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

}
