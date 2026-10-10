using System;
using System.Collections.Generic;
using System.Linq;
using LinkLauncher.Services;

var checks = 0;

Check("UPを左右どちらの順で受けても完了する", () =>
{
    foreach ((int first, int second) in new[]
    {
        (ChordReleaseReceiverMessages.WmLButtonUp, ChordReleaseReceiverMessages.WmRButtonUp),
        (ChordReleaseReceiverMessages.WmRButtonUp, ChordReleaseReceiverMessages.WmLButtonUp)
    })
    {
        var platform = new FakePlatform();
        using var receiver = new ChordReleaseReceiver(platform);
        var completed = new List<bool>();
        receiver.Completed += completed.Add;

        Require(receiver.TryBegin(120, 240));
        Require(receiver.IsActive);
        platform.Deliver(first);
        Require(receiver.IsActive && completed.Count == 0);
        platform.Deliver(second);
        Require(!receiver.IsActive && completed.SequenceEqual(new[] { true }));
        Require(platform.Capture == IntPtr.Zero && !platform.Visible);
        Require(platform.ReleaseCaptureCalls == 1);
    }
});

Check("重複UPは二重完了を起こさない", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(1, 2));
    platform.Deliver(ChordReleaseReceiverMessages.WmLButtonUp);
    platform.Deliver(ChordReleaseReceiverMessages.WmLButtonUp);
    Require(receiver.IsActive && completed.Count == 0);
    platform.Deliver(ChordReleaseReceiverMessages.WmRButtonUp);
    platform.Deliver(ChordReleaseReceiverMessages.WmRButtonUp);
    Require(completed.SequenceEqual(new[] { true }));
    Require(platform.ReleaseCaptureCalls == 1);
});

Check("capture確立失敗は開始を拒否しfalseを一度通知する", () =>
{
    var platform = new FakePlatform { CaptureRequestSucceeds = false };
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(!receiver.TryBegin(10, 20));
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture != platform.Window && platform.ReleaseCaptureCalls == 0);
    Require(!platform.Visible);
});

Check("foreground確認失敗はcaptureを試さず開始を拒否する", () =>
{
    var platform = new FakePlatform { ForegroundRequestSucceeds = false };
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(!receiver.TryBegin(10, 20));
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 0);
    Require(!platform.Visible);
});

Check("capture要求中にforegroundが変われば自分のcaptureだけ解放し前景を維持する", () =>
{
    IntPtr otherForeground = new(900);
    var platform = new FakePlatform();
    platform.OnCaptureRequest = () => platform.Foreground = otherForeground;
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(!receiver.TryBegin(10, 20));
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 1);
    Require(platform.Foreground == otherForeground);
    Require(!platform.Visible);
});

Check("capture喪失はfalseで完了し他windowのcaptureを解放しない", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20));
    platform.Capture = new IntPtr(800);
    platform.Deliver(ChordReleaseReceiverMessages.WmCaptureChanged);
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == new IntPtr(800) && platform.ReleaseCaptureCalls == 0);
    Require(!platform.Visible);
});

Check("watchdog timeoutはfalseで完了しcaptureを解放する", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20));
    Require(platform.WatchdogRunning);
    platform.Deliver(ChordReleaseReceiverMessages.WmTimer, new IntPtr(ChordReleaseReceiver.WatchdogTimerId));
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 1);
    Require(!platform.Visible && !platform.WatchdogRunning);
});

Check("watchdog開始失敗はfalseで完了しcaptureを解放する", () =>
{
    var platform = new FakePlatform { WatchdogStarts = false };
    using var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(!receiver.TryBegin(10, 20));
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 1);
    Require(!platform.Visible && !platform.WatchdogRunning);
});

Check("Disposeはセッションを安全に中断する", () =>
{
    var platform = new FakePlatform();
    var receiver = new ChordReleaseReceiver(platform);
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20));
    receiver.Dispose();
    receiver.Dispose();
    Require(completed.SequenceEqual(new[] { false }));
    Require(platform.Capture == IntPtr.Zero && platform.ReleaseCaptureCalls == 1);
    Require(!platform.Visible && platform.Disposed && !platform.WatchdogRunning);
    Require(!receiver.TryBegin(10, 20));
});

Check("setup中に再入した両UPは成功として保持される", () =>
{
    var platform = new FakePlatform();
    using var receiver = new ChordReleaseReceiver(platform);
    bool armingIsVisibleToReentrantHook = false;
    platform.OnCaptureRequest = () =>
    {
        armingIsVisibleToReentrantHook = receiver.IsActive;
        platform.Deliver(ChordReleaseReceiverMessages.WmRButtonUp);
        platform.Deliver(ChordReleaseReceiverMessages.WmLButtonUp);
    };
    var completed = new List<bool>();
    receiver.Completed += completed.Add;

    Require(receiver.TryBegin(10, 20));
    Require(armingIsVisibleToReentrantHook);
    Require(!receiver.IsActive && completed.SequenceEqual(new[] { true }));
    Require(!platform.WatchdogRunning && platform.Capture == IntPtr.Zero);
});

Console.WriteLine($"PASS {checks}/10");
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
    internal IntPtr Foreground { get; set; } = new(200);
    internal IntPtr Capture { get; set; }
    internal bool Visible { get; private set; }
    internal bool CaptureRequestSucceeds { get; set; } = true;
    internal bool ForegroundRequestSucceeds { get; set; } = true;
    internal bool ShowSucceeds { get; set; } = true;
    internal bool WatchdogStarts { get; set; } = true;
    internal bool WatchdogRunning { get; private set; }
    internal bool Disposed { get; private set; }
    internal int ReleaseCaptureCalls { get; private set; }
    internal Action? OnCaptureRequest { get; set; }

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
        Visible = true;
        return true;
    }

    public void RequestForeground(IntPtr window)
    {
        if (ForegroundRequestSucceeds) Foreground = window;
    }

    public void RequestCapture(IntPtr window)
    {
        if (CaptureRequestSucceeds) Capture = window;
        OnCaptureRequest?.Invoke();
    }

    public bool StartWatchdog(uint timerId, uint milliseconds)
    {
        if (!WatchdogStarts) return false;
        WatchdogRunning = true;
        return true;
    }

    public void StopWatchdog(uint timerId) => WatchdogRunning = false;

    public void ReleaseCapture()
    {
        ReleaseCaptureCalls++;
        if (Capture == Window) Capture = IntPtr.Zero;
    }

    public void Hide() => Visible = false;

    public void Dispose()
    {
        Disposed = true;
        _messageHandler = null;
    }

    internal void Deliver(int message, IntPtr wParam = default)
    {
        _messageHandler?.Invoke(message, wParam, IntPtr.Zero);
    }
}
