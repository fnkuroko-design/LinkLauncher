#include "BridgeHook.h"

#define BRIDGE_LOCK_ATTEMPTS 4096L
#define BRIDGE_MAP_NAME_CAPACITY 96
#define BRIDGE_PROBE_MESSAGE (WM_APP + 0x3A)
#define BRIDGE_PROBE_RIGHT_SEEN 10
#define BRIDGE_PROBE_LEFT_CANDIDATE 20
#define BRIDGE_PROBE_LEFT_REJECTED 21
#define BRIDGE_PROBE_SEND_FAILED 30
#define BRIDGE_PROBE_ACK 31
#define BRIDGE_PROBE_FOREGROUND 40

#pragma data_seg(".LLCFG")
static volatile LONG g_cfgOwnerPid = 0;
static volatile ULONGLONG g_cfgOwnerHwnd = 0;
#pragma data_seg()
#pragma comment(linker, "/SECTION:.LLCFG,RWS")

static HINSTANCE g_module = NULL;
static HHOOK g_mouseHook = NULL;
static HANDLE g_mapping = NULL;
static BRIDGE_SHARED *g_shared = NULL;
static volatile LONG g_mapInitState = 0;

static void BridgeZeroMemory(volatile unsigned char *memory, SIZE_T length)
{
    SIZE_T i;
    for (i = 0; i < length; ++i) memory[i] = 0;
}

static BOOL BridgeMakeMapName(DWORD ownerPid, WCHAR *name, SIZE_T capacity)
{
    static const WCHAR prefix[] = L"Local\\LinkLauncher.MouseChord.";
    WCHAR digits[12];
    SIZE_T prefixLength = 0;
    SIZE_T digitCount = 0;
    SIZE_T i;
    DWORD value = ownerPid;

    if (name == NULL || capacity < (sizeof(prefix) / sizeof(prefix[0])) + 1) return FALSE;
    while (prefix[prefixLength] != L'\0') ++prefixLength;
    if (prefixLength + 12 >= capacity) return FALSE;
    for (i = 0; i < prefixLength; ++i) name[i] = prefix[i];
    do
    {
        digits[digitCount++] = (WCHAR)(L'0' + (value % 10));
        value /= 10;
    } while (value != 0 && digitCount < (sizeof(digits) / sizeof(digits[0])));
    for (i = 0; i < digitCount; ++i) name[prefixLength + i] = digits[digitCount - i - 1];
    name[prefixLength + digitCount] = L'\0';
    return TRUE;
}

static BOOL BridgeTryLock(BRIDGE_SHARED *shared)
{
    LONG i;
    if (shared == NULL) return FALSE;
    for (i = 0; i < BRIDGE_LOCK_ATTEMPTS; ++i)
    {
        if (InterlockedCompareExchange(&shared->lock, 1, 0) == 0) return TRUE;
    }
    return FALSE;
}

static void BridgeUnlock(BRIDGE_SHARED *shared)
{
    InterlockedExchange(&shared->lock, 0);
}

static void BridgeCloseLocalMapping(void)
{
    BRIDGE_SHARED *shared = g_shared;
    HANDLE mapping = g_mapping;
    g_shared = NULL;
    g_mapping = NULL;
    InterlockedExchange(&g_mapInitState, 0);
    if (shared != NULL) UnmapViewOfFile(shared);
    if (mapping != NULL) CloseHandle(mapping);
}

static BOOL BridgeAttachMapping(HANDLE mapping, BRIDGE_SHARED *shared)
{
    if (mapping == NULL || shared == NULL) return FALSE;
    g_mapping = mapping;
    g_shared = shared;
    InterlockedExchange(&g_mapInitState, 2);
    return TRUE;
}

static BRIDGE_SHARED *BridgeEnsureShared(void)
{
    LONG state;
    LONG ownerPid;
    WCHAR mapName[BRIDGE_MAP_NAME_CAPACITY];
    HANDLE mapping;
    BRIDGE_SHARED *shared;

    state = InterlockedCompareExchange(&g_mapInitState, 0, 0);
    if (state == 2) return g_shared;
    if (InterlockedCompareExchange(&g_mapInitState, 1, 0) != 0) return NULL;

    ownerPid = InterlockedCompareExchange(&g_cfgOwnerPid, 0, 0);
    if (ownerPid <= 0 || !BridgeMakeMapName((DWORD)ownerPid, mapName, BRIDGE_MAP_NAME_CAPACITY))
    {
        InterlockedExchange(&g_mapInitState, 0);
        return NULL;
    }
    mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, mapName);
    if (mapping == NULL)
    {
        InterlockedExchange(&g_mapInitState, 0);
        return NULL;
    }
    shared = (BRIDGE_SHARED *)MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(BRIDGE_SHARED));
    if (shared == NULL || shared->magic != (LONG)BRIDGE_SHARED_MAGIC ||
        shared->version != (LONG)BRIDGE_SHARED_VERSION ||
        shared->byteSize != (LONG)sizeof(BRIDGE_SHARED) || shared->ownerPid != ownerPid)
    {
        if (shared != NULL) UnmapViewOfFile(shared);
        CloseHandle(mapping);
        InterlockedExchange(&g_mapInitState, 0);
        return NULL;
    }
    g_mapping = mapping;
    g_shared = shared;
    InterlockedExchange(&g_mapInitState, 2);
    return shared;
}

static void BridgePostOwnerMessage(BRIDGE_SHARED *shared, DWORD sequence, LPARAM phase)
{
    HWND owner;
    if (shared == NULL || sequence == 0 ||
        InterlockedCompareExchange(&shared->enabled, 0, 0) == 0)
    {
        return;
    }
    owner = (HWND)(ULONG_PTR)shared->ownerHwnd;
    if (owner != NULL)
    {
        PostMessageW(owner, BRIDGE_OWNER_MESSAGE, (WPARAM)sequence, phase);
    }
}

#if defined(BRIDGE_INPUT_PROBE)
static void BridgePostProbe(BRIDGE_SHARED *shared, WPARAM stage, LPARAM detail)
{
    HWND owner;
    if (shared == NULL || InterlockedCompareExchange(&shared->enabled, 0, 0) == 0) return;
    owner = (HWND)(ULONG_PTR)shared->ownerHwnd;
    if (owner != NULL) PostMessageW(owner, BRIDGE_PROBE_MESSAGE, stage, detail);
}
#else
#define BridgePostProbe(shared, stage, detail) ((void)0)
#endif

static BOOL BridgeIsRootTarget(HWND window, HWND *root, DWORD *pid, DWORD *tid)
{
    HWND targetRoot;
    DWORD targetPid = 0;
    DWORD targetTid;
    if (window == NULL) return FALSE;
    targetTid = GetWindowThreadProcessId(window, &targetPid);
    if (targetTid == 0 || targetPid == 0) return FALSE;
    targetRoot = GetAncestor(window, GA_ROOT);
    if (targetRoot == NULL) targetRoot = window;
    if (root != NULL) *root = targetRoot;
    if (pid != NULL) *pid = targetPid;
    if (tid != NULL) *tid = targetTid;
    return TRUE;
}

static BOOL BridgeIsRightDown(UINT message)
{
    return message == WM_RBUTTONDOWN || message == WM_NCRBUTTONDOWN;
}

static BOOL BridgeIsLeftDown(UINT message)
{
    return message == WM_LBUTTONDOWN || message == WM_NCLBUTTONDOWN;
}

static BOOL BridgeIsRightUp(UINT message)
{
    return message == WM_RBUTTONUP || message == WM_NCRBUTTONUP;
}

static BOOL BridgeIsLeftUp(UINT message)
{
    return message == WM_LBUTTONUP || message == WM_NCLBUTTONUP;
}

static BOOL BridgeBeginGesture(
    BRIDGE_SHARED *shared,
    DWORD sequence,
    DWORD sourcePid,
    HWND sourceRoot,
    HWND sourceForeground)
{
    DWORD foregroundPid = 0;
    DWORD_PTR uiResult = 0;
    DWORD_PTR sendResult;
    HWND foreground;
    HWND owner;
    DWORD ownerPid;
    HWND capture;
    DWORD capturePid = 0;
    DWORD captureTid;
    BOOL accepted = FALSE;
    BOOL promoted = FALSE;
    LONG decision;
    LONG completedSequence = 0;
    DWORD tick;

    if (shared == NULL || sequence == 0) return FALSE;
    owner = (HWND)(ULONG_PTR)shared->ownerHwnd;
    ownerPid = (DWORD)shared->ownerPid;
    foreground = GetForegroundWindow();
    if (owner == NULL || ownerPid == 0 || foreground == NULL ||
        GetWindowThreadProcessId(foreground, &foregroundPid) == 0 || foregroundPid != sourcePid ||
        foreground != sourceForeground)
    {
        BridgePostProbe(shared, BRIDGE_PROBE_SEND_FAILED, 1);
        if (BridgeTryLock(shared))
        {
            BridgeStateReject(&shared->state, (LONG)sequence);
            BridgeUnlock(shared);
        }
        return FALSE;
    }

    AllowSetForegroundWindow(ownerPid);

    /* GetCapture is per-thread; release only capture owned by this callback thread. */
    capture = GetCapture();
    if (capture != NULL)
    {
        captureTid = GetWindowThreadProcessId(capture, &capturePid);
        if (captureTid == GetCurrentThreadId() && capturePid == sourcePid)
        {
            ReleaseCapture();
        }
    }

    sendResult = SendMessageTimeoutW(
        owner,
        BRIDGE_OWNER_MESSAGE,
        (WPARAM)sequence,
        (LPARAM)BRIDGE_OWNER_BEGIN,
        SMTO_ABORTIFHUNG,
        75,
        &uiResult);
    if (sendResult != 0 && (uiResult == 1 || uiResult == 2)) accepted = TRUE;

    tick = GetTickCount();
    if (accepted && BridgeTryLock(shared))
    {
        decision = BridgeStateAccept(&shared->state, (LONG)sequence, tick, &completedSequence);
        if (decision == BRIDGE_DECISION_BEGIN || decision == BRIDGE_DECISION_COMPLETE)
        {
            /* Acceptance is now visible to all hook bitnesses. */
        }
        else
        {
            accepted = FALSE;
        }
        BridgeUnlock(shared);
    }
    else
    {
        accepted = FALSE;
    }

    if (!accepted)
    {
        BridgePostProbe(shared, BRIDGE_PROBE_SEND_FAILED, sendResult == 0 ? 2 : 3);
        if (BridgeTryLock(shared))
        {
            BridgeStateReject(&shared->state, (LONG)sequence);
            BridgeUnlock(shared);
        }
        return FALSE;
    }

    BridgePostProbe(shared, BRIDGE_PROBE_ACK, (LPARAM)uiResult);

    if (uiResult == 1)
    {
        foreground = GetForegroundWindow();
        foregroundPid = 0;
        if (foreground == sourceForeground &&
            GetWindowThreadProcessId(foreground, &foregroundPid) != 0 && foregroundPid == sourcePid)
        {
            promoted = SetForegroundWindow(owner);
        }
    }
    if (promoted && BridgeTryLock(shared))
    {
        shared->statusBits |= (LONG)BRIDGE_STATUS_FOREGROUND_PROMOTED;
        BridgeUnlock(shared);
    }
    BridgePostProbe(shared, BRIDGE_PROBE_FOREGROUND,
        uiResult == 2 ? 2 : (promoted ? 1 : 0));
    if (completedSequence != 0)
    {
        BridgePostOwnerMessage(shared, (DWORD)completedSequence, BRIDGE_OWNER_COMPLETE);
    }
    (void)sourceRoot;
    return TRUE;
}

static LRESULT CALLBACK BridgeMouseProc(int code, WPARAM wParam, LPARAM lParam)
{
    MOUSEHOOKSTRUCT *mouse;
    BRIDGE_SHARED *shared;
    DWORD tick;
    HWND targetRoot = NULL;
    DWORD targetPid = 0;
    DWORD targetTid = 0;
    DWORD ownerPid;
    HWND ownerRoot;
    LONG decision = BRIDGE_DECISION_PASS;
    LONG completedSequence = 0;
    LONG expiredSequence = 0;
    LONG supersededSequence = 0;
    LONG sequence = 0;
    LONG rejectionDetail = 0;
    BOOL targetMatches = FALSE;
    BOOL beginAccepted;
    HWND sourceForeground = NULL;
    BOOL queuedRightDown = FALSE;
    BOOL queuedOtherButtons = FALSE;
    BOOL hadCandidate = FALSE;
    BOOL candidateStarted = FALSE;
    BOOL activeCancelled = FALSE;
    BOOL probeLeft = FALSE;

    if (code != HC_ACTION || wParam == HC_NOREMOVE)
    {
        return CallNextHookEx(NULL, code, wParam, lParam);
    }
    shared = BridgeEnsureShared();
    if (shared == NULL || InterlockedCompareExchange(&shared->enabled, 0, 0) == 0)
    {
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    mouse = (MOUSEHOOKSTRUCT *)lParam;
    if (mouse == NULL) return CallNextHookEx(NULL, code, wParam, lParam);
    tick = GetTickCount();
    ownerPid = (DWORD)shared->ownerPid;
    ownerRoot = (HWND)(ULONG_PTR)shared->ownerHwnd;

    if (!BridgeIsRootTarget(mouse->hwnd, &targetRoot, &targetPid, &targetTid) ||
        targetPid != GetCurrentProcessId() || targetTid != GetCurrentThreadId())
    {
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    if (wParam == WM_MOUSEMOVE || wParam == WM_NCMOUSEMOVE)
    {
        if (!BridgeTryLock(shared)) return CallNextHookEx(NULL, code, wParam, lParam);
        BridgeStateOnMove(&shared->state, mouse->pt.x, mouse->pt.y);
        BridgeUnlock(shared);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    if (BridgeIsRightDown((UINT)wParam))
    {
        if (!BridgeTryLock(shared)) return CallNextHookEx(NULL, code, wParam, lParam);
        if (shared->state.phase == BRIDGE_PHASE_ACTIVE &&
            (BRIDGE_U32)(tick - shared->state.startTick) >= BRIDGE_WATCHDOG_MS)
        {
            BridgeStateExpire(&shared->state, tick, &expiredSequence);
        }
        if (targetPid == ownerPid)
        {
            if (shared->state.phase == BRIDGE_PHASE_CANDIDATE ||
                shared->state.phase == BRIDGE_PHASE_ACTIVE)
            {
                if (shared->state.phase == BRIDGE_PHASE_ACTIVE &&
                    shared->state.requestValid && shared->state.pendingButtons != 0)
                {
                    supersededSequence = shared->state.requestSequence;
                }
                BridgeStateCancel(&shared->state);
            }
        }
        else
        {
            BridgeStateOnRightDown(
                &shared->state,
                (BRIDGE_U64)(ULONG_PTR)targetRoot,
                targetPid,
                targetTid,
                mouse->pt.x,
                mouse->pt.y,
                tick,
                &supersededSequence);
            candidateStarted = shared->state.phase == BRIDGE_PHASE_CANDIDATE &&
                shared->state.targetRoot == (BRIDGE_U64)(ULONG_PTR)targetRoot &&
                (DWORD)shared->state.targetPid == targetPid &&
                (DWORD)shared->state.targetTid == targetTid;
        }
        BridgeUnlock(shared);
        if (expiredSequence != 0)
        {
            BridgePostOwnerMessage(shared, (DWORD)expiredSequence, BRIDGE_OWNER_COMPLETE);
        }
        if (supersededSequence != 0)
        {
            BridgePostOwnerMessage(shared, (DWORD)supersededSequence, BRIDGE_OWNER_COMPLETE);
        }
        if (candidateStarted) BridgePostProbe(shared, BRIDGE_PROBE_RIGHT_SEEN, 0);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    if (BridgeIsRightUp((UINT)wParam) || BridgeIsLeftUp((UINT)wParam))
    {
        LONG button = BridgeIsRightUp((UINT)wParam)
            ? (LONG)BRIDGE_BUTTON_RIGHT
            : (LONG)BRIDGE_BUTTON_LEFT;
        if (!BridgeTryLock(shared)) return CallNextHookEx(NULL, code, wParam, lParam);
        targetMatches = BridgeStateIsChordTarget(
            &shared->state,
            (BRIDGE_U64)(ULONG_PTR)targetRoot,
            targetPid,
            ownerPid,
            (BRIDGE_U64)(ULONG_PTR)ownerRoot);
        decision = BridgeStateOnUp(&shared->state, button, targetMatches, tick, &completedSequence);
        BridgeUnlock(shared);
        if (completedSequence != 0)
        {
            BridgePostOwnerMessage(shared, (DWORD)completedSequence, BRIDGE_OWNER_COMPLETE);
        }
        if (decision == BRIDGE_DECISION_CONSUME || decision == BRIDGE_DECISION_COMPLETE)
        {
            return 1;
        }
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    if (BridgeIsLeftDown((UINT)wParam))
    {
        if (targetPid != ownerPid)
        {
            probeLeft = TRUE;
            queuedRightDown = (GetKeyState(VK_RBUTTON) & 0x8000) != 0;
            queuedOtherButtons =
                (GetKeyState(VK_MBUTTON) & 0x8000) != 0 ||
                (GetKeyState(VK_XBUTTON1) & 0x8000) != 0 ||
                (GetKeyState(VK_XBUTTON2) & 0x8000) != 0;
            sourceForeground = GetForegroundWindow();
        }
        if (!BridgeTryLock(shared)) return CallNextHookEx(NULL, code, wParam, lParam);
        if (shared->state.phase == BRIDGE_PHASE_ACTIVE)
        {
            if (shared->state.requestValid && shared->state.pendingButtons != 0)
            {
                supersededSequence = shared->state.requestSequence;
            }
            BridgeStateCancel(&shared->state);
            activeCancelled = TRUE;
        }
        else if (targetPid == ownerPid && shared->state.phase == BRIDGE_PHASE_CANDIDATE)
        {
            BridgeStateCancel(&shared->state);
        }
        else if (targetPid != ownerPid)
        {
            hadCandidate = shared->state.phase == BRIDGE_PHASE_CANDIDATE;
            if (hadCandidate)
            {
                if (!queuedRightDown) rejectionDetail = 1;
                else if (queuedOtherButtons) rejectionDetail = 2;
                else if (shared->state.targetRoot != (BRIDGE_U64)(ULONG_PTR)targetRoot ||
                    (DWORD)shared->state.targetPid != targetPid ||
                    (DWORD)shared->state.targetTid != targetTid) rejectionDetail = 3;
                else
                {
                    LONG dx = shared->state.pressX - mouse->pt.x;
                    LONG dy = shared->state.pressY - mouse->pt.y;
                    if (dx < 0) dx = -dx;
                    if (dy < 0) dy = -dy;
                    rejectionDetail = dx * dx + dy * dy > 25 ? 4 : 5;
                }
            }
            decision = BridgeStateOnLeftDown(
                &shared->state,
                (BRIDGE_U64)(ULONG_PTR)targetRoot,
                targetPid,
                targetTid,
                mouse->pt.x,
                mouse->pt.y,
                queuedRightDown,
                queuedOtherButtons,
                tick);
        }
        if (decision == BRIDGE_DECISION_BEGIN)
        {
            sequence = shared->state.requestSequence;
        }
        BridgeUnlock(shared);
        if (supersededSequence != 0)
        {
            BridgePostOwnerMessage(shared, (DWORD)supersededSequence, BRIDGE_OWNER_COMPLETE);
        }
        if (probeLeft && decision != BRIDGE_DECISION_BEGIN)
        {
            BridgePostProbe(shared, BRIDGE_PROBE_LEFT_REJECTED, rejectionDetail);
        }
        if (activeCancelled) return CallNextHookEx(NULL, code, wParam, lParam);
        if (decision != BRIDGE_DECISION_BEGIN) return CallNextHookEx(NULL, code, wParam, lParam);

        BridgePostProbe(shared, BRIDGE_PROBE_LEFT_CANDIDATE, (LPARAM)sequence);
        beginAccepted = BridgeBeginGesture(shared, (DWORD)sequence, targetPid, targetRoot, sourceForeground);
        if (beginAccepted) return 1;
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    return CallNextHookEx(NULL, code, wParam, lParam);
}

BOOL __cdecl BridgeInstall(HWND owner, DWORD ownerPid)
{
    WCHAR mapName[BRIDGE_MAP_NAME_CAPACITY];
    HANDLE mapping = NULL;
    BRIDGE_SHARED *shared = NULL;
#if defined(_WIN64)
    DWORD lastError = ERROR_SUCCESS;
    BOOL created = FALSE;
#endif
    LONG currentPid = (LONG)GetCurrentProcessId();

    if (owner == NULL || ownerPid == 0 || !BridgeMakeMapName(ownerPid, mapName, BRIDGE_MAP_NAME_CAPACITY))
    {
        return FALSE;
    }
    if (g_mouseHook != NULL && g_shared != NULL &&
        InterlockedCompareExchange(&g_shared->enabled, 0, 0) != 0)
    {
        return TRUE;
    }

#if defined(_WIN64)
    if ((DWORD)currentPid != ownerPid) return FALSE;
    mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0,
        (DWORD)sizeof(BRIDGE_SHARED), mapName);
    if (mapping == NULL) return FALSE;
    lastError = GetLastError();
    created = (lastError != ERROR_ALREADY_EXISTS);
    shared = (BRIDGE_SHARED *)MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(BRIDGE_SHARED));
    if (shared == NULL)
    {
        CloseHandle(mapping);
        return FALSE;
    }
    if (created)
    {
        BridgeZeroMemory((volatile unsigned char *)shared, sizeof(BRIDGE_SHARED));
        shared->magic = (LONG)BRIDGE_SHARED_MAGIC;
        shared->version = (LONG)BRIDGE_SHARED_VERSION;
        shared->byteSize = (LONG)sizeof(BRIDGE_SHARED);
        shared->ownerPid = (LONG)ownerPid;
        shared->ownerHwnd = (ULONGLONG)(ULONG_PTR)owner;
        BridgeStateInit(&shared->state);
    }
    else if (shared->magic != (LONG)BRIDGE_SHARED_MAGIC ||
        shared->version != (LONG)BRIDGE_SHARED_VERSION ||
        shared->byteSize != (LONG)sizeof(BRIDGE_SHARED) || shared->enabled != 0)
    {
        UnmapViewOfFile(shared);
        CloseHandle(mapping);
        return FALSE;
    }
    else
    {
        LONG previousSequence = shared->state.sequence;
        BridgeZeroMemory((volatile unsigned char *)shared, sizeof(BRIDGE_SHARED));
        shared->magic = (LONG)BRIDGE_SHARED_MAGIC;
        shared->version = (LONG)BRIDGE_SHARED_VERSION;
        shared->byteSize = (LONG)sizeof(BRIDGE_SHARED);
        shared->ownerPid = (LONG)ownerPid;
        shared->ownerHwnd = (ULONGLONG)(ULONG_PTR)owner;
        BridgeStateInit(&shared->state);
        shared->state.sequence = previousSequence;
    }
#else
    if ((DWORD)currentPid == ownerPid) return FALSE;
    mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, mapName);
    if (mapping == NULL) return FALSE;
    shared = (BRIDGE_SHARED *)MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(BRIDGE_SHARED));
    if (shared == NULL)
    {
        CloseHandle(mapping);
        return FALSE;
    }
    if (shared->magic != (LONG)BRIDGE_SHARED_MAGIC ||
        shared->version != (LONG)BRIDGE_SHARED_VERSION ||
        shared->byteSize != (LONG)sizeof(BRIDGE_SHARED) || shared->ownerPid != (LONG)ownerPid ||
        (DWORD)shared->ownerHwnd != (DWORD)(ULONG_PTR)owner ||
        InterlockedCompareExchange(&shared->enabled, 0, 0) == 0)
    {
        UnmapViewOfFile(shared);
        CloseHandle(mapping);
        return FALSE;
    }
#endif

    if (!BridgeAttachMapping(mapping, shared))
    {
        UnmapViewOfFile(shared);
        CloseHandle(mapping);
        return FALSE;
    }
    g_cfgOwnerPid = (LONG)ownerPid;
    g_cfgOwnerHwnd = shared->ownerHwnd;
    g_mouseHook = SetWindowsHookExW(WH_MOUSE, BridgeMouseProc, g_module, 0);
    if (g_mouseHook == NULL)
    {
        if ((DWORD)currentPid == ownerPid)
        {
            InterlockedExchange(&shared->enabled, 0);
            shared->statusBits = 0;
        }
        BridgeCloseLocalMapping();
        g_cfgOwnerPid = 0;
        g_cfgOwnerHwnd = 0;
        return FALSE;
    }

    if (!BridgeTryLock(shared))
    {
        UnhookWindowsHookEx(g_mouseHook);
        g_mouseHook = NULL;
        BridgeCloseLocalMapping();
        return FALSE;
    }
    if ((DWORD)currentPid == ownerPid)
    {
        shared->statusBits |= (LONG)BRIDGE_STATUS_HOOK64;
        shared->statusBits |= (LONG)BRIDGE_STATUS_ENABLED;
        InterlockedExchange(&shared->enabled, 1);
    }
    else
    {
        shared->statusBits |= (LONG)BRIDGE_STATUS_HOOK32;
    }
    BridgeUnlock(shared);
    return TRUE;
}

void __cdecl BridgeStop(void)
{
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    BOOL isOwner = ((DWORD)InterlockedCompareExchange(&g_cfgOwnerPid, 0, 0) == GetCurrentProcessId());

    if (isOwner && shared != NULL)
    {
        InterlockedExchange(&shared->enabled, 0);
        if (BridgeTryLock(shared))
        {
            shared->statusBits &= ~((LONG)BRIDGE_STATUS_HOOK64 |
                (LONG)BRIDGE_STATUS_ENABLED | (LONG)BRIDGE_STATUS_FOREGROUND_PROMOTED);
            BridgeStateCancel(&shared->state);
            BridgeUnlock(shared);
        }
        g_cfgOwnerPid = 0;
        g_cfgOwnerHwnd = 0;
    }

    if (g_mouseHook != NULL)
    {
        UnhookWindowsHookEx(g_mouseHook);
        g_mouseHook = NULL;
    }
    if (!isOwner && shared != NULL && BridgeTryLock(shared))
    {
        shared->statusBits &= ~(LONG)BRIDGE_STATUS_HOOK32;
        BridgeUnlock(shared);
    }
    BridgeCloseLocalMapping();
}

UINT __cdecl BridgePendingButtons(void)
{
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    UINT pending = 0;
    if (shared == NULL) return 0;
    if (!BridgeTryLock(shared)) return 0;
    pending = (UINT)shared->state.pendingButtons;
    BridgeUnlock(shared);
    return pending;
}

UINT __cdecl BridgeSequence(void)
{
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    UINT sequence = 0;
    if (shared == NULL) return 0;
    if (!BridgeTryLock(shared)) return 0;
    sequence = (UINT)shared->state.sequence;
    BridgeUnlock(shared);
    return sequence;
}

UINT __cdecl BridgeStatus(void)
{
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    UINT status = 0;
    if (shared == NULL) return 0;
    if (!BridgeTryLock(shared)) return 0;
    status = (UINT)shared->statusBits;
    BridgeUnlock(shared);
    return status;
}

UINT __cdecl BridgeHasCandidate(void)
{
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    UINT candidate = 0;
    if (shared == NULL) return 0;
    if (!BridgeTryLock(shared)) return 0;
    candidate = shared->state.phase == BRIDGE_PHASE_CANDIDATE ? 1U : 0U;
    BridgeUnlock(shared);
    return candidate;
}

BOOL __cdecl BridgeTakeRequest(UINT sequence)
{
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    LONG taken = 0;
    if (shared == NULL || sequence == 0) return FALSE;
    if (!BridgeTryLock(shared)) return FALSE;
    taken = BridgeStateTakeRequest(&shared->state, (LONG)sequence);
    BridgeUnlock(shared);
    return taken != 0;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        BridgeCloseLocalMapping();
    }
    return TRUE;
}
