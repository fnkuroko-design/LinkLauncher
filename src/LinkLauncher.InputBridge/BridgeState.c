#include "BridgeState.h"

void BridgeMenuGuardClear(BRIDGE_MENU_GUARD *guard)
{
    if (guard != 0) guard->valid = 0;
}

void BridgeMenuGuardArm(BRIDGE_MENU_GUARD *guard, BRIDGE_U64 root,
    BRIDGE_U32 pid, BRIDGE_U32 tid, BRIDGE_I32 ownerShown, BRIDGE_U32 tick)
{
    if (guard == 0) return;
    guard->valid = root != 0 && pid != 0 && tid != 0;
    guard->targetRoot = root;
    guard->targetPid = (BRIDGE_I32)pid;
    guard->targetTid = (BRIDGE_I32)tid;
    guard->ownerShown = ownerShown;
    guard->startTick = tick;
    guard->reserved = 0;
}

BRIDGE_I32 BridgeMenuGuardMatches(const BRIDGE_MENU_GUARD *guard,
    BRIDGE_U64 root, BRIDGE_U32 pid, BRIDGE_U32 tid,
    BRIDGE_I32 ownerVisible, BRIDGE_U32 tick)
{
    if (guard == 0 || !guard->valid || guard->targetRoot != root ||
        (BRIDGE_U32)guard->targetPid != pid || (BRIDGE_U32)guard->targetTid != tid)
        return 0;
    /* A gesture that hides the launcher needs only a bounded cancellation.
     * A shown launcher keeps its source guard until closed or new input. */
    return guard->ownerShown ? ownerVisible :
        (BRIDGE_U32)(tick - guard->startTick) < BRIDGE_WATCHDOG_MS;
}

static BRIDGE_I32 BridgeWithinAxis(BRIDGE_I32 a, BRIDGE_I32 b)
{
    if (a >= b)
    {
        return (((BRIDGE_U32)a - (BRIDGE_U32)b) <= (BRIDGE_U32)BRIDGE_MOVE_LIMIT);
    }
    return (((BRIDGE_U32)b - (BRIDGE_U32)a) <= (BRIDGE_U32)BRIDGE_MOVE_LIMIT);
}

static BRIDGE_I32 BridgeWithinPoint(BRIDGE_I32 x1, BRIDGE_I32 y1, BRIDGE_I32 x2, BRIDGE_I32 y2)
{
    BRIDGE_U32 dx;
    BRIDGE_U32 dy;
    if (!BridgeWithinAxis(x1, x2) || !BridgeWithinAxis(y1, y2)) return 0;
    dx = x1 >= x2 ? (BRIDGE_U32)x1 - (BRIDGE_U32)x2 : (BRIDGE_U32)x2 - (BRIDGE_U32)x1;
    dy = y1 >= y2 ? (BRIDGE_U32)y1 - (BRIDGE_U32)y2 : (BRIDGE_U32)y2 - (BRIDGE_U32)y1;
    return dx * dx + dy * dy <= 25UL;
}

static void BridgeClearSession(BRIDGE_STATE *state)
{
    state->phase = BRIDGE_PHASE_IDLE;
    state->pendingButtons = 0;
    state->requestSequence = 0;
    state->requestValid = 0;
    state->requestClaimed = 0;
    state->pressX = 0;
    state->pressY = 0;
    state->targetPid = 0;
    state->targetTid = 0;
    state->rightDownSeen = 0;
    state->startTick = 0;
    state->targetRoot = 0;
}

void BridgeStateInit(BRIDGE_STATE *state)
{
    if (state == 0) return;
    state->sequence = 0;
    BridgeClearSession(state);
    state->reserved = 0;
}

void BridgeStateCancel(BRIDGE_STATE *state)
{
    if (state == 0) return;
    BridgeClearSession(state);
}

BRIDGE_I32 BridgeStateOnRightDown(
    BRIDGE_STATE *state,
    BRIDGE_U64 root,
    BRIDGE_U32 pid,
    BRIDGE_U32 tid,
    BRIDGE_I32 x,
    BRIDGE_I32 y,
    BRIDGE_U32 tick,
    BRIDGE_I32 *supersededSequence)
{
    if (supersededSequence != 0) *supersededSequence = 0;
    if (state == 0 || root == 0 || pid == 0) return BRIDGE_DECISION_PASS;

    if (state->phase == BRIDGE_PHASE_HANDSHAKE)
    {
        /* Do not race the synchronous owner acknowledgement. */
        return BRIDGE_DECISION_PASS;
    }
    if (state->phase == BRIDGE_PHASE_ACTIVE)
    {
        if (state->targetRoot == root && (BRIDGE_U32)state->targetPid == pid)
        {
            /* Keep a fresh same-target click balanced by passing its whole pair. */
            if (state->requestValid && state->pendingButtons != 0 && supersededSequence != 0)
            {
                *supersededSequence = state->requestSequence;
            }
            BridgeClearSession(state);
            return BRIDGE_DECISION_PASS;
        }
        if (state->requestValid && state->pendingButtons != 0 && supersededSequence != 0)
        {
            *supersededSequence = state->requestSequence;
        }
        BridgeClearSession(state);
    }

    state->phase = BRIDGE_PHASE_CANDIDATE;
    state->pressX = x;
    state->pressY = y;
    state->targetPid = (BRIDGE_I32)pid;
    state->targetTid = (BRIDGE_I32)tid;
    state->targetRoot = root;
    state->rightDownSeen = 1;
    state->startTick = tick;
    return BRIDGE_DECISION_PASS;
}

BRIDGE_I32 BridgeStateOnMove(BRIDGE_STATE *state, BRIDGE_I32 x, BRIDGE_I32 y)
{
    if (state == 0 || state->phase != BRIDGE_PHASE_CANDIDATE) return BRIDGE_DECISION_PASS;
    if (!BridgeWithinPoint(state->pressX, state->pressY, x, y))
    {
        BridgeClearSession(state);
    }
    return BRIDGE_DECISION_PASS;
}

BRIDGE_I32 BridgeStateOnLeftDown(
    BRIDGE_STATE *state,
    BRIDGE_U64 root,
    BRIDGE_U32 pid,
    BRIDGE_U32 tid,
    BRIDGE_I32 x,
    BRIDGE_I32 y,
    BRIDGE_I32 queuedRightDown,
    BRIDGE_I32 queuedOtherButtons,
    BRIDGE_U32 tick)
{
    BRIDGE_U32 nextSequence;
    if (state == 0 || state->phase != BRIDGE_PHASE_CANDIDATE) return BRIDGE_DECISION_PASS;

    if (!state->rightDownSeen || !queuedRightDown || queuedOtherButtons || state->targetRoot != root ||
        (BRIDGE_U32)state->targetPid != pid || (BRIDGE_U32)state->targetTid != tid ||
        !BridgeWithinPoint(state->pressX, state->pressY, x, y))
    {
        BridgeClearSession(state);
        return BRIDGE_DECISION_PASS;
    }

    nextSequence = (BRIDGE_U32)state->sequence + 1UL;
    if (nextSequence == 0) nextSequence = 1UL;
    state->sequence = (BRIDGE_I32)nextSequence;
    state->phase = BRIDGE_PHASE_HANDSHAKE;
    state->pendingButtons = (BRIDGE_I32)BRIDGE_BUTTON_BOTH;
    state->requestSequence = (BRIDGE_I32)nextSequence;
    state->requestValid = 1;
    state->requestClaimed = 0;
    state->startTick = tick;
    return BRIDGE_DECISION_BEGIN;
}

BRIDGE_I32 BridgeStateOnUp(
    BRIDGE_STATE *state,
    BRIDGE_I32 button,
    BRIDGE_I32 targetMatches,
    BRIDGE_U32 tick,
    BRIDGE_I32 *completedSequence)
{
    BRIDGE_I32 pending;
    (void)tick;
    if (completedSequence != 0) *completedSequence = 0;
    if (state == 0) return BRIDGE_DECISION_PASS;

    if (state->phase == BRIDGE_PHASE_CANDIDATE &&
        button == (BRIDGE_I32)BRIDGE_BUTTON_RIGHT)
    {
        BridgeClearSession(state);
        return BRIDGE_DECISION_PASS;
    }
    if (state->phase != BRIDGE_PHASE_HANDSHAKE && state->phase != BRIDGE_PHASE_ACTIVE)
    {
        return BRIDGE_DECISION_PASS;
    }
    if ((state->pendingButtons & button) == 0) return BRIDGE_DECISION_PASS;

    state->pendingButtons &= ~button;
    if (state->phase == BRIDGE_PHASE_HANDSHAKE)
    {
        /* Before UI acceptance, UP is observed for ordering but always passes. */
        return BRIDGE_DECISION_PASS;
    }

    if (!targetMatches)
    {
        /* A redirected release closes the session but is never swallowed. */
        if (state->pendingButtons == 0)
        {
            if (completedSequence != 0) *completedSequence = state->requestSequence;
            BridgeClearSession(state);
        }
        return BRIDGE_DECISION_PASS;
    }

    pending = state->pendingButtons;
    if (pending == 0)
    {
        if (completedSequence != 0) *completedSequence = state->requestSequence;
        BridgeClearSession(state);
        return BRIDGE_DECISION_COMPLETE;
    }
    return BRIDGE_DECISION_CONSUME;
}

BRIDGE_I32 BridgeStateAccept(
    BRIDGE_STATE *state,
    BRIDGE_I32 sequence,
    BRIDGE_U32 tick,
    BRIDGE_I32 *completedSequence)
{
    if (completedSequence != 0) *completedSequence = 0;
    if (state == 0 || state->phase != BRIDGE_PHASE_HANDSHAKE ||
        !state->requestValid || state->requestSequence != sequence)
    {
        return BRIDGE_DECISION_PASS;
    }
    if (state->pendingButtons == 0)
    {
        if (completedSequence != 0) *completedSequence = state->requestSequence;
        BridgeClearSession(state);
        return BRIDGE_DECISION_COMPLETE;
    }
    state->phase = BRIDGE_PHASE_ACTIVE;
    state->startTick = tick;
    return BRIDGE_DECISION_BEGIN;
}

BRIDGE_I32 BridgeStateReject(BRIDGE_STATE *state, BRIDGE_I32 sequence)
{
    if (state == 0 || state->requestSequence != sequence || !state->requestValid)
    {
        return 0;
    }
    BridgeClearSession(state);
    return 1;
}

BRIDGE_I32 BridgeStateExpire(
    BRIDGE_STATE *state,
    BRIDGE_U32 tick,
    BRIDGE_I32 *expiredSequence)
{
    if (expiredSequence != 0) *expiredSequence = 0;
    if (state == 0 || state->phase != BRIDGE_PHASE_ACTIVE ||
        (BRIDGE_U32)(tick - state->startTick) < BRIDGE_WATCHDOG_MS)
    {
        return 0;
    }
    if (expiredSequence != 0) *expiredSequence = state->requestSequence;
    BridgeClearSession(state);
    return 1;
}

BRIDGE_I32 BridgeStateTakeRequest(BRIDGE_STATE *state, BRIDGE_I32 sequence)
{
    if (state == 0 || !state->requestValid || state->requestClaimed ||
        state->requestSequence != sequence)
    {
        return 0;
    }
    state->requestClaimed = 1;
    return 1;
}

BRIDGE_I32 BridgeStateIsChordTarget(
    const BRIDGE_STATE *state,
    BRIDGE_U64 root,
    BRIDGE_U32 pid,
    BRIDGE_U32 ownerPid,
    BRIDGE_U64 ownerRoot)
{
    if (state == 0) return 0;
    if (state->phase == BRIDGE_PHASE_CANDIDATE &&
        root == state->targetRoot && (BRIDGE_U32)state->targetPid == pid) return 1;
    if (state->phase != BRIDGE_PHASE_HANDSHAKE && state->phase != BRIDGE_PHASE_ACTIVE) return 0;
    if (root == state->targetRoot && (BRIDGE_U32)state->targetPid == pid) return 1;
    if (ownerPid != 0 && pid == ownerPid && root == ownerRoot) return 1;
    return 0;
}
