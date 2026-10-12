#ifndef LINKLAUNCHER_BRIDGE_REQUEST_H
#define LINKLAUNCHER_BRIDGE_REQUEST_H
#include <windows.h>
#include "BridgeState.h"

/* A sequence-tagged, one-way decision shared by the sender and UI.
 * Resolved accepted decisions survive response timeouts and state-lock contention. */
void BridgeRequestStart(volatile LONG64 *request, DWORD sequence);
BOOL BridgeRequestIsPending(volatile LONG64 *request, DWORD sequence);
BOOL BridgeRequestCommit(volatile LONG64 *request, DWORD sequence, BOOL ownerShown);
/* Return 1=show, 2=hide, 0=cancelled/stale. Only the sender resolves a pending request. */
LONG BridgeRequestResolve(volatile LONG64 *request, DWORD sequence);
/* Return 1/2=resolved accepted, -1=resolved cancelled, 0=unresolved/stale. */
LONG BridgeRequestDecision(volatile LONG64 *request, DWORD sequence);
/* Caller holds the state lock; finalize before processing the next input event.
 * Returns a sequence whose releases already completed, or zero. */
LONG BridgeRequestFinalize(volatile LONG64 *request, BRIDGE_STATE *state,
    BRIDGE_MENU_GUARD *guard, DWORD tick);
#endif
