using System;
using System.Collections.Generic;
using LinkLauncher.Services;

var checks = 0;

Check("Capture時に自プロセスの押下対象を拒否", () =>
{
    FakePlatform platform = CreatePlatform(process: 1, thread: 10);
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(!target.IsValid);
});

Check("Captureは押下元のPIDとthreadを固定して保持", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(target.IsValid && target.ProcessId == 2 && target.ThreadId == 10);
});

Check("一致する対象と同threadのcaptureを許容", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("captureなしを許容", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.Captures.Remove(target.ThreadId);
    Require(MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("別PIDのcaptureを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    IntPtr otherProcess = new(40);
    platform.AddWindow(otherProcess, process: 3, thread: 30, root: new IntPtr(40));
    platform.Captures[target.ThreadId] = otherProcess;
    Require(!MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("同PIDでも別threadのcaptureを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    IntPtr otherThread = new(30);
    platform.AddWindow(otherThread, process: target.ProcessId, thread: 99, root: target.Root);
    platform.Captures[target.ThreadId] = otherThread;
    Require(!MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("再利用されたorigin HWNDの別PIDを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.Identities[target.Window] = new Identity(3, target.ThreadId);
    Require(!MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("再利用されたorigin HWNDの別threadを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.Identities[target.Window] = new Identity(target.ProcessId, 99);
    Require(!MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("現在位置が別rootなら拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    IntPtr otherRoot = new(40);
    IntPtr otherChild = new(41);
    platform.AddWindow(otherRoot, process: 2, thread: 30, root: otherRoot);
    platform.AddWindow(otherChild, process: 2, thread: 31, root: otherRoot);
    platform.PointWindow = otherChild;
    Require(!MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("同PID・同root内の別thread childを許容", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    IntPtr sibling = new(31);
    platform.AddWindow(sibling, process: target.ProcessId, thread: 99, root: target.Root);
    platform.PointWindow = sibling;
    Require(MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("消滅したrootを拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.Identities.Remove(target.Root);
    Require(!MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("capture照会失敗時は拒否", () =>
{
    FakePlatform platform = CreatePlatform();
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    platform.CaptureQuerySucceeds = false;
    Require(!MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Check("top-level windowを押下元として許容", () =>
{
    var platform = new FakePlatform { PointWindow = new IntPtr(50) };
    platform.AddWindow(new IntPtr(50), process: 2, thread: 40, root: new IntPtr(50));
    platform.Captures[40] = new IntPtr(50);
    MousePressTarget target = MousePressCancellation.Capture(100, 200, ownProcessId: 1, platform);
    Require(MousePressCancellation.CanTransfer(target, 100, 200, platform));
});

Console.WriteLine($"PASS {checks}/13");
return 0;

void Check(string name, Action action)
{
    action();
    checks++;
    Console.WriteLine("PASS " + name);
}

void Require(bool condition)
{
    if (!condition) throw new InvalidOperationException("押下対象の照合結果が期待と一致しません。");
}

static FakePlatform CreatePlatform(uint process = 2, uint thread = 10, uint rootThread = 11)
{
    var platform = new FakePlatform { PointWindow = new IntPtr(10) };
    platform.AddWindow(new IntPtr(10), process, thread, new IntPtr(20));
    platform.AddWindow(new IntPtr(20), process, rootThread, new IntPtr(20));
    platform.Captures[thread] = new IntPtr(10);
    return platform;
}

internal readonly record struct Identity(uint ProcessId, uint ThreadId);

internal sealed class FakePlatform : IMouseCancellationPlatform
{
    internal IntPtr PointWindow { get; set; }
    internal bool CaptureQuerySucceeds { get; set; } = true;
    internal Dictionary<IntPtr, Identity> Identities { get; } = new();
    internal Dictionary<IntPtr, IntPtr> Roots { get; } = new();
    internal Dictionary<uint, IntPtr> Captures { get; } = new();

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

    internal void AddWindow(IntPtr window, uint process, uint thread, IntPtr root)
    {
        Identities[window] = new Identity(process, thread);
        Roots[window] = root;
    }
}
