using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LinkLauncher.Services;

/// <summary>
/// 物理入力を遮断せず、WH_MOUSEによるウィンドウ宛ての呼び出し入力の仲介を管理します。
/// 入力の生成やカーソル移動は行いません。
/// </summary>
internal sealed class NativeMouseChord : IDisposable
{
    internal const int NotificationMessage = 0x8039;
    internal const int BeginNotification = 1;
    internal const int CompleteNotification = 2;
    private const uint InstalledArchitectures = 3;
    private const int WmQuit = 0x0012;

    // Unhook直後も既存callbackが終了処理中である可能性があるため、DLLはプロセスの寿命まで保持します。
    private static Bindings? _bindings;
    private readonly Bindings _api;
    private Process? _host;
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
            string hostLibrary = Path.Combine(directory, "LinkLauncher.MouseHook.x86.dll");
            string hostExecutable = Path.Combine(directory, "LinkLauncher.MouseHookHost.x86.exe");
            foreach (string path in new[] { library, hostLibrary, hostExecutable })
                if (!File.Exists(path)) throw new FileNotFoundException("入力仲介の補助ファイルがありません。", path);

            Bindings api = _bindings ??= new Bindings(library);
            created = new NativeMouseChord(api);
            if (api.Install(owner, (uint)Environment.ProcessId) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "64bit入力仲介を開始できませんでした。");

            var start = new ProcessStartInfo(hostExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(unchecked((ulong)owner.ToInt64()).ToString("X", CultureInfo.InvariantCulture));
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            created._host = Process.Start(start) ?? throw new InvalidOperationException("32bit入力仲介を開始できませんでした。");

            var waiting = Stopwatch.StartNew();
            while ((api.Status() & InstalledArchitectures) != InstalledArchitectures)
            {
                if (created._host.HasExited || waiting.ElapsedMilliseconds >= 750)
                    throw new InvalidOperationException("32bit入力仲介の開始を確認できませんでした。");
                Thread.Sleep(10);
            }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _api.Stop(); } catch { }
        if (_host is not null)
        {
            try
            {
                if (!_host.HasExited)
                {
                    // このインスタンスが起動した補助プロセスのスレッドだけに終了を通知します。
                    foreach (ProcessThread thread in _host.Threads)
                    {
                        try { PostThreadMessage((uint)thread.Id, WmQuit, UIntPtr.Zero, IntPtr.Zero); }
                        finally { thread.Dispose(); }
                    }
                    if (!_host.WaitForExit(500)) _host.Kill();
                }
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            finally { _host.Dispose(); _host = null; }
        }
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
            PendingButtons = Export<ReadFunction>(library, "BridgePendingButtons");
            Sequence = Export<ReadFunction>(library, "BridgeSequence");
            Status = Export<ReadFunction>(library, "BridgeStatus");
            HasCandidate = Export<ReadFunction>(library, "BridgeHasCandidate");
            TakeRequest = Export<TakeFunction>(library, "BridgeTakeRequest");
        }

        private static T Export<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, int message, UIntPtr wParam, IntPtr lParam);
}
