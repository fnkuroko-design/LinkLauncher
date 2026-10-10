using System;
using System.Collections.Generic;
using System.Linq;
using LinkLauncher.Services;

var checks = 0;
const uint NativeDownTime = 12345;

Check("TryBeginはforeground/captureを変更せずnative DOWNを待つ", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var started = new List<string>();

    receiver.Started += () => started.Add("started");
    Require(receiver.TryBegin(120, 240, NativeDownTime));
    Require(receiver.IsActive && platform.Visible);
    Require(platform.LastX == 120 && platform.LastY == 240);
    Require(platform.Foreground == platform.PreviousForeground);
    Require(platform.Capture == IntPtr.Zero && started.Count == 0);
    Require(platform.ActiveTimerId == ChordReleaseReceiver.PreparationTimerId);
    Require(platform.LastTimerMilliseconds == ChordReleaseReceiver.PreparationMilliseconds);
});

Check("native clickのactivate後だけStartedし左右UP両順で完了する", () =>
{
    foreach ((int first, int second) in new[]
    {
        (ChordReleaseReceiverMessages.WmLButtonUp, ChordReleaseReceiverMessages.WmRButtonUp),
        (ChordReleaseReceiverMessages.WmRButtonUp, ChordReleaseReceiverMessages.WmLButtonUp)
    })
    {
        var platform = new FakePlatform();
        using var receiver = new ChordReleaseReceiver(platform);
        var events = new List<string>();
        var completed = new List<bool>();
        receiver.Started += () => events.Add("started");
        receiver.Completed += succeeded =>
        {
            events.Add("completed");
            completed.Add(succeeded);
        };

        Require(receiver.TryBegin(20, 30, NativeDownTime));
        Require(events.Count == 0);
        platform.DeliverNativeLeftClick(NativeDownTime);
        Require(events.SequenceEqual(new[] { "started" }));
        Require(receiver.IsActive && platform.Foreground == platform.Window && platform.Capture == platform.Window);
        Require(platform.ActiveTimerId == ChordReleaseReceiver.WatchdogTimerId);
        Require(platform.LastTimerMilliseconds == ChordReleaseReceiver.WatchdogMilliseconds);

        platform.Deliver(first);
        Require(receiver.IsActive && completed.Count == 0);
        platform.Deliver(second);
        platform.Deliver(first);
        platform.Deliver(second);
        Require(!receiver.IsActive && completed.SequenceEqual(new[] { true }));
        Require(events.SequenceEqual(new[] { "started", "completed" }));
        Require(platform.Capture == IntPtr.Zero && !platform.Visible);
        Require(platform.ReleaseCaptureCalls == 1 && !platform.TimerRunning);
        Require(platform.Foreground == platform.PreviousForeground);
    }
});

Check("WM_MOUSEACTIVATEは候補phaseだけactivateし遅延messageをeatする", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var started = 0;
    var completed = new List<bool>();
    IntPtr? activationDuringFinish = null;
    IntPtr? activationDuringDispose = null;
    receiver.Started += () => started++;
    receiver.Completed += completed.Add;
    platform.OnReleaseCapture = () =>
        activationDuringFinish = platform.Deliver(ChordReleaseReceiverMessages.WmMouseActivate);
    platform.OnDispose = () =>
        activationDuringDispose = platform.Deliver(ChordReleaseReceiverMessages.WmMouseActivate);

    var noActivateAndEat = new IntPtr(ChordReleaseReceiverMessages.MaNoActivateAndEat);
    Require(platform.Deliver(ChordReleaseReceiverMessages.WmMouseActivate) == noActivateAndEat);
    Require(receiver.TryBegin(1, 2, NativeDownTime));
    IntPtr? activation = platform.Deliver(ChordReleaseReceiverMessages.WmMouseActivate, messageTime: NativeDownTime);
    Require(activation == new IntPtr(ChordReleaseReceiverMessages.MaActivate));
    Require(started == 0 && platform.Capture == IntPtr.Zero);
    platform.DeliverNativeLeftClick(NativeDownTime);
    Require(started == 1 && platform.Capture == platform.Window && receiver.IsActive);
    Require(platform.Deliver(ChordReleaseReceiverMessages.WmMouseActivate) == new IntPtr(ChordReleaseReceiverMessages.MaActivate));

    platform.Deliver(ChordReleaseReceiverMessages.WmLButtonUp);
    platform.Deliver(ChordReleaseReceiverMessages.WmRButtonUp);
    Require(completed.SequenceEqual(new[] { true }));
    Require(activationDuringFinish == noActivateAndEat);
    Require(platform.Deliver(ChordReleaseReceiverMessages.WmMouseActivate) == noActivateAndEat);

    receiver.Dispose();
    Require(activationDuringDispose == noActivateAndEat);
});

Check("native再DOWNは対応UPだけを取り消しStartedを重ねない", () =>
{
    foreach ((int leftDown, int rightDown, int leftUp, int rightUp) in new[]
    {
        (ChordReleaseReceiverMessages.WmLButtonDown, ChordReleaseReceiverMessages.WmRButtonDown,
            ChordReleaseReceiverMessages.WmLButtonUp, ChordReleaseReceiverMessages.WmRButtonUp),
        (ChordReleaseReceiverMessages.WmNcLButtonDown, ChordReleaseReceiverMessages.WmNcRButtonDown,
            ChordReleaseReceiverMessages.WmNcLButtonUp, ChordReleaseReceiverMessages.WmNcRButtonUp)
    })
    {
        var platform = new FakePlatform();
        using var receiver = new ChordReleaseReceiver(platform);
        var events = new List<string>();
        var completed = new List<bool>();
        receiver.Started += () => events.Add("started");
        receiver.Completed += succeeded =>
        {
            events.Add("completed");
            completed.Add(succeeded);
        };

        Require(receiver.TryBegin(10, 20, NativeDownTime));
        platform.DeliverNativeLeftClick(NativeDownTime);
        platform.Deliver(leftUp);
        platform.Deliver(leftDown, lParam: new IntPtr(17), messageTime: NativeDownTime + 1);
        platform.Deliver(rightUp);
        Require(receiver.IsActive && completed.Count == 0 && events.SequenceEqual(new[] { "started" }));

        platform.Deliver(rightDown);
        platform.Deliver(leftUp);
        Require(receiver.IsActive && completed.Count == 0 && events.SequenceEqual(new[] { "started" }));
        platform.Deliver(rightUp);
        Require(!receiver.IsActive && completed.SequenceEqual(new[] { true }));
        Require(events.SequenceEqual(new[] { "started", "completed" }));
        Require(platform.Capture == IntPtr.Zero && !platform.Visible && platform.ReleaseCaptureCalls == 1);
    }
});

Check("native LEFTDOWNが受信窓をforegroundにしなければcaptureせず中断する", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var started = 0;
    var completed = new List<bool>();
    receiver.Started += () => started++;
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.DeliverNativeLeftClick(NativeDownTime, activateReceiver: false);
    Require(!receiver.IsActive && started == 0 && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && !platform.Visible);
    Require(platform.CaptureRequestCalls == 0);
});

Check("capture失敗はStartedせずfalseでcleanupする", () =>
{
    var platform = new FakePlatform { CaptureRequestSucceeds = false };
    using var receiver = new ChordReleaseReceiver(platform);
    var started = 0;
    var completed = new List<bool>();
    receiver.Started += () => started++;
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.DeliverNativeLeftClick(NativeDownTime);
    Require(!receiver.IsActive && started == 0 && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 0);
    Require(!platform.Visible && !platform.TimerRunning);
});

Check("capture要求中にforegroundが変われば自分のcaptureだけ解放し他foregroundを維持する", () =>
{
    IntPtr otherForeground = new(900);
    var platform = new FakePlatform();
    platform.OnCaptureRequest = () => platform.Foreground = otherForeground;
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.DeliverNativeLeftClick(NativeDownTime);
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 1);
    Require(platform.Foreground == otherForeground && platform.ForegroundRequestCalls == 0);
    Require(!platform.Visible);
});

Check("native LEFTDOWN未着で250ms timeoutし、次の操作を吸わない", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.Deliver(ChordReleaseReceiverMessages.WmTimer, new IntPtr(ChordReleaseReceiver.PreparationTimerId));
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && !platform.Visible && !platform.TimerRunning);

    Require(receiver.TryBegin(30, 40, NativeDownTime));
    Require(receiver.IsActive && platform.Visible);
    platform.Deliver(ChordReleaseReceiverMessages.WmLButtonDown, messageTime: NativeDownTime + 1);
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false, false }));
    Require(platform.Capture == IntPtr.Zero && !platform.Visible);
});

Check("native LEFTDOWN前のUPは不成立でcleanupする", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var started = 0;
    var completed = new List<bool>();
    receiver.Started += () => started++;
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.Deliver(ChordReleaseReceiverMessages.WmRButtonUp);
    Require(!receiver.IsActive && started == 0 && completed.SequenceEqual(new[] { false }));
    Require(!platform.Visible && platform.Capture == IntPtr.Zero);
});

Check("capture確立中に再入したUP両方を保持しStarted後に完了する", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var events = new List<string>();
    var completed = new List<bool>();
    receiver.Started += () => events.Add("started");
    receiver.Completed += succeeded =>
    {
        events.Add("completed");
        completed.Add(succeeded);
    };
    platform.OnCaptureRequest = () =>
    {
        platform.Deliver(ChordReleaseReceiverMessages.WmRButtonUp);
        platform.Deliver(ChordReleaseReceiverMessages.WmLButtonUp);
    };

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.DeliverNativeLeftClick(NativeDownTime);
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { true }));
    Require(events.SequenceEqual(new[] { "started", "completed" }));
    Require(platform.Capture == IntPtr.Zero && !platform.Visible && platform.ReleaseCaptureCalls == 1);
});

Check("capture喪失はfalseで完了し他windowのcaptureを解放しない", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var started = 0;
    var completed = new List<bool>();
    receiver.Started += () => started++;
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.DeliverNativeLeftClick(NativeDownTime);
    platform.Capture = new IntPtr(800);
    platform.Deliver(ChordReleaseReceiverMessages.WmCaptureChanged);
    Require(!receiver.IsActive && started == 1 && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == new IntPtr(800) && platform.ReleaseCaptureCalls == 0);
    Require(!platform.Visible);
});

Check("2秒watchdogとDisposeは安全に中断する", () =>
{
    var platform = new FakePlatform();
    var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20, NativeDownTime));
    platform.DeliverNativeLeftClick(NativeDownTime);
    platform.Deliver(ChordReleaseReceiverMessages.WmTimer, new IntPtr(ChordReleaseReceiver.PreparationTimerId));
    Require(receiver.IsActive && completed.Count == 0);
    platform.Deliver(ChordReleaseReceiverMessages.WmTimer, new IntPtr(ChordReleaseReceiver.WatchdogTimerId));
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 1 && !platform.Visible);

    Require(receiver.TryBegin(30, 40, NativeDownTime));
    platform.DeliverNativeLeftClick(NativeDownTime);
    receiver.Dispose();
    receiver.Dispose();
    Require(completed.SequenceEqual(new[] { false, false }));
    Require(platform.Capture == IntPtr.Zero && !platform.Visible && platform.Disposed);
    Require(!receiver.TryBegin(50, 60, NativeDownTime));
});

Console.WriteLine($"PASS {checks}/12");
return 0;

void Check(string name, Action action)
{
    action();
    checks++;
    Console.WriteLine("PASS " + name);
}

void Require(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("ChordReleaseReceiverの期待結果と一致しません。");
    }
}

internal sealed class FakePlatform : IChordReleaseReceiverPlatform
{
    private Func<int, IntPtr, IntPtr, IntPtr?>? _messageHandler;

    internal IntPtr Window { get; } = new(100);
    internal IntPtr PreviousForeground { get; } = new(200);
    internal IntPtr Foreground { get; set; } = new(200);
    internal IntPtr Capture { get; set; }
    internal bool Visible { get; private set; }
    internal bool CaptureRequestSucceeds { get; set; } = true;
    internal bool ShowSucceeds { get; set; } = true;
    internal bool WatchdogStarts { get; set; } = true;
    internal bool TimerRunning { get; private set; }
    internal uint ActiveTimerId { get; private set; }
    internal uint LastTimerMilliseconds { get; private set; }
    internal bool Disposed { get; private set; }
    internal int LastX { get; private set; }
    internal int LastY { get; private set; }
    internal int CaptureRequestCalls { get; private set; }
    internal int ReleaseCaptureCalls { get; private set; }
    internal int ForegroundRequestCalls { get; private set; }
    internal Action? OnCaptureRequest { get; set; }
    internal Action? OnReleaseCapture { get; set; }
    internal Action? OnDispose { get; set; }

    public bool IsOwnerThread => true;

    public IntPtr CreateWindow(Func<int, IntPtr, IntPtr, IntPtr?> messageHandler)
    {
        _messageHandler = messageHandler;
        return Window;
    }

    public IntPtr GetForegroundWindow() => Foreground;

    public IntPtr GetCapture() => Capture;

    public bool ShowAtPhysicalPoint(int x, int y)
    {
        if (!ShowSucceeds) return false;
        LastX = x;
        LastY = y;
        Visible = true;
        return true;
    }

    public void RequestForeground(IntPtr window)
    {
        ForegroundRequestCalls++;
        Foreground = window;
    }

    public void RequestCapture(IntPtr window)
    {
        CaptureRequestCalls++;
        if (CaptureRequestSucceeds) Capture = window;
        OnCaptureRequest?.Invoke();
    }

    public bool StartWatchdog(uint timerId, uint milliseconds)
    {
        if (!WatchdogStarts) return false;
        TimerRunning = true;
        ActiveTimerId = timerId;
        LastTimerMilliseconds = milliseconds;
        return true;
    }

    public void StopWatchdog(uint timerId)
    {
        if (ActiveTimerId == timerId)
        {
            TimerRunning = false;
            ActiveTimerId = 0;
        }
    }

    public void ReleaseCapture()
    {
        ReleaseCaptureCalls++;
        if (Capture == Window) Capture = IntPtr.Zero;
        OnReleaseCapture?.Invoke();
    }

    public void Hide() => Visible = false;

    public void Dispose()
    {
        Disposed = true;
        OnDispose?.Invoke();
        _messageHandler = null;
    }

    internal uint MessageTime { get; private set; }

    public uint GetMessageTime() => MessageTime;

    internal IntPtr? Deliver(
        int message,
        IntPtr wParam = default,
        IntPtr lParam = default,
        uint? messageTime = null)
    {
        if (messageTime.HasValue) MessageTime = messageTime.Value;
        return _messageHandler?.Invoke(message, wParam, lParam);
    }

    internal void DeliverNativeLeftClick(uint messageTime, bool activateReceiver = true, IntPtr lParam = default)
    {
        IntPtr? activation = Deliver(ChordReleaseReceiverMessages.WmMouseActivate, messageTime: messageTime);
        Require(activation == new IntPtr(ChordReleaseReceiverMessages.MaActivate));
        if (activateReceiver) Foreground = Window;
        Deliver(ChordReleaseReceiverMessages.WmLButtonDown, lParam: lParam, messageTime: messageTime);
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Fake native clickの期待結果と一致しません。");
        }
    }
}
