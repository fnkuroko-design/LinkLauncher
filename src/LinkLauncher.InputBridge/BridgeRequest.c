#include "BridgeRequest.h"

#define REQUEST_PENDING 0UL
#define REQUEST_COMMITTED_SHOW 1UL
#define REQUEST_COMMITTED_HIDE 2UL
#define REQUEST_RESOLVED_SHOW 3UL
#define REQUEST_RESOLVED_HIDE 4UL
#define REQUEST_CANCELLED 5UL

static LONG64 BridgeRequestValue(DWORD sequence, DWORD state)
{
    return (LONG64)(((ULONGLONG)sequence << 32) | state);
}

void BridgeRequestStart(volatile LONG64 *request, DWORD sequence)
{
    if (request != NULL) InterlockedExchange64(request, BridgeRequestValue(sequence, REQUEST_PENDING));
}

BOOL BridgeRequestIsPending(volatile LONG64 *request, DWORD sequence)
{
    return request != NULL && sequence != 0 &&
        InterlockedCompareExchange64(request, 0, 0) == BridgeRequestValue(sequence, REQUEST_PENDING);
}

BOOL BridgeRequestCommit(volatile LONG64 *request, DWORD sequence, BOOL ownerShown)
{
    LONG64 pending;
    if (request == NULL || sequence == 0) return FALSE;
    pending = BridgeRequestValue(sequence, REQUEST_PENDING);
    return InterlockedCompareExchange64(request,
        BridgeRequestValue(sequence, ownerShown ? REQUEST_COMMITTED_SHOW : REQUEST_COMMITTED_HIDE), pending) == pending;
}

LONG BridgeRequestResolve(volatile LONG64 *request, DWORD sequence)
{
    LONG64 observed;
    LONG64 desired;
    DWORD state;
    LONG choice;
    if (request == NULL || sequence == 0) return 0;
    for (;;)
    {
        observed = InterlockedCompareExchange64(request, 0, 0);
        if ((DWORD)((ULONGLONG)observed >> 32) != sequence) return 0;
        state = (DWORD)observed;
        if (state == REQUEST_RESOLVED_SHOW) return 1;
        if (state == REQUEST_RESOLVED_HIDE) return 2;
        if (state == REQUEST_CANCELLED) return 0;
        if (state == REQUEST_PENDING) { desired = BridgeRequestValue(sequence, REQUEST_CANCELLED); choice = 0; }
        else if (state == REQUEST_COMMITTED_SHOW) { desired = BridgeRequestValue(sequence, REQUEST_RESOLVED_SHOW); choice = 1; }
        else if (state == REQUEST_COMMITTED_HIDE) { desired = BridgeRequestValue(sequence, REQUEST_RESOLVED_HIDE); choice = 2; }
        else return 0;
        if (InterlockedCompareExchange64(request, desired, observed) == observed) return choice;
    }
}

LONG BridgeRequestDecision(volatile LONG64 *request, DWORD sequence)
{
    LONG64 observed;
    DWORD state;
    if (request == NULL || sequence == 0) return 0;
    observed = InterlockedCompareExchange64(request, 0, 0);
    if ((DWORD)((ULONGLONG)observed >> 32) != sequence) return 0;
    state = (DWORD)observed;
    return state == REQUEST_RESOLVED_SHOW ? 1 : state == REQUEST_RESOLVED_HIDE ? 2 : state == REQUEST_CANCELLED ? -1 : 0;
}

LONG BridgeRequestFinalize(volatile LONG64 *request, BRIDGE_STATE *state,
    BRIDGE_MENU_GUARD *guard, DWORD tick)
{
    LONG choice;
    LONG completed = 0;
    LONG decision;
    BRIDGE_U64 root;
    DWORD pid;
    DWORD tid;
    LONG sequence;
    if (state == NULL || state->phase != BRIDGE_PHASE_HANDSHAKE) return 0;
    sequence = state->requestSequence;
    choice = BridgeRequestDecision(request, (DWORD)sequence);
    if (choice == 0) return 0;
    if (choice < 0) { BridgeStateReject(state, sequence); return 0; }
    root = state->targetRoot;
    pid = (DWORD)state->targetPid;
    tid = (DWORD)state->targetTid;
    decision = BridgeStateAccept(state, sequence, tick, &completed);
    if (decision == BRIDGE_DECISION_BEGIN || decision == BRIDGE_DECISION_COMPLETE)
        BridgeMenuGuardArm(guard, root, pid, tid, choice == 1, tick);
    return completed;
}
