#ifndef LINKLAUNCHER_BRIDGE_HOOK_H
#define LINKLAUNCHER_BRIDGE_HOOK_H

#include <windows.h>
#include "BridgeState.h"

#define BRIDGE_SHARED_MAGIC 0x434D4C4CUL /* LLMC */
#define BRIDGE_SHARED_VERSION 2UL
#define BRIDGE_OWNER_MESSAGE (WM_APP + 0x39)
#define BRIDGE_OWNER_BEGIN 1
#define BRIDGE_OWNER_COMPLETE 2

#define BRIDGE_STATUS_HOOK64 0x00000001UL
#define BRIDGE_STATUS_HOOK32 0x00000002UL
#define BRIDGE_STATUS_ENABLED 0x00000004UL
#define BRIDGE_STATUS_FOREGROUND_PROMOTED 0x00000008UL
#define BRIDGE_STATUS_MENU_HOOK 0x00000010UL

/* 固定幅と明示的なpaddingで両bitnessを揃え、stateの8-byte alignmentも維持する。 */
#pragma pack(push, 8)
typedef struct BRIDGE_SHARED
{
    volatile LONG lock;
    volatile LONG enabled;
    volatile LONG statusBits;
    LONG magic;
    LONG version;
    LONG byteSize;
    LONG ownerPid;
    volatile LONG otherButtons;
    ULONGLONG ownerHwnd;
    BRIDGE_STATE state;
} BRIDGE_SHARED;
#pragma pack(pop)
typedef char BRIDGE_SHARED_SIZE_CHECK[(sizeof(BRIDGE_SHARED) == 104) ? 1 : -1];

/* These names are exported through the architecture-specific .def files. */
BOOL __cdecl BridgeInstall(HWND owner, DWORD ownerPid);
void __cdecl BridgeStop(void);
UINT __cdecl BridgePendingButtons(void);
UINT __cdecl BridgeSequence(void);
UINT __cdecl BridgeStatus(void);
UINT __cdecl BridgeHasCandidate(void);
BOOL __cdecl BridgeTakeRequest(UINT sequence);

/* Used as the x86 no-CRT executable entry point. */
void __cdecl BridgeHostEntry(void);

#endif
