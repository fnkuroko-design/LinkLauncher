using System;
using LinkLauncher.Services;

var checks = 0;
Check("解放が先でもcallbackを一度だけ返す", () =>
{
    var state = new MouseActivationCompletion();
    long ticket = state.Begin();
    int calls = 0;
    Action callback = () => calls++;

    Require(state.Release() is null);
    Require(state.IsPending);
    Require(state.IsCurrent(ticket));
    Action? ready = state.Capture(ticket, callback);
    Require(ReferenceEquals(ready, callback));
    Require(!state.IsPending);
    Require(state.IsCurrent(ticket));
    ready?.Invoke();
    Require(calls == 1);
    Require(state.Capture(ticket, callback) is null);
    Require(state.Release() is null);
});

Check("callback登録が先なら解放時に一度だけ返す", () =>
{
    var state = new MouseActivationCompletion();
    long ticket = state.Begin();
    int calls = 0;
    Action callback = () => calls++;

    Require(state.Capture(ticket, callback) is null);
    Require(state.IsPending);
    Action? ready = state.Release();
    Require(ReferenceEquals(ready, callback));
    Require(!state.IsPending);
    Require(state.Release() is null);
    ready?.Invoke();
    Require(calls == 1);
});

Check("次の要求とCancelで旧ticketとcallbackを破棄する", () =>
{
    var state = new MouseActivationCompletion();
    long oldTicket = state.Begin();
    Action oldCallback = () => throw new InvalidOperationException("旧callbackが残っています。");
    Require(state.Capture(oldTicket, oldCallback) is null);

    long currentTicket = state.Begin();
    Require(!state.IsCurrent(oldTicket));
    Require(state.IsCurrent(currentTicket));
    Require(state.Release() is null);
    Require(state.Capture(oldTicket, oldCallback) is null);
    Require(state.IsPending);

    Action currentCallback = () => { };
    Require(ReferenceEquals(state.Capture(currentTicket, currentCallback), currentCallback));

    long cancelledTicket = state.Begin();
    Action cancelledCallback = () => throw new InvalidOperationException("キャンセル済みcallbackが残っています。");
    Require(state.Capture(cancelledTicket, cancelledCallback) is null);
    state.Cancel();
    Require(!state.IsCurrent(cancelledTicket));
    Require(!state.IsPending);
    Require(state.Release() is null);
    Require(state.Capture(cancelledTicket, cancelledCallback) is null);
});

Check("null Captureと二重Releaseはcallbackを返さない", () =>
{
    var state = new MouseActivationCompletion();
    long cancelledTicket = state.Begin();
    Require(state.Capture(cancelledTicket, null) is null);
    Require(!state.IsCurrent(cancelledTicket));
    Require(!state.IsPending);
    Require(state.Release() is null);

    long ticket = state.Begin();
    Require(state.Release() is null);
    Require(state.Release() is null);
    Require(state.IsPending);
    Action callback = () => { };
    Require(ReferenceEquals(state.Capture(ticket, callback), callback));
    Require(state.Release() is null);
});

Console.WriteLine($"PASS {checks}/4");
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
        throw new InvalidOperationException("完了状態の期待結果と一致しません。");
    }
}
