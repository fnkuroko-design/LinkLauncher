#ifndef LINKLAUNCHER_BRIDGE_STATE_H
#define LINKLAUNCHER_BRIDGE_STATE_H

/* Pure, fixed-width state shared by the Windows hook and its CLI checks. */
typedef unsigned long BRIDGE_U32;
typedef unsigned long long BRIDGE_U64;
typedef long BRIDGE_I32;

#define BRIDGE_BUTTON_RIGHT 0x00000001UL
#define BRIDGE_BUTTON_LEFT  0x00000002UL
#define BRIDGE_BUTTON_BOTH  (BRIDGE_BUTTON_RIGHT | BRIDGE_BUTTON_LEFT)
#define BRIDGE_MOVE_LIMIT   5
#define BRIDGE_WATCHDOG_MS  2000UL

enum BRIDGE_PHASE
{
    BRIDGE_PHASE_IDLE = 0,
    BRIDGE_PHASE_CANDIDATE = 1,
    BRIDGE_PHASE_HANDSHAKE = 2,
    BRIDGE_PHASE_ACTIVE = 3
};

enum BRIDGE_DECISION
{
    BRIDGE_DECISION_PASS = 0,
    BRIDGE_DECISION_BEGIN = 1,
    BRIDGE_DECISION_CONSUME = 2,
    BRIDGE_DECISION_COMPLETE = 3,
    BRIDGE_DECISION_EXPIRE = 4
};

typedef struct BRIDGE_STATE
{
    BRIDGE_I32 phase;
    BRIDGE_I32 sequence;
    BRIDGE_I32 pendingButtons;
    BRIDGE_I32 requestSequence;
    BRIDGE_I32 requestValid;
    BRIDGE_I32 requestClaimed;
    BRIDGE_I32 pressX;
    BRIDGE_I32 pressY;
    BRIDGE_I32 targetPid;
    BRIDGE_I32 targetTid;
    BRIDGE_I32 rightDownSeen;
    BRIDGE_I32 reserved;
    BRIDGE_U32 startTick;
    BRIDGE_U64 targetRoot;
} BRIDGE_STATE;

/* Menu cancellation outlives window-addressed button UP bookkeeping.
 * It never owns or suppresses physical input; the next user action clears it. */
typedef struct BRIDGE_MENU_GUARD
{
    BRIDGE_I32 valid;
    BRIDGE_I32 targetPid;
    BRIDGE_I32 targetTid;
    BRIDGE_I32 ownerShown;
    BRIDGE_U64 targetRoot;
    BRIDGE_U32 startTick;
    BRIDGE_I32 reserved;
} BRIDGE_MENU_GUARD;

void BridgeMenuGuardClear(BRIDGE_MENU_GUARD *guard);
void BridgeMenuGuardArm(BRIDGE_MENU_GUARD *guard, BRIDGE_U64 root,
    BRIDGE_U32 pid, BRIDGE_U32 tid, BRIDGE_I32 ownerShown, BRIDGE_U32 tick);
BRIDGE_I32 BridgeMenuGuardMatches(const BRIDGE_MENU_GUARD *guard,
    BRIDGE_U64 root, BRIDGE_U32 pid, BRIDGE_U32 tid,
    BRIDGE_I32 ownerVisible, BRIDGE_U32 tick);

void BridgeStateInit(BRIDGE_STATE *state);
void BridgeStateCancel(BRIDGE_STATE *state);
BRIDGE_I32 BridgeStateOnRightDown(
    BRIDGE_STATE *state,
    BRIDGE_U64 root,
    BRIDGE_U32 pid,
    BRIDGE_U32 tid,
    BRIDGE_I32 x,
    BRIDGE_I32 y,
    BRIDGE_U32 tick,
    BRIDGE_I32 *supersededSequence);
BRIDGE_I32 BridgeStateOnMove(
    BRIDGE_STATE *state,
    BRIDGE_I32 x,
    BRIDGE_I32 y);
BRIDGE_I32 BridgeStateOnLeftDown(
    BRIDGE_STATE *state,
    BRIDGE_U64 root,
    BRIDGE_U32 pid,
    BRIDGE_U32 tid,
    BRIDGE_I32 x,
    BRIDGE_I32 y,
    BRIDGE_I32 queuedRightDown,
    BRIDGE_I32 queuedOtherButtons,
    BRIDGE_U32 tick);
BRIDGE_I32 BridgeStateOnUp(
    BRIDGE_STATE *state,
    BRIDGE_I32 button,
    BRIDGE_I32 targetMatches,
    BRIDGE_U32 tick,
    BRIDGE_I32 *completedSequence);
BRIDGE_I32 BridgeStateAccept(
    BRIDGE_STATE *state,
    BRIDGE_I32 sequence,
    BRIDGE_U32 tick,
    BRIDGE_I32 *completedSequence);
BRIDGE_I32 BridgeStateReject(BRIDGE_STATE *state, BRIDGE_I32 sequence);
BRIDGE_I32 BridgeStateExpire(
    BRIDGE_STATE *state,
    BRIDGE_U32 tick,
    BRIDGE_I32 *expiredSequence);
BRIDGE_I32 BridgeStateTakeRequest(BRIDGE_STATE *state, BRIDGE_I32 sequence);
BRIDGE_I32 BridgeStateIsChordTarget(
    const BRIDGE_STATE *state,
    BRIDGE_U64 root,
    BRIDGE_U32 pid,
    BRIDGE_U32 ownerPid,
    BRIDGE_U64 ownerRoot);

#endif
