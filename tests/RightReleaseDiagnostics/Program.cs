using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FormsTimer = System.Windows.Forms.Timer;

internal static class Program
{
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const uint VkLButton = 0x01;
    private const uint VkRButton = 0x02;
    private const uint LlMouseFlagInjected = 0x00000001;
    private const uint LlMouseFlagLowerIlInjected = 0x00000002;
    private const int CaptureDurationSeconds = 180;
    private const int DeferredSnapshotMilliseconds = 25;
    private const int TimerIntervalMilliseconds = 5;

    private static readonly LowLevelMouseProc HookCallbackRoot = MouseHookCallback;
    private static readonly Queue<PendingObservation> PendingObservations = new();
    private static readonly Queue<string> PendingDiagnostics = new();

    private static FormsTimer? _timer;
    private static long _startedAt;
    private static long _captureDeadline;
    private static long _eventSequence;
    private static IntPtr _hookHandle;
    private static bool _acceptingEvents;
    private static bool _shutdownStarted;
    private static bool _fatalObservationError;
    private static bool _outputFailed;
    private static int _droppedObservations;

    private static int Main()
    {
        _startedAt = Stopwatch.GetTimestamp();

        try
        {
            if (!TryInstallHook())
            {
                return 2;
            }

            _captureDeadline = _startedAt + MillisecondsToStopwatchTicks(CaptureDurationSeconds * 1000L);
            _timer = new FormsTimer { Interval = TimerIntervalMilliseconds };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            WriteLineSafely("START scope=WH_MOUSE_LL events=R/L_DOWN_UP duration_seconds=180");
            WriteLineSafely("Each event is forwarded unchanged. GetAsyncSnapshot values are observations, not causal conclusions.");

            Application.Run();
            return _fatalObservationError || _outputFailed ? 3 : 0;
        }
        catch (Exception error)
        {
            WriteLineSafely($"FATAL type={error.GetType().Name} message={Clean(error.Message)}");
            return 3;
        }
        finally
        {
            _acceptingEvents = false;
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
                _timer.Dispose();
                _timer = null;
            }

            TryUnhook();
            if (_droppedObservations != 0)
            {
                WriteLineSafely($"DIAGNOSTIC dropped_observations={_droppedObservations}");
            }

            try
            {
                Console.Out.Flush();
            }
            catch (IOException)
            {
                // The redirected output may have been closed by its reader.
            }
            catch (ObjectDisposedException)
            {
                // The redirected output may have been closed by its reader.
            }
        }
    }

    private static bool TryInstallHook()
    {
        try
        {
            short left = GetAsyncKeyState(VkLButton);
            short right = GetAsyncKeyState(VkRButton);
            WriteLineSafely($"STARTUP GetAsyncSnapshot L={FormatKeyState(left)} R={FormatKeyState(right)}");

            IntPtr module = GetModuleHandle(null);
            if (module == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                WriteLineSafely($"STARTUP_FAILURE api=GetModuleHandleW win32={error}");
                return false;
            }

            _hookHandle = SetWindowsHookEx(WhMouseLl, HookCallbackRoot, module, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                WriteLineSafely($"STARTUP_FAILURE api=SetWindowsHookExW hook=WH_MOUSE_LL win32={error}");
                return false;
            }

            _acceptingEvents = true;
            return true;
        }
        catch (Exception error)
        {
            WriteLineSafely($"STARTUP_FAILURE type={error.GetType().Name} message={Clean(error.Message)}");
            return false;
        }
    }

    private static IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        PendingObservation? observation = null;
        if (code == HcAction && _acceptingEvents && Stopwatch.GetTimestamp() < _captureDeadline)
        {
            int message = unchecked((int)wParam.ToInt64());
            if (TryGetEventName(message, out string eventName))
            {
                try
                {
                    long eventTimestamp = Stopwatch.GetTimestamp();
                    MSLLHOOKSTRUCT mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    AsyncSnapshot before = GetAsyncSnapshot();
                    long sequence = ++_eventSequence;
                    long dueTimestamp = eventTimestamp + MillisecondsToStopwatchTicks(DeferredSnapshotMilliseconds);
                    observation = new PendingObservation(
                        sequence,
                        eventTimestamp,
                        dueTimestamp,
                        eventName,
                        mouse.Flags,
                        before);
                }
                catch (Exception error)
                {
                    QueueDiagnosticSafely(
                        $"OBSERVATION_FAILURE type={error.GetType().Name} message={Clean(error.Message)}");
                    _fatalObservationError = true;
                }
            }
        }

        // Keep the downstream result unchanged. Observation and queuing failures are isolated from this call.
        IntPtr nextResult = CallNextHookEx(_hookHandle, code, wParam, lParam);

        if (observation != null)
        {
            try
            {
                observation.CallNextResult = nextResult;
                PendingObservations.Enqueue(observation);
            }
            catch (Exception)
            {
                _droppedObservations++;
            }
        }

        return nextResult;
    }

    private static void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        long now = Stopwatch.GetTimestamp();

        while (PendingDiagnostics.Count > 0)
        {
            WriteLineSafely(PendingDiagnostics.Dequeue());
        }

        while (PendingObservations.Count > 0 && PendingObservations.Peek().DueTimestamp <= now)
        {
            PendingObservation observation = PendingObservations.Dequeue();
            try
            {
                AsyncSnapshot deferred = GetAsyncSnapshot();
                long sampledAt = Stopwatch.GetTimestamp();
                WriteLineSafely(FormatObservation(observation, deferred, sampledAt));
            }
            catch (Exception error)
            {
                WriteLineSafely(
                    $"DEFERRED_FAILURE event_id={observation.Sequence} type={error.GetType().Name} message={Clean(error.Message)}");
                _fatalObservationError = true;
            }
        }

        if (!_shutdownStarted && (_fatalObservationError || _outputFailed || now >= _captureDeadline))
        {
            BeginShutdown(now);
        }

        if (_shutdownStarted && PendingObservations.Count == 0)
        {
            if (_droppedObservations != 0)
            {
                WriteLineSafely($"DIAGNOSTIC dropped_observations={_droppedObservations}");
                _droppedObservations = 0;
            }

            WriteLineSafely($"END elapsed_ms={ElapsedMilliseconds(Stopwatch.GetTimestamp()).ToString("F3", CultureInfo.InvariantCulture)}");
            _timer?.Stop();
            Application.ExitThread();
        }
    }

    private static void BeginShutdown(long now)
    {
        _shutdownStarted = true;
        _acceptingEvents = false;
        WriteLineSafely(_fatalObservationError
            ? "STOP reason=observation_failure"
            : _outputFailed
                ? "STOP reason=output_failure"
                : $"STOP reason=duration elapsed_ms={ElapsedMilliseconds(now).ToString("F3", CultureInfo.InvariantCulture)}");
        TryUnhook();
    }

    private static void TryUnhook()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        if (UnhookWindowsHookEx(_hookHandle))
        {
            _hookHandle = IntPtr.Zero;
            return;
        }

        int error = Marshal.GetLastWin32Error();
        WriteLineSafely($"UNHOOK_FAILURE win32={error}");
    }

    private static AsyncSnapshot GetAsyncSnapshot() => new(
        GetAsyncKeyState(VkLButton),
        GetAsyncKeyState(VkRButton));

    private static string FormatObservation(PendingObservation observation, AsyncSnapshot deferred, long sampledAt)
    {
        uint injectedFlags = observation.Flags & (LlMouseFlagInjected | LlMouseFlagLowerIlInjected);
        string source = injectedFlags == 0 ? "physical" : "injected";
        double eventMs = ElapsedMilliseconds(observation.EventTimestamp);
        double sampleMs = ElapsedMilliseconds(sampledAt);
        double delayMs = MillisecondsBetween(observation.EventTimestamp, sampledAt);
        ulong callNext = unchecked((ulong)observation.CallNextResult.ToInt64());

        return string.Create(CultureInfo.InvariantCulture,
            $"EVENT id={observation.Sequence} t_ms={eventMs:F3} event={observation.EventName} source={source} flags=0x{observation.Flags:X8} lower_il_injected={(observation.Flags & LlMouseFlagLowerIlInjected) != 0} call_next_return=0x{callNext:X} downstream_consumed={observation.CallNextResult != IntPtr.Zero} GetAsyncSnapshot_Pre[{FormatSnapshot(observation.Before)}] GetAsyncSnapshot_Deferred25ms[sample_t_ms={sampleMs:F3};actual_delay_ms={delayMs:F3};{FormatSnapshot(deferred)}]");
    }

    private static string FormatSnapshot(AsyncSnapshot snapshot) =>
        $"L={FormatKeyState(snapshot.Left)};R={FormatKeyState(snapshot.Right)}";

    private static string FormatKeyState(short state)
    {
        ushort raw = unchecked((ushort)state);
        bool down = (raw & 0x8000) != 0;
        return $"raw=0x{raw:X4},down={down}";
    }

    private static bool TryGetEventName(int message, out string eventName)
    {
        eventName = message switch
        {
            WmLButtonDown => "L_DOWN",
            WmLButtonUp => "L_UP",
            WmRButtonDown => "R_DOWN",
            WmRButtonUp => "R_UP",
            _ => string.Empty
        };

        return eventName.Length != 0;
    }

    private static void QueueDiagnosticSafely(string message)
    {
        try
        {
            PendingDiagnostics.Enqueue(message);
        }
        catch (Exception)
        {
            _droppedObservations++;
        }
    }

    private static void WriteLineSafely(string message)
    {
        try
        {
            Console.WriteLine(message);
        }
        catch (IOException)
        {
            _outputFailed = true;
        }
        catch (ObjectDisposedException)
        {
            _outputFailed = true;
        }
    }

    private static string Clean(string message) => message.Replace('\r', ' ').Replace('\n', ' ');

    private static double ElapsedMilliseconds(long timestamp) => MillisecondsBetween(_startedAt, timestamp);

    private static double MillisecondsBetween(long start, long end) =>
        (end - start) * 1000.0 / Stopwatch.Frequency;

    private static long MillisecondsToStopwatchTicks(long milliseconds) =>
        (long)(milliseconds * (double)Stopwatch.Frequency / 1000.0);

    private sealed class PendingObservation
    {
        internal long Sequence { get; }
        internal long EventTimestamp { get; }
        internal long DueTimestamp { get; }
        internal string EventName { get; }
        internal uint Flags { get; }
        internal AsyncSnapshot Before { get; }
        internal IntPtr CallNextResult { get; set; }

        internal PendingObservation(long sequence, long eventTimestamp, long dueTimestamp,
            string eventName, uint flags, AsyncSnapshot before)
        {
            Sequence = sequence;
            EventTimestamp = eventTimestamp;
            DueTimestamp = dueTimestamp;
            EventName = eventName;
            Flags = flags;
            Before = before;
        }
    }

    private readonly record struct AsyncSnapshot(short Left, short Right);

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

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(uint virtualKey);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
