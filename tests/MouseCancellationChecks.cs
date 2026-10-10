using System;
using System.Collections.Generic;
using System.Linq;
using LinkLauncher.Services;

var checks = 0;

Check("Captureとchildとrootの通知先は重複しない", () =>
{
    FakePlatform platform = CreatePlatform();
    IntPtr capture = new(30);
    platform.AddWindow(capture, process: 2, thread: 10, root: new IntPtr(20));
    platform.Captures[10] = capture;

    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.SequenceEqual(new[] { capture, target.Window, target.Root }));
    Require(platform.Notifications.Distinct().Count() == platform.Notifications.Count);

    platform.Notifications.Clear();
    platform.Captures[10] = target.Window;
    Require(MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.SequenceEqual(new[] { target.Window, target.Root }));
    Require(platform.Notifications.Distinct().Count() == platform.Notifications.Count);

    platform.Notifications.Clear();
    platform.Identities[target.Root] = new Identity(target.ProcessId, target.ThreadId);
    platform.Captures[10] = target.Root;
    Require(MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.SequenceEqual(new[] { target.Root, target.Window }));
    Require(platform.Notifications.Distinct().Count() == platform.Notifications.Count);

    var topLevelPlatform = new FakePlatform { PointWindow = new IntPtr(50) };
    topLevelPlatform.AddWindow(new IntPtr(50), process: 2, thread: 40, root: new IntPtr(50));
    topLevelPlatform.Captures[40] = new IntPtr(50);
    MousePressTarget topLevelTarget = MousePressCancellation.Capture(100, 200, ownProcessId: 1, topLevelPlatform);
    Require(MousePressCancellation.TryCancel(topLevelTarget, 100, 200, topLevelPlatform));
    Require(topLevelPlatform.Notifications.SequenceEqual(new[] { topLevelTarget.Window }));
});

Check("Capture時に自プロセスの押下対象を拒否", () =>
{
    FakePlatform platform = CreatePlatform(process: 1, thread: 10);
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(!target.IsValid);
});

Check("CaptureはoriginのPIDとthreadを固定して保持", () =>
{
    FakePlatform platform = CreatePlatform(process: 2, thread: 10, rootThread: 11);
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(target.IsValid && target.ProcessId == 2 && target.ThreadId == 10);
});

Check("再利用されたorigin HWNDの別PIDを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.Identities[target.Window] = new Identity(3, 10);
    Require(!MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.Count == 0);
});

Check("再利用されたorigin HWNDの別threadを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.Identities[target.Window] = new Identity(2, 99);
    Require(!MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.Count == 0);
});

Check("現在の点が別rootなら拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    IntPtr otherRoot = new(40);
    IntPtr otherChild = new(41);
    platform.AddWindow(otherRoot, process: 2, thread: 30, root: otherRoot);
    platform.AddWindow(otherChild, process: 2, thread: 31, root: otherRoot);
    platform.PointWindow = otherChild;

    Require(!MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.Count == 0);
});

Check("同じPIDの別thread rootとchildは許容", () =>
{
    FakePlatform platform = CreatePlatform(rootThread: 20, currentThread: 30);
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(target.IsValid && target.ThreadId == 10);
    Require(MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.SequenceEqual(new[] { target.Window, target.Root }));

    platform.Notifications.Clear();
    platform.Captures.Remove(target.ThreadId);
    Require(MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.SequenceEqual(new[] { target.Window, target.Root }));
});

Check("消滅したrootを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.Identities.Remove(target.Root);
    Require(!MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.Count == 0);
});

Check("通知失敗時はfalseを返す", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.NotificationSucceeds = false;
    Require(!MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.SequenceEqual(new[] { target.Window }));
});

Check("capture照会失敗時は通知せずfalseを返す", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.CaptureQuerySucceeds = false;
    Require(!MousePressCancellation.TryCancel(target, 100, 200, platform));
    Require(platform.Notifications.Count == 0);
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
    if (!condition) throw new InvalidOperationException("マウスキャンセルの期待結果と一致しません。");
}

static FakePlatform CreatePlatform(uint process = 2, uint thread = 10, uint rootThread = 11, uint currentThread = 12)
{
    var platform = new FakePlatform { PointWindow = new IntPtr(10) };
    platform.AddWindow(new IntPtr(10), process, thread, new IntPtr(20));
    platform.AddWindow(new IntPtr(20), process, rootThread, new IntPtr(20));
    platform.AddWindow(new IntPtr(30), process, currentThread, new IntPtr(20));
    platform.Captures[thread] = new IntPtr(10);
    return platform;
}

internal readonly record struct Identity(uint ProcessId, uint ThreadId);

internal sealed class FakePlatform : IMouseCancellationPlatform
{
    internal IntPtr PointWindow { get; set; }
    internal bool CaptureQuerySucceeds { get; set; } = true;
    internal bool NotificationSucceeds { get; set; } = true;
    internal Dictionary<IntPtr, Identity> Identities { get; } = new();
    internal Dictionary<IntPtr, IntPtr> Roots { get; } = new();
    internal Dictionary<uint, IntPtr> Captures { get; } = new();
    internal List<IntPtr> Notifications { get; } = new();

    public IntPtr WindowAtPhysicalPoint(int x, int y) => PointWindow;

    public IntPtr RootWindow(IntPtr window) => Roots.TryGetValue(window, out IntPtr root) ? root : IntPtr.Zero;

    public bool TryGetIdentity(IntPtr window, out uint processId, out uint threadId)
    {
        if (Identities.TryGetValue(window, out Identity identity))
        {
            processId = identity.ProcessId;
            threadId = identity.ThreadId;
            return true;
        }

        processId = 0;
        threadId = 0;
        return false;
    }

    public bool TryGetCapture(uint threadId, out IntPtr window)
    {
        if (!CaptureQuerySucceeds)
        {
            window = IntPtr.Zero;
            return false;
        }

        window = Captures.TryGetValue(threadId, out IntPtr capture) ? capture : IntPtr.Zero;
        return true;
    }

    public bool NotifyCancelMode(IntPtr window)
    {
        Notifications.Add(window);
        return NotificationSucceeds;
    }

    internal void AddWindow(IntPtr window, uint process, uint thread, IntPtr root)
    {
        Identities[window] = new Identity(process, thread);
        Roots[window] = root;
    }
}
