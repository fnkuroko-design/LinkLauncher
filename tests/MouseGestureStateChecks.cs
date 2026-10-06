using System;
using LinkLauncher.Services;

var checks = 0;
Check("上向きジェスチャーを認識", () =>
{
    var state = new RightMouseGestureState();
    Require(state.TryBegin(300, 500, 100, false));
    Require(!state.ShouldRestoreOnMove(302, 480, 180, false, true));
    Require(state.Release(302, 410, 420, false, true) == RightMouseReleaseAction.Recognized);
});
Check("Ctrl右クリックを認識", () =>
{
    var state = new RightMouseGestureState();
    Require(state.TryBegin(300, 500, 100, true));
    Require(!state.ShouldRestoreOnMove(309, 506, 220, true, false));
    Require(state.Release(309, 506, 300, true, false) == RightMouseReleaseAction.Recognized);
});
Check("横移動は通常ドラッグへ戻す", () =>
{
    var state = new RightMouseGestureState();
    Require(state.TryBegin(300, 500, 100, false));
    Require(state.ShouldRestoreOnMove(325, 500, 180, false, true));
    Require(state.Release(325, 500, 240, false, true) == RightMouseReleaseAction.ReplayClick);
});
Check("下方向への移動は通常操作へ戻す", () =>
{
    var state = new RightMouseGestureState();
    Require(state.TryBegin(300, 500, 100, false));
    Require(state.ShouldRestoreOnMove(300, 526, 180, false, true));
    Require(state.Release(300, 526, 240, false, true) == RightMouseReleaseAction.ReplayClick);
});
Check("長押し後の移動はジェスチャーにしない", () =>
{
    var state = new RightMouseGestureState();
    Require(state.TryBegin(300, 500, 100, false));
    Require(state.ShouldRestoreOnMove(300, 420, 1601, false, true));
    Require(state.Release(300, 420, 1700, false, true) == RightMouseReleaseAction.ReplayClick);
});
Check("設定変更や破棄時に保留状態を解除する", () =>
{
    var state = new RightMouseGestureState();
    Require(state.TryBegin(300, 500, 100, false));
    Require(state.Cancel());
    Require(!state.IsPending);
    Require(state.Release(300, 400, 200, false, true) == RightMouseReleaseAction.Pass);
});
Console.WriteLine($"PASS {checks}/6");
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
        throw new InvalidOperationException("マウス状態の期待結果と一致しません。");
    }
}
