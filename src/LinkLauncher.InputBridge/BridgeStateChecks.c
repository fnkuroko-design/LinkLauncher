/*
 * Pure state regression checks; no Win32 calls or synthetic input.
 * From an MSVC developer prompt in this directory:
 *   cl /nologo /W4 /TC /O2 /Fe:BridgeStateChecks.exe BridgeStateChecks.c BridgeState.c
 *   BridgeStateChecks.exe
 */
#include <stdio.h>
#include "BridgeState.h"

#define CHECK_VALUE(label, actual, expected) \
    do { \
        BRIDGE_I32 checkActual = (BRIDGE_I32)(actual); \
        BRIDGE_I32 checkExpected = (BRIDGE_I32)(expected); \
        if (checkActual != checkExpected) { \
            printf("FAIL %s: got %ld, expected %ld\n", (label), \
                (long)checkActual, (long)checkExpected); \
            return 0; \
        } \
    } while (0)

#define CHECK_TRUE(label, expression) CHECK_VALUE((label), (expression) ? 1 : 0, 1)

static int CheckNormalChordLeftUpFirst(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 completed = 0;
    BRIDGE_I32 superseded = 0;
    BridgeStateInit(&state);

    CHECK_VALUE("right down passes", BridgeStateOnRightDown(&state, 0x100, 101, 201,
        100, 100, 10, &superseded), BRIDGE_DECISION_PASS);
    CHECK_VALUE("left down begins", BridgeStateOnLeftDown(&state, 0x100, 101, 201,
        103, 104, 1, 0, 20), BRIDGE_DECISION_BEGIN);
    CHECK_VALUE("candidate accepted", BridgeStateAccept(&state, 1, 25, &completed),
        BRIDGE_DECISION_BEGIN);
    CHECK_VALUE("left up consumed", BridgeStateOnUp(&state, BRIDGE_BUTTON_LEFT, 1,
        30, &completed), BRIDGE_DECISION_CONSUME);
    CHECK_VALUE("right remains pending", state.pendingButtons, BRIDGE_BUTTON_RIGHT);
    CHECK_VALUE("right up completes", BridgeStateOnUp(&state, BRIDGE_BUTTON_RIGHT, 1,
        31, &completed), BRIDGE_DECISION_COMPLETE);
    CHECK_VALUE("completed sequence", completed, 1);
    CHECK_VALUE("session cleared", state.phase, BRIDGE_PHASE_IDLE);
    return 1;
}

static int CheckNormalChordRightUpFirst(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 completed = 0;
    BRIDGE_I32 superseded = 0;
    BridgeStateInit(&state);

    BridgeStateOnRightDown(&state, 0x200, 102, 202, 20, 30, 10, &superseded);
    CHECK_VALUE("left down begins", BridgeStateOnLeftDown(&state, 0x200, 102, 202,
        20, 30, 1, 0, 15), BRIDGE_DECISION_BEGIN);
    CHECK_VALUE("request can be claimed", BridgeStateTakeRequest(&state, 1), 1);
    CHECK_VALUE("request cannot be claimed twice", BridgeStateTakeRequest(&state, 1), 0);
    BridgeStateAccept(&state, 1, 20, &completed);
    CHECK_VALUE("right up after long hold consumed", BridgeStateOnUp(&state, BRIDGE_BUTTON_RIGHT, 1,
        3025, &completed), BRIDGE_DECISION_CONSUME);
    CHECK_VALUE("left up after long hold completes", BridgeStateOnUp(&state, BRIDGE_BUTTON_LEFT, 1,
        4026, &completed), BRIDGE_DECISION_COMPLETE);
    CHECK_VALUE("completed sequence", completed, 1);
    return 1;
}

static int CheckEarlyUpsBeforeAcknowledgement(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 completed = 0;
    BRIDGE_I32 superseded = 0;
    BridgeStateInit(&state);

    BridgeStateOnRightDown(&state, 0x300, 103, 203, 5, 5, 1, &superseded);
    CHECK_VALUE("left down begins", BridgeStateOnLeftDown(&state, 0x300, 103, 203,
        5, 5, 1, 0, 2), BRIDGE_DECISION_BEGIN);
    CHECK_VALUE("early right up passes", BridgeStateOnUp(&state, BRIDGE_BUTTON_RIGHT, 1,
        3, &completed), BRIDGE_DECISION_PASS);
    CHECK_VALUE("early left up passes", BridgeStateOnUp(&state, BRIDGE_BUTTON_LEFT, 1,
        4, &completed), BRIDGE_DECISION_PASS);
    CHECK_VALUE("early ups clear pending", state.pendingButtons, 0);
    CHECK_VALUE("ack completes released chord", BridgeStateAccept(&state, 1, 5,
        &completed), BRIDGE_DECISION_COMPLETE);
    CHECK_VALUE("completed sequence", completed, 1);
    CHECK_VALUE("session cleared", state.phase, BRIDGE_PHASE_IDLE);
    return 1;
}

static int CheckCandidateRejections(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 completed = 0;
    BRIDGE_I32 superseded = 0;

    BridgeStateInit(&state);
    BridgeStateOnRightDown(&state, 0x400, 104, 204, 100, 100, 1, &superseded);
    CHECK_VALUE("right state required", BridgeStateOnLeftDown(&state, 0x400, 104, 204,
        100, 100, 0, 0, 2), BRIDGE_DECISION_PASS);
    CHECK_VALUE("invalid candidate cleared", state.phase, BRIDGE_PHASE_IDLE);

    BridgeStateOnRightDown(&state, 0x400, 104, 204, 100, 100, 3, &superseded);
    CHECK_VALUE("middle or side button blocks", BridgeStateOnLeftDown(&state, 0x400, 104,
        204, 100, 100, 1, 1, 4), BRIDGE_DECISION_PASS);
    CHECK_VALUE("other-button candidate cleared", state.phase, BRIDGE_PHASE_IDLE);

    BridgeStateOnRightDown(&state, 0x400, 104, 204, 100, 100, 5, &superseded);
    CHECK_VALUE("wrong target root rejected", BridgeStateOnLeftDown(&state, 0x401, 104,
        204, 100, 100, 1, 0, 6), BRIDGE_DECISION_PASS);
    CHECK_VALUE("wrong root cleared", state.phase, BRIDGE_PHASE_IDLE);

    BridgeStateOnRightDown(&state, 0x400, 104, 204, 100, 100, 7, &superseded);
    CHECK_VALUE("wrong target thread rejected", BridgeStateOnLeftDown(&state, 0x400, 104,
        205, 100, 100, 1, 0, 8), BRIDGE_DECISION_PASS);
    CHECK_VALUE("wrong thread cleared", state.phase, BRIDGE_PHASE_IDLE);

    BridgeStateOnRightDown(&state, 0x400, 104, 204, 100, 100, 9, &superseded);
    CHECK_VALUE("out-of-radius rejected", BridgeStateOnLeftDown(&state, 0x400, 104, 204,
        104, 104, 1, 0, 10), BRIDGE_DECISION_PASS);
    CHECK_VALUE("out-of-radius cleared", state.phase, BRIDGE_PHASE_IDLE);

    BridgeStateOnRightDown(&state, 0x400, 104, 204, 100, 100, 11, &superseded);
    CHECK_VALUE("right up cancels candidate", BridgeStateOnUp(&state,
        BRIDGE_BUTTON_RIGHT, 1, 12, &completed), BRIDGE_DECISION_PASS);
    CHECK_VALUE("right-up candidate cleared", state.phase, BRIDGE_PHASE_IDLE);
    return 1;
}

static int CheckMovementRadiusAndTargetGuard(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 superseded = 0;
    BridgeStateInit(&state);

    BridgeStateOnRightDown(&state, 0x500, 105, 205, 0, 0, 1, &superseded);
    BridgeStateOnMove(&state, 3, 4);
    CHECK_VALUE("five-pixel move remains candidate", state.phase, BRIDGE_PHASE_CANDIDATE);
    BridgeStateOnLeftDown(&state, 0x500, 105, 205, 3, 4, 1, 0, 2);
    CHECK_VALUE("five-pixel chord accepted", state.phase, BRIDGE_PHASE_HANDSHAKE);

    BridgeStateInit(&state);
    BridgeStateOnRightDown(&state, 0x500, 105, 205, 0, 0, 1, &superseded);
    BridgeStateOnMove(&state, 4, 4);
    CHECK_VALUE("diagonal beyond five pixels cancels", state.phase, BRIDGE_PHASE_IDLE);
    CHECK_VALUE("candidate query only candidate phase",
        state.phase == BRIDGE_PHASE_CANDIDATE, 0);
    return 1;
}

static int CheckRepressAndRedirectedRelease(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 completed = 0;
    BRIDGE_I32 superseded = 0;
    BridgeStateInit(&state);

    BridgeStateOnRightDown(&state, 0x600, 106, 206, 1, 1, 1, &superseded);
    BridgeStateOnLeftDown(&state, 0x600, 106, 206, 1, 1, 1, 0, 2);
    BridgeStateAccept(&state, 1, 3, &completed);
    CHECK_VALUE("active same-target repress passes", BridgeStateOnRightDown(&state,
        0x600, 106, 206, 1, 1, 4, &superseded), BRIDGE_DECISION_PASS);
    CHECK_VALUE("active request superseded", superseded, 1);
    CHECK_VALUE("repress starts next candidate", state.phase, BRIDGE_PHASE_CANDIDATE);
    CHECK_VALUE("new pair up passes", BridgeStateOnUp(&state, BRIDGE_BUTTON_RIGHT, 0,
        5, &completed), BRIDGE_DECISION_PASS);

    BridgeStateOnRightDown(&state, 0x600, 106, 206, 1, 1, 6, &superseded);
    BridgeStateOnLeftDown(&state, 0x600, 106, 206, 1, 1, 1, 0, 7);
    BridgeStateAccept(&state, 2, 8, &completed);
    CHECK_VALUE("redirected right release passes", BridgeStateOnUp(&state,
        BRIDGE_BUTTON_RIGHT, 0, 9, &completed), BRIDGE_DECISION_PASS);
    CHECK_VALUE("redirected release remains visible", state.pendingButtons,
        BRIDGE_BUTTON_LEFT);
    CHECK_VALUE("matching left release completes", BridgeStateOnUp(&state,
        BRIDGE_BUTTON_LEFT, 1, 10, &completed), BRIDGE_DECISION_COMPLETE);
    CHECK_VALUE("redirected session completion", completed, 2);
    return 1;
}

static int CheckRepeatedCallsWithUndeliveredWindowUps(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 completed = 0;
    BRIDGE_I32 superseded = 0;
    BRIDGE_I32 index;
    BridgeStateInit(&state);
    /* Simulate menu cancellation and pointer-leave dismissal without
     * window-addressed UPs; every new RIGHTDOWN must remain usable. */
    for (index = 0; index < 4; ++index)
    {
        BRIDGE_U32 tick = (BRIDGE_U32)index * 100UL;
        CHECK_VALUE("repeat right down passes", BridgeStateOnRightDown(&state,
            0x600, 106, 206, 10, 10, tick, &superseded), BRIDGE_DECISION_PASS);
        CHECK_VALUE("repeat retires only previous request", superseded, index);
        CHECK_VALUE("every repeat has a candidate", state.phase, BRIDGE_PHASE_CANDIDATE);
        CHECK_VALUE("every repeat starts a gesture", BridgeStateOnLeftDown(&state,
            0x600, 106, 206, 10, 10, 1, 0, tick + 1), BRIDGE_DECISION_BEGIN);
        CHECK_VALUE("repeat sequence advances", state.requestSequence, index + 1);
        CHECK_VALUE("repeat request claimed once", BridgeStateTakeRequest(&state, index + 1), 1);
        CHECK_VALUE("repeat accepted", BridgeStateAccept(&state, index + 1,
            tick + 2, &completed), BRIDGE_DECISION_BEGIN);
    }
    BridgeStateOnRightDown(&state, 0x600, 106, 206, 10, 10, 500, &superseded);
    CHECK_VALUE("next lone right release passes", BridgeStateOnUp(&state,
        BRIDGE_BUTTON_RIGHT, 1, 501, &completed), BRIDGE_DECISION_PASS);
    CHECK_VALUE("lone right click leaves no pending buttons", state.pendingButtons, 0);
    BridgeStateOnRightDown(&state, 0x600, 106, 206, 10, 10, 600, &superseded);
    BridgeStateOnLeftDown(&state, 0x600, 106, 206, 10, 10, 1, 0, 601);
    BridgeStateTakeRequest(&state, 5);
    BridgeStateAccept(&state, 5, 602, &completed);
    BridgeStateOnRightDown(&state, 0x600, 106, 206, 20, 20, 700, &superseded);
    CHECK_VALUE("next drag move passes", BridgeStateOnMove(&state, 26, 20), BRIDGE_DECISION_PASS);
    CHECK_VALUE("next drag releases candidate", state.phase, BRIDGE_PHASE_IDLE);
    CHECK_VALUE("next drag right release passes", BridgeStateOnUp(&state,
        BRIDGE_BUTTON_RIGHT, 1, 701, &completed), BRIDGE_DECISION_PASS);
    return 1;
}

static int CheckWatchdogAndSequenceWrap(void)
{
    BRIDGE_STATE state;
    BRIDGE_I32 completed = 0;
    BRIDGE_I32 expired = 0;
    BRIDGE_I32 superseded = 0;
    BridgeStateInit(&state);
    state.sequence = (BRIDGE_I32)-1;
    BridgeStateOnRightDown(&state, 0x700, 107, 207, 8, 9, 0xFFFFFFF0UL, &superseded);
    CHECK_VALUE("sequence wraps to nonzero", BridgeStateOnLeftDown(&state, 0x700, 107,
        207, 8, 9, 1, 0, 0xFFFFFFF1UL), BRIDGE_DECISION_BEGIN);
    CHECK_VALUE("wrapped sequence is one", state.sequence, 1);
    BridgeStateAccept(&state, 1, 0xFFFFFFF2UL, &completed);
    CHECK_VALUE("watchdog does not expire early", BridgeStateExpire(&state, 0x000007C1UL,
        &expired), 0);
    CHECK_VALUE("watchdog expires after two seconds", BridgeStateExpire(&state, 0x000007C2UL,
        &expired), 1);
    CHECK_VALUE("expired sequence retained", expired, 1);
    CHECK_VALUE("expired session cleared", state.phase, BRIDGE_PHASE_IDLE);
    return 1;
}

static int CheckMenuAfterButtonRelease(void)
{
    BRIDGE_STATE state;
    BRIDGE_MENU_GUARD guard = { 0 };
    BRIDGE_I32 superseded = 0;
    BRIDGE_I32 completed = 0;
    BridgeStateInit(&state);
    BridgeStateOnRightDown(&state, 0x800, 108, 208, 10, 10, 0, &superseded);
    BridgeStateOnLeftDown(&state, 0x800, 108, 208, 10, 10, 1, 0, 1);
    BridgeStateTakeRequest(&state, 1);
    BridgeStateAccept(&state, 1, 2, &completed);
    BridgeMenuGuardArm(&guard, 0x800, 108, 208, 1, 2);
    BridgeStateOnUp(&state, BRIDGE_BUTTON_LEFT, 1, 3, &completed);
    BridgeStateOnUp(&state, BRIDGE_BUTTON_RIGHT, 1, 4, &completed);
    CHECK_VALUE("physical session ended independently", state.pendingButtons, 0);
    CHECK_VALUE("delayed source menu still guarded", BridgeMenuGuardMatches(&guard,
        0x800, 108, 208, 1, 10000), 1);
    BridgeMenuGuardClear(&guard);
    CHECK_VALUE("next user action restores source menus", BridgeMenuGuardMatches(&guard,
        0x800, 108, 208, 1, 10001), 0);
    return 1;
}

static int CheckMenuSourceAndVisibilityBoundary(void)
{
    BRIDGE_MENU_GUARD guard = { 0 };
    BridgeMenuGuardArm(&guard, 0x900, 109, 209, 1, 0);
    CHECK_VALUE("other source window unaffected", BridgeMenuGuardMatches(&guard,
        0x901, 109, 209, 1, 1), 0);
    CHECK_VALUE("other process unaffected", BridgeMenuGuardMatches(&guard,
        0x900, 110, 209, 1, 1), 0);
    CHECK_VALUE("other thread unaffected", BridgeMenuGuardMatches(&guard,
        0x900, 109, 210, 1, 1), 0);
    CHECK_VALUE("hidden launcher no longer guards", BridgeMenuGuardMatches(&guard,
        0x900, 109, 209, 0, 1), 0);
    BridgeMenuGuardClear(&guard);
    CHECK_VALUE("reopening does not restore cleared guard", BridgeMenuGuardMatches(&guard,
        0x900, 109, 209, 1, 2), 0);
    return 1;
}

static int CheckMenuHideGestureAndRearm(void)
{
    BRIDGE_MENU_GUARD guard = { 0 };
    BridgeMenuGuardArm(&guard, 0xA00, 110, 210, 0, 0xFFFFFFF0UL);
    CHECK_VALUE("hide gesture cancels immediate menu across clock wrap", BridgeMenuGuardMatches(&guard,
        0xA00, 110, 210, 0, 0x10UL), 1);
    CHECK_VALUE("hide gesture cancellation expires", BridgeMenuGuardMatches(&guard,
        0xA00, 110, 210, 0, 0x7C0UL), 0);
    BridgeMenuGuardArm(&guard, 0xB00, 111, 211, 1, 3000);
    CHECK_VALUE("old gesture source no longer guarded", BridgeMenuGuardMatches(&guard,
        0xA00, 110, 210, 1, 3001), 0);
    CHECK_VALUE("new accepted source guarded", BridgeMenuGuardMatches(&guard,
        0xB00, 111, 211, 1, 3001), 1);
    BridgeMenuGuardArm(&guard, 0, 111, 211, 1, 3002);
    CHECK_VALUE("invalid source cannot arm cancellation", guard.valid, 0);
    return 1;
}

int main(void)
{
    int passed = 0;
    int total = 11;
    passed += CheckNormalChordLeftUpFirst();
    passed += CheckNormalChordRightUpFirst();
    passed += CheckEarlyUpsBeforeAcknowledgement();
    passed += CheckCandidateRejections();
    passed += CheckMovementRadiusAndTargetGuard();
    passed += CheckRepressAndRedirectedRelease();
    passed += CheckWatchdogAndSequenceWrap();
    passed += CheckMenuAfterButtonRelease();
    passed += CheckMenuSourceAndVisibilityBoundary();
    passed += CheckMenuHideGestureAndRearm();
    passed += CheckRepeatedCallsWithUndeliveredWindowUps();
    printf("Bridge state checks: %d/%d passed\n", passed, total);
    return passed == total ? 0 : 1;
}
