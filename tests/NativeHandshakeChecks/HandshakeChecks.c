#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include "BridgeState.h"
#include "BridgeRequest.h"

/* No hook installation, physical mouse state, input injection, foreground/capture changes, or visible windows. */
#define CHECK_MESSAGE (WM_APP + 0x51)
static CRITICAL_SECTION g_gate;
static BRIDGE_STATE g_state;
static BRIDGE_MENU_GUARD g_menuGuard;
static HANDLE g_ready;
static HANDLE g_finished;
static HWND g_receiver;
static LONG g_displayCount;
static LONG g_claimed;
static DWORD g_delay;
static int g_fixed;
static int g_beforeCommit;
static int g_replyFailure;
static int g_earlyUps;
static int g_show;
__declspec(align(8)) static volatile LONG64 g_verdict;

static LRESULT CALLBACK Receiver(HWND window, UINT message, WPARAM sequence, LPARAM unused)
{
    LONG claimed;
    (void)unused;
    if (message != CHECK_MESSAGE) return DefWindowProcW(window, message, sequence, unused);
    EnterCriticalSection(&g_gate);
    claimed = BridgeStateTakeRequest(&g_state, (LONG)sequence);
    LeaveCriticalSection(&g_gate);
    InterlockedExchange(&g_claimed, claimed);
    if (claimed)
    {
        if (g_beforeCommit) Sleep(g_delay);
        if (!g_fixed || BridgeRequestCommit(&g_verdict, (DWORD)sequence, g_show))
        {
            if (g_earlyUps)
            {
                EnterCriticalSection(&g_gate);
                BridgeStateOnUp(&g_state, BRIDGE_BUTTON_LEFT, 1, GetTickCount(), NULL);
                BridgeStateOnUp(&g_state, BRIDGE_BUTTON_RIGHT, 1, GetTickCount(), NULL);
                LeaveCriticalSection(&g_gate);
            }
            /* Stand-in for synchronous Show/Hide/Activate; never changes any real window. */
            if (!g_beforeCommit) Sleep(g_delay);
            InterlockedIncrement(&g_displayCount);
        }
    }
    SetEvent(g_finished);
    return claimed && !g_replyFailure ? 1 : 0;
}

static DWORD WINAPI ReceiverThread(void *unused)
{
    WNDCLASSW kind = { 0 };
    MSG message;
    (void)unused;
    kind.hInstance = GetModuleHandleW(NULL);
    kind.lpfnWndProc = Receiver;
    kind.lpszClassName = L"LinkLauncher.NoInput.HandshakeCheck";
    if (!RegisterClassW(&kind)) { SetEvent(g_ready); return 1; }
    g_receiver = CreateWindowExW(0, kind.lpszClassName, L"", 0, 0, 0, 0, 0,
        HWND_MESSAGE, NULL, kind.hInstance, NULL);
    SetEvent(g_ready);
    if (g_receiver == NULL) return 1;
    while (GetMessageW(&message, NULL, 0, 0) > 0) DispatchMessageW(&message);
    DestroyWindow(g_receiver);
    UnregisterClassW(kind.lpszClassName, kind.hInstance);
    return 0;
}

static int RunCase(const char *name, DWORD delay, int fixed, int beforeCommit,
    int replyFailure, int earlyUps, int show, int expectAccepted, int expectDisplay)
{
    BRIDGE_I32 completed = 0;
    DWORD_PTR result = 0;
    DWORD_PTR delivered;
    DWORD start;
    DWORD elapsed;
    LONG accepted;
    LONG displayed;
    EnterCriticalSection(&g_gate);
    BridgeStateInit(&g_state);
    BridgeMenuGuardClear(&g_menuGuard);
    BridgeStateOnRightDown(&g_state, 1, 2, 3, 0, 0, GetTickCount(), NULL);
    BridgeStateOnLeftDown(&g_state, 1, 2, 3, 0, 0, 1, 0, GetTickCount());
    g_delay = delay;
    g_fixed = fixed; g_beforeCommit = beforeCommit; g_replyFailure = replyFailure;
    g_earlyUps = earlyUps; g_show = show;
    BridgeRequestStart(&g_verdict, 1);
    InterlockedExchange(&g_displayCount, 0);
    InterlockedExchange(&g_claimed, 0);
    ResetEvent(g_finished);
    LeaveCriticalSection(&g_gate);
    start = GetTickCount();
    delivered = SendMessageTimeoutW(g_receiver, CHECK_MESSAGE, 1, 0, SMTO_ABORTIFHUNG, 75, &result);
    elapsed = GetTickCount() - start;
    EnterCriticalSection(&g_gate);
    accepted = fixed ? BridgeRequestResolve(&g_verdict, 1) != 0 : delivered != 0 && result == 1;
    if (fixed)
    {
        completed = BridgeRequestFinalize(&g_verdict, &g_state, &g_menuGuard, GetTickCount());
    }
    else if (accepted)
    {
        LONG decision = BridgeStateAccept(&g_state, 1, GetTickCount(), &completed);
        accepted = decision == BRIDGE_DECISION_BEGIN || decision == BRIDGE_DECISION_COMPLETE;
    }
    if (!fixed && !accepted) BridgeStateReject(&g_state, 1);
    LeaveCriticalSection(&g_gate);
    if (WaitForSingleObject(g_finished, 5000) != WAIT_OBJECT_0) return 0;
    displayed = InterlockedCompareExchange(&g_displayCount, 0, 0);
    printf("%s: elapsed_ms=%lu send_success=%d ui_started=%ld display_count=%ld source_decision=%s\n",
        name, elapsed, delivered != 0, InterlockedCompareExchange(&g_claimed, 0, 0), displayed, accepted ? "consume" : "pass");
    if (InterlockedCompareExchange(&g_claimed, 0, 0) != 1) return 0;
    if (earlyUps && g_state.pendingButtons != 0) return 0;
    return displayed == expectDisplay && accepted == expectAccepted;
}

static int CheckTokens(void)
{
    BridgeRequestStart(&g_verdict, 100);
    if (BridgeRequestResolve(&g_verdict, 100) != 0 || BridgeRequestCommit(&g_verdict, 100, TRUE)) return 0;
    BridgeRequestStart(&g_verdict, 101);
    if (BridgeRequestCommit(&g_verdict, 100, TRUE) || BridgeRequestResolve(&g_verdict, 100) != 0) return 0;
    if (!BridgeRequestCommit(&g_verdict, 101, FALSE) || BridgeRequestCommit(&g_verdict, 101, TRUE)) return 0;
    if (BridgeRequestResolve(&g_verdict, 101) != 2 || BridgeRequestDecision(&g_verdict, 101) != 2) return 0;
    BridgeRequestStart(&g_verdict, 0xFFFFFFFFUL);
    if (!BridgeRequestCommit(&g_verdict, 0xFFFFFFFFUL, TRUE) || BridgeRequestResolve(&g_verdict, 0xFFFFFFFFUL) != 1) return 0;
    puts("PASS cancelled, duplicate, stale and wrapped request tokens");
    return 1;
}

static void PrepareDeferred(BRIDGE_STATE *state, BRIDGE_MENU_GUARD *guard, BOOL accepted)
{
    BridgeStateInit(state);
    BridgeMenuGuardClear(guard);
    BridgeStateOnRightDown(state, 11, 12, 13, 0, 0, 100, NULL);
    BridgeStateOnLeftDown(state, 11, 12, 13, 0, 0, 1, 0, 101);
    BridgeStateTakeRequest(state, 1);
    BridgeRequestStart(&g_verdict, 1);
    if (accepted) BridgeRequestCommit(&g_verdict, 1, TRUE);
    BridgeRequestResolve(&g_verdict, 1);
    /* The sender could not get its state lock. Leave HANDSHAKE untouched until
     * the next event obtains the lock and calls the production finalizer. */
}

static int CheckDeferredRearm(void)
{
    BRIDGE_STATE state;
    BRIDGE_MENU_GUARD guard;
    LONG superseded = 0;
    PrepareDeferred(&state, &guard, TRUE);
    if (BridgeRequestFinalize(&g_verdict, &state, &guard, 102) != 0 || state.phase != BRIDGE_PHASE_ACTIVE) return 0;
    BridgeStateOnRightDown(&state, 11, 12, 13, 0, 0, 103, &superseded);
    if (superseded != 1 || state.phase != BRIDGE_PHASE_CANDIDATE) return 0;
    if (BridgeStateOnLeftDown(&state, 11, 12, 13, 0, 0, 1, 0, 104) != BRIDGE_DECISION_BEGIN) return 0;
    if (state.requestSequence != 2) return 0;
    BridgeRequestStart(&g_verdict, 2);
    if (!BridgeRequestCommit(&g_verdict, 2, TRUE) || BridgeRequestResolve(&g_verdict, 2) != 1) return 0;
    puts("PASS deferred acceptance finalized before next right-down rearm");
    return 1;
}

static int CheckDeferredUp(void)
{
    BRIDGE_STATE state;
    BRIDGE_MENU_GUARD guard;
    LONG completed = 0;
    PrepareDeferred(&state, &guard, TRUE);
    BridgeRequestFinalize(&g_verdict, &state, &guard, 102);
    if (BridgeStateOnUp(&state, BRIDGE_BUTTON_LEFT, 1, 103, &completed) != BRIDGE_DECISION_CONSUME || completed != 0) return 0;
    if (BridgeStateOnUp(&state, BRIDGE_BUTTON_RIGHT, 1, 104, &completed) != BRIDGE_DECISION_COMPLETE || completed != 1) return 0;
    if (state.phase != BRIDGE_PHASE_IDLE || state.pendingButtons != 0) return 0;
    puts("PASS deferred acceptance finalized before matching releases");
    return 1;
}

static int CheckDeferredCancel(void)
{
    BRIDGE_STATE state;
    BRIDGE_MENU_GUARD guard;
    PrepareDeferred(&state, &guard, FALSE);
    BridgeRequestFinalize(&g_verdict, &state, &guard, 102);
    if (state.phase != BRIDGE_PHASE_IDLE || guard.valid) return 0;
    BridgeStateOnRightDown(&state, 11, 12, 13, 0, 0, 103, NULL);
    if (BridgeStateOnLeftDown(&state, 11, 12, 13, 0, 0, 1, 0, 104) != BRIDGE_DECISION_BEGIN) return 0;
    puts("PASS deferred cancellation finalized before next chord");
    return 1;
}

static int CheckDeferredEarlyReleases(void)
{
    BRIDGE_STATE state;
    BRIDGE_MENU_GUARD guard;
    PrepareDeferred(&state, &guard, TRUE);
    BridgeStateOnUp(&state, BRIDGE_BUTTON_LEFT, 1, 102, NULL);
    BridgeStateOnUp(&state, BRIDGE_BUTTON_RIGHT, 1, 103, NULL);
    if (BridgeRequestFinalize(&g_verdict, &state, &guard, 104) != 1) return 0;
    if (BridgeRequestFinalize(&g_verdict, &state, &guard, 105) != 0) return 0;
    if (state.phase != BRIDGE_PHASE_IDLE || state.pendingButtons != 0 || !guard.valid || guard.targetRoot != 11) return 0;
    puts("PASS deferred completion emitted once with original menu target");
    return 1;
}

int main(void)
{
    HANDLE worker;
    DWORD threadId = 0;
    int passed = 0;
    InitializeCriticalSection(&g_gate);
    g_ready = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_finished = CreateEventW(NULL, TRUE, FALSE, NULL);
    worker = CreateThread(NULL, 0, ReceiverThread, NULL, 0, &threadId);
    if (worker == NULL || WaitForSingleObject(g_ready, 5000) != WAIT_OBJECT_0 || g_receiver == NULL) return 2;
    passed += RunCase("legacy-fast", 0, 0, 0, 0, 0, 1, 1, 1);
    passed += RunCase("legacy-slow-control", 200, 0, 0, 0, 0, 1, 0, 1);
    passed += RunCase("fixed-fast", 0, 1, 0, 0, 0, 1, 1, 1);
    passed += RunCase("fixed-slow-show", 200, 1, 0, 0, 0, 1, 1, 1);
    passed += RunCase("fixed-slow-hide", 200, 1, 0, 0, 0, 0, 1, 1);
    passed += RunCase("cancel-before-commit", 200, 1, 1, 0, 0, 1, 0, 0);
    passed += RunCase("committed-response-failure", 0, 1, 0, 1, 0, 1, 1, 1);
    passed += RunCase("early-releases-during-slow-show", 200, 1, 0, 0, 1, 1, 1, 1);
    passed += CheckTokens();
    passed += CheckDeferredRearm();
    passed += CheckDeferredUp();
    passed += CheckDeferredCancel();
    passed += CheckDeferredEarlyReleases();
    PostThreadMessageW(threadId, WM_QUIT, 0, 0);
    if (WaitForSingleObject(worker, 5000) != WAIT_OBJECT_0) return 2;
    CloseHandle(worker); CloseHandle(g_ready); CloseHandle(g_finished);
    DeleteCriticalSection(&g_gate);
    printf("PASS %d/13\n", passed);
    return passed == 13 ? 0 : 1;
}
