using System;
using LinkLauncher.Models;
using LinkLauncher.Services;

var checks = 0;
Check("短い中押しはクリック再生へ進む", () =>
{
    var state = new MouseActivationState();
    Require(state.TryBegin(MouseActivationButton.Middle, 300, 500));
    Require(state.Release() == MouseActivationReleaseAction.ReplayClick);
    Require(!state.IsPending);
});
Check("中から右の順だけを組合せとして認識", () =>
{
    var state = new MouseActivationState();
    Require(state.TryBegin(MouseActivationButton.Middle, 300, 500));
    Require(state.IsExpectedChord(MouseActivationPattern.MiddleThenRight, MouseActivationButton.Right));
    Require(!state.IsExpectedChord(MouseActivationPattern.MiddleThenRight, MouseActivationButton.Left));
    Require(!state.IsExpectedChord(MouseActivationPattern.MiddleThenRight, MouseActivationButton.XButton2));
    Require(state.Cancel());
    Require(state.Release() == MouseActivationReleaseAction.Pass);
});
Check("右から左の順だけを組合せとして認識", () =>
{
    var state = new MouseActivationState();
    Require(state.TryBegin(MouseActivationButton.Right, 300, 500));
    Require(state.IsExpectedChord(MouseActivationPattern.RightThenLeft, MouseActivationButton.Left));
    Require(!state.IsExpectedChord(MouseActivationPattern.RightThenLeft, MouseActivationButton.Right));
    Require(!state.IsExpectedChord(MouseActivationPattern.RightThenLeft, MouseActivationButton.XButton1));
});
Check("5pxを超える移動で通常ドラッグへ切り替える", () =>
{
    var state = new MouseActivationState();
    Require(state.TryBegin(MouseActivationButton.Right, 300, 500));
    Require(!state.ShouldRestoreOnMove(305, 504));
    Require(state.ShouldRestoreOnMove(306, 500));
    Require(!state.ShouldRestoreOnMove(300, 490));
    Require(state.Release() == MouseActivationReleaseAction.Pass);
});
Check("移動が検出されると二つ目ボタンで起動しない", () =>
{
    var state = new MouseActivationState();
    Require(state.TryBegin(MouseActivationButton.Middle, 300, 500));
    Require(state.ShouldRestoreOnMove(300, 506));
    Require(!state.IsExpectedChord(MouseActivationPattern.MiddleThenRight, MouseActivationButton.Right));
});
Check("左/Xボタンを先行保留対象にしない", () =>
{
    var state = new MouseActivationState();
    Require(!state.TryBegin(MouseActivationButton.Left, 300, 500));
    Require(!state.TryBegin(MouseActivationButton.XButton1, 300, 500));
    Require(!state.TryBegin(MouseActivationButton.XButton2, 300, 500));
    Require(!state.IsPending);
});
Check("設定変更時のCancelでUpのクリック再生を止める", () =>
{
    var state = new MouseActivationState();
    Require(state.TryBegin(MouseActivationButton.Middle, 300, 500));
    Require(state.Cancel());
    Require(!state.IsPending);
    Require(state.Release() == MouseActivationReleaseAction.Pass);
});

Console.WriteLine($"PASS {checks}/7");
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
