#include "BridgeHook.h"

#define BRIDGE_LOCK_ATTEMPTS 4096L
#define BRIDGE_MAP_NAME_CAPACITY 96
#define BRIDGE_GESTURE_BUDGET_MS 75UL
#define BRIDGE_PROBE_MESSAGE (WM_APP + 0x3A)
#define BRIDGE_PROBE_RIGHT_SEEN 10
#define BRIDGE_PROBE_LEFT_CANDIDATE 20
#define BRIDGE_PROBE_LEFT_REJECTED 21
#define BRIDGE_PROBE_SEND_FAILED 30
#define BRIDGE_PROBE_ACK 31
#define BRIDGE_PROBE_CAPTURE_FAILED 32
#define BRIDGE_PROBE_FOREGROUND 40
#define BRIDGE_PROBE_CONTEXT 41

#define BRIDGE_OTHER_BUTTON_MIDDLE 0x00000001L
#define BRIDGE_OTHER_BUTTON_X1 0x00000002L
#define BRIDGE_OTHER_BUTTON_X2 0x00000004L

enum BRIDGE_PROBE_CAPTURE_FAILURE
{
    BRIDGE_PROBE_CAPTURE_FAILURE_SOURCE_THREAD = 1,
    BRIDGE_PROBE_CAPTURE_FAILURE_SOURCE_WINDOW = 2,
    BRIDGE_PROBE_CAPTURE_FAILURE_RELEASE = 3,
    BRIDGE_PROBE_CAPTURE_FAILURE_QUERY = 4,
    BRIDGE_PROBE_CAPTURE_FAILURE_WINDOW = 5,
    BRIDGE_PROBE_CAPTURE_FAILURE_BUDGET = 6,
    BRIDGE_PROBE_CAPTURE_FAILURE_CANCEL = 7,
    BRIDGE_PROBE_CAPTURE_FAILURE_RECHECK = 8,
    BRIDGE_PROBE_CAPTURE_FAILURE_REMAINS = 9
};

#pragma data_seg(".LLCFG")
static volatile LONG g_cfgOwnerPid = 0;
static volatile ULONGLONG g_cfgOwnerHwnd = 0;
#pragma data_seg()
#pragma comment(linker, "/SECTION:.LLCFG,RWS")

#if defined(BRIDGE_INPUT_PROBE)
typedef struct BRIDGE_PROBE_STATS
{
    volatile LONG callbackTotal;
    volatile LONG lastCode;
    volatile LONG lastMessage;
    volatile LONG hcNoRemove;
    volatile LONG nonAction;
    volatile LONG sharedAcquireFailure;
    volatile LONG ownerPidZero;
    volatile LONG mapNameFailure;
    volatile LONG initBusy;
    volatile LONG openMappingFailure;
    volatile LONG mapViewFailure;
    volatile LONG validationFailure;
    volatile LONG lastSharedFailure;
    volatile LONG lastMappingError;
    volatile LONG lastValidationFailure;
    volatile LONG disabled;
    volatile LONG nullMouse;
    volatile LONG targetLookupFailure;
    volatile LONG contextSkipped;
    volatile LONG contextAccepted;
    volatile LONG rightDown;
    volatile LONG leftDown;
    volatile LONG up;
    volatile LONG candidate;
    volatile LONG rejected;
    volatile LONG begin;
    volatile LONG ack;
    volatile LONG complete;
    volatile LONG lastTargetPid;
    volatile LONG lastTargetTid;
    volatile LONG lastCurrentPid;
    volatile LONG lastCurrentTid;
    volatile LONG lockFailure;
} BRIDGE_PROBE_STATS;
typedef char BRIDGE_PROBE_SIZE_CHECK[(sizeof(BRIDGE_PROBE_STATS) == 132) ? 1 : -1];

enum BRIDGE_PROBE_SHARED_FAILURE
{
    BRIDGE_PROBE_SHARED_FAILURE_NONE = 0,
    BRIDGE_PROBE_SHARED_FAILURE_OWNER_PID = 1,
    BRIDGE_PROBE_SHARED_FAILURE_MAP_NAME = 2,
    BRIDGE_PROBE_SHARED_FAILURE_INIT_BUSY = 3,
    BRIDGE_PROBE_SHARED_FAILURE_OPEN_MAPPING = 4,
    BRIDGE_PROBE_SHARED_FAILURE_MAP_VIEW = 5,
    BRIDGE_PROBE_SHARED_FAILURE_VALIDATION = 6
};

enum BRIDGE_PROBE_VALIDATION_FAILURE
{
    BRIDGE_PROBE_VALIDATION_MAGIC = 1,
    BRIDGE_PROBE_VALIDATION_VERSION = 2,
    BRIDGE_PROBE_VALIDATION_SIZE = 3,
    BRIDGE_PROBE_VALIDATION_OWNER_PID = 4
};

#pragma data_seg(".LLPRB")
static volatile BRIDGE_PROBE_STATS g_probeStats = { 0 };
#pragma data_seg()
#pragma comment(linker, "/SECTION:.LLPRB,RWS")

#define BRIDGE_PROBE_COUNT(field) InterlockedIncrement(&g_probeStats.field)
#define BRIDGE_PROBE_SET(field, value) InterlockedExchange(&g_probeStats.field, (LONG)(value))
#else
#define BRIDGE_PROBE_COUNT(field) ((void)0)
#define BRIDGE_PROBE_SET(field, value) ((void)0)
#endif

#if defined(_WIN64)
static LONG BridgeReadOtherButtons(void)
{
    LONG buttons = 0;
    if ((GetAsyncKeyState(VK_MBUTTON) & (SHORT)0x8000) != 0)
        buttons |= BRIDGE_OTHER_BUTTON_MIDDLE;
    if ((GetAsyncKeyState(VK_XBUTTON1) & (SHORT)0x8000) != 0)
        buttons |= BRIDGE_OTHER_BUTTON_X1;
    if ((GetAsyncKeyState(VK_XBUTTON2) & (SHORT)0x8000) != 0)
        buttons |= BRIDGE_OTHER_BUTTON_X2;
    return buttons;
}
#endif

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
    if (InterlockedCompareExchange(&g_mapInitState, 1, 0) != 0)
    {
        BRIDGE_PROBE_COUNT(sharedAcquireFailure);
        BRIDGE_PROBE_COUNT(initBusy);
        BRIDGE_PROBE_SET(lastSharedFailure, BRIDGE_PROBE_SHARED_FAILURE_INIT_BUSY);
        BRIDGE_PROBE_SET(lastMappingError, 0);
        return NULL;
    }

    ownerPid = InterlockedCompareExchange(&g_cfgOwnerPid, 0, 0);
    if (ownerPid <= 0)
    {
        BRIDGE_PROBE_COUNT(sharedAcquireFailure);
        BRIDGE_PROBE_COUNT(ownerPidZero);
        BRIDGE_PROBE_SET(lastSharedFailure, BRIDGE_PROBE_SHARED_FAILURE_OWNER_PID);
        BRIDGE_PROBE_SET(lastMappingError, 0);
        InterlockedExchange(&g_mapInitState, 0);
        return NULL;
    }
    if (!BridgeMakeMapName((DWORD)ownerPid, mapName, BRIDGE_MAP_NAME_CAPACITY))
    {
        BRIDGE_PROBE_COUNT(sharedAcquireFailure);
        BRIDGE_PROBE_COUNT(mapNameFailure);
        BRIDGE_PROBE_SET(lastSharedFailure, BRIDGE_PROBE_SHARED_FAILURE_MAP_NAME);
        BRIDGE_PROBE_SET(lastMappingError, 0);
        InterlockedExchange(&g_mapInitState, 0);
        return NULL;
    }
    mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, mapName);
    if (mapping == NULL)
    {
        BRIDGE_PROBE_COUNT(sharedAcquireFailure);
        BRIDGE_PROBE_COUNT(openMappingFailure);
        BRIDGE_PROBE_SET(lastSharedFailure, BRIDGE_PROBE_SHARED_FAILURE_OPEN_MAPPING);
        BRIDGE_PROBE_SET(lastMappingError, GetLastError());
        InterlockedExchange(&g_mapInitState, 0);
        return NULL;
    }
    shared = (BRIDGE_SHARED *)MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(BRIDGE_SHARED));
    if (shared == NULL)
    {
        BRIDGE_PROBE_COUNT(sharedAcquireFailure);
        BRIDGE_PROBE_COUNT(mapViewFailure);
        BRIDGE_PROBE_SET(lastSharedFailure, BRIDGE_PROBE_SHARED_FAILURE_MAP_VIEW);
        BRIDGE_PROBE_SET(lastMappingError, GetLastError());
        CloseHandle(mapping);
        InterlockedExchange(&g_mapInitState, 0);
        return NULL;
    }
    if (shared->magic != (LONG)BRIDGE_SHARED_MAGIC ||
        shared->version != (LONG)BRIDGE_SHARED_VERSION ||
        shared->byteSize != (LONG)sizeof(BRIDGE_SHARED) || shared->ownerPid != ownerPid)
    {
#if defined(BRIDGE_INPUT_PROBE)
        LONG validationFailure = shared->magic != (LONG)BRIDGE_SHARED_MAGIC
            ? BRIDGE_PROBE_VALIDATION_MAGIC
            : (shared->version != (LONG)BRIDGE_SHARED_VERSION
                ? BRIDGE_PROBE_VALIDATION_VERSION
                : (shared->byteSize != (LONG)sizeof(BRIDGE_SHARED)
                    ? BRIDGE_PROBE_VALIDATION_SIZE
                    : BRIDGE_PROBE_VALIDATION_OWNER_PID));
        BRIDGE_PROBE_COUNT(sharedAcquireFailure);
        BRIDGE_PROBE_COUNT(validationFailure);
        BRIDGE_PROBE_SET(lastSharedFailure, BRIDGE_PROBE_SHARED_FAILURE_VALIDATION);
        BRIDGE_PROBE_SET(lastMappingError, 0);
        BRIDGE_PROBE_SET(lastValidationFailure, validationFailure);
#endif
        UnmapViewOfFile(shared);
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
#if defined(BRIDGE_INPUT_PROBE)
    if (phase == BRIDGE_OWNER_COMPLETE) BRIDGE_PROBE_COUNT(complete);
#endif
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
    return message == WM_RBUTTONDOWN || message == WM_RBUTTONDBLCLK ||
        message == WM_NCRBUTTONDOWN || message == WM_NCRBUTTONDBLCLK;
}

static BOOL BridgeIsLeftDown(UINT message)
{
    return message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK ||
        message == WM_NCLBUTTONDOWN || message == WM_NCLBUTTONDBLCLK;
}

static BOOL BridgeIsRightUp(UINT message)
{
    return message == WM_RBUTTONUP || message == WM_NCRBUTTONUP;
}

static BOOL BridgeIsLeftUp(UINT message)
{
    return message == WM_LBUTTONUP || message == WM_NCLBUTTONUP;
}

static BOOL BridgeGetOtherButtonChange(
    UINT message,
    const MOUSEHOOKSTRUCT *mouse,
    LONG *button,
    BOOL *isDown)
{
    switch (message)
    {
    case WM_MBUTTONDOWN:
    case WM_MBUTTONDBLCLK:
    case WM_NCMBUTTONDOWN:
    case WM_NCMBUTTONDBLCLK:
        *button = BRIDGE_OTHER_BUTTON_MIDDLE;
        *isDown = TRUE;
        return TRUE;
    case WM_MBUTTONUP:
    case WM_NCMBUTTONUP:
        *button = BRIDGE_OTHER_BUTTON_MIDDLE;
        *isDown = FALSE;
        return TRUE;
    case WM_XBUTTONDOWN:
    case WM_XBUTTONDBLCLK:
    case WM_NCXBUTTONDOWN:
    case WM_NCXBUTTONDBLCLK:
    case WM_XBUTTONUP:
    case WM_NCXBUTTONUP:
    {
        WORD xButton;
        if (mouse == NULL) return FALSE;
        xButton = HIWORD(((const MOUSEHOOKSTRUCTEX *)mouse)->mouseData);
        if (xButton == XBUTTON1) *button = BRIDGE_OTHER_BUTTON_X1;
        else if (xButton == XBUTTON2) *button = BRIDGE_OTHER_BUTTON_X2;
        else return FALSE;
        *isDown = message == WM_XBUTTONDOWN || message == WM_XBUTTONDBLCLK ||
            message == WM_NCXBUTTONDOWN || message == WM_NCXBUTTONDBLCLK;
        return TRUE;
    }
    default:
        return FALSE;
    }
}

static DWORD BridgeRemainingGestureBudget(DWORD startTick)
{
    DWORD elapsed = GetTickCount() - startTick;
    return elapsed >= BRIDGE_GESTURE_BUDGET_MS
        ? 0
        : BRIDGE_GESTURE_BUDGET_MS - elapsed;
}

static void BridgeRejectSequence(BRIDGE_SHARED *shared, DWORD sequence)
{
    if (shared == NULL || sequence == 0 || !BridgeTryLock(shared)) return;
    BridgeStateReject(&shared->state, (LONG)sequence);
    BridgeUnlock(shared);
}

static BOOL BridgeReleaseSourceCapture(
    DWORD sourcePid,
    DWORD sourceTid,
    HWND sourceRoot,
    BOOL sourceContext,
    DWORD currentTid,
    DWORD startTick,
    LONG *failureDetail)
{
    HWND capture;
    HWND captureRoot = NULL;
    DWORD capturePid = 0;
    DWORD captureTid = 0;

    if (failureDetail != NULL) *failureDetail = 0;

    if (sourceContext)
    {
        if (currentTid != sourceTid)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_SOURCE_THREAD;
            return FALSE;
        }
        capture = GetCapture();
        if (capture == NULL) return TRUE;
        if (!BridgeIsRootTarget(capture, &captureRoot, &capturePid, &captureTid) ||
            capturePid != sourcePid || captureTid != sourceTid || captureRoot != sourceRoot)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_SOURCE_WINDOW;
            return FALSE;
        }
        if (BridgeRemainingGestureBudget(startTick) == 0 || !ReleaseCapture() || GetCapture() != NULL)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_RELEASE;
            return FALSE;
        }
        return TRUE;
    }

    {
        GUITHREADINFO info;
        DWORD_PTR cancelResult = 0;
        DWORD remaining;

        BridgeZeroMemory((volatile unsigned char *)&info, sizeof(info));
        info.cbSize = (DWORD)sizeof(info);
        if (!GetGUIThreadInfo(sourceTid, &info))
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_QUERY;
            return FALSE;
        }
        capture = info.hwndCapture;
        if (capture == NULL) return TRUE;
        if (!BridgeIsRootTarget(capture, &captureRoot, &capturePid, &captureTid) ||
            capturePid != sourcePid || captureTid != sourceTid || captureRoot != sourceRoot)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_WINDOW;
            return FALSE;
        }
        remaining = BridgeRemainingGestureBudget(startTick);
        if (remaining == 0)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_BUDGET;
            return FALSE;
        }
        if (SendMessageTimeoutW(
                capture,
                WM_CANCELMODE,
                0,
                0,
                SMTO_ABORTIFHUNG,
                remaining,
                &cancelResult) == 0)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_CANCEL;
            return FALSE;
        }

        BridgeZeroMemory((volatile unsigned char *)&info, sizeof(info));
        info.cbSize = (DWORD)sizeof(info);
        if (!GetGUIThreadInfo(sourceTid, &info))
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_RECHECK;
            return FALSE;
        }
        if (info.hwndCapture != NULL)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_REMAINS;
            return FALSE;
        }
    }
    return TRUE;
}

static BOOL BridgeBeginGesture(
    BRIDGE_SHARED *shared,
    DWORD sequence,
    DWORD sourcePid,
    DWORD sourceTid,
    HWND sourceRoot,
    HWND sourceForeground,
    BOOL sourceContext,
    DWORD currentTid)
{
    DWORD foregroundPid = 0;
    DWORD_PTR uiResult = 0;
    DWORD_PTR sendResult;
    HWND foreground;
    HWND owner;
    DWORD ownerPid;
    DWORD remaining;
    BOOL accepted = FALSE;
    BOOL promoted = FALSE;
    LONG decision;
    LONG completedSequence = 0;
    DWORD tick;
    DWORD startTick = GetTickCount();
    LONG captureFailure = 0;

    if (shared == NULL || sequence == 0) return FALSE;
    owner = (HWND)(ULONG_PTR)shared->ownerHwnd;
    ownerPid = (DWORD)shared->ownerPid;
    foreground = GetForegroundWindow();
    if (owner == NULL || ownerPid == 0 || foreground == NULL ||
        GetWindowThreadProcessId(foreground, &foregroundPid) == 0 || foregroundPid != sourcePid ||
        foreground != sourceForeground)
    {
        BridgePostProbe(shared, BRIDGE_PROBE_SEND_FAILED, 1);
        BridgeRejectSequence(shared, sequence);
        return FALSE;
    }

    if (sourceContext)
    {
        AllowSetForegroundWindow(ownerPid);
    }

    if (!BridgeReleaseSourceCapture(
            sourcePid,
            sourceTid,
            sourceRoot,
            sourceContext,
            currentTid,
            startTick,
            &captureFailure))
    {
        BridgePostProbe(shared, BRIDGE_PROBE_CAPTURE_FAILED, captureFailure);
        BridgeRejectSequence(shared, sequence);
        return FALSE;
    }

    foreground = GetForegroundWindow();
    foregroundPid = 0;
    if (foreground == NULL || foreground != sourceForeground ||
        GetWindowThreadProcessId(foreground, &foregroundPid) == 0 || foregroundPid != sourcePid)
    {
        BridgePostProbe(shared, BRIDGE_PROBE_SEND_FAILED, 4);
        BridgeRejectSequence(shared, sequence);
        return FALSE;
    }

    remaining = BridgeRemainingGestureBudget(startTick);
    if (remaining == 0)
    {
        sendResult = 0;
    }
    else
    {
        sendResult = SendMessageTimeoutW(
            owner,
            BRIDGE_OWNER_MESSAGE,
            (WPARAM)sequence,
            (LPARAM)BRIDGE_OWNER_BEGIN,
            SMTO_ABORTIFHUNG,
            remaining,
            &uiResult);
    }
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
        BridgePostProbe(shared, BRIDGE_PROBE_SEND_FAILED,
            sendResult == 0 ? (remaining == 0 ? 5 : 2) : 3);
        BridgeRejectSequence(shared, sequence);
        return FALSE;
    }

    BRIDGE_PROBE_COUNT(ack);
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
    return TRUE;
}

static LRESULT CALLBACK BridgeMouseProc(int code, WPARAM wParam, LPARAM lParam)
{
    MOUSEHOOKSTRUCT *mouse;
    BRIDGE_SHARED *shared;
    DWORD tick;
    DWORD currentPid;
    DWORD currentTid;
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
    BOOL sourceContext;
    BOOL beginAccepted;
    HWND sourceForeground = NULL;
    BOOL queuedRightDown = FALSE;
    BOOL queuedOtherButtons = FALSE;
    BOOL hadCandidate = FALSE;
    BOOL candidateStarted = FALSE;
    BOOL activeCancelled = FALSE;
    BOOL probeLeft = FALSE;
    BOOL otherButtonDown = FALSE;
    LONG otherButton = 0;

    if (code < 0)
    {
        return CallNextHookEx(NULL, code, wParam, lParam);
    }
#if defined(BRIDGE_INPUT_PROBE)
    BRIDGE_PROBE_COUNT(callbackTotal);
    BRIDGE_PROBE_SET(lastCode, code);
    BRIDGE_PROBE_SET(lastMessage, (UINT)wParam);
#endif
    if (code == HC_NOREMOVE)
    {
        BRIDGE_PROBE_COUNT(hcNoRemove);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }
    if (code != HC_ACTION)
    {
        BRIDGE_PROBE_COUNT(nonAction);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    shared = BridgeEnsureShared();
    if (shared == NULL)
    {
        return CallNextHookEx(NULL, code, wParam, lParam);
    }
    if (InterlockedCompareExchange(&shared->enabled, 0, 0) == 0)
    {
        BRIDGE_PROBE_COUNT(disabled);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    mouse = (MOUSEHOOKSTRUCT *)lParam;
    if (mouse == NULL)
    {
        BRIDGE_PROBE_COUNT(nullMouse);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    if (BridgeGetOtherButtonChange((UINT)wParam, mouse, &otherButton, &otherButtonDown))
    {
        if (!BridgeTryLock(shared))
        {
            BRIDGE_PROBE_COUNT(lockFailure);
            return CallNextHookEx(NULL, code, wParam, lParam);
        }
        if (otherButtonDown) shared->otherButtons |= otherButton;
        else shared->otherButtons &= ~otherButton;
        BridgeUnlock(shared);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    tick = GetTickCount();
    ownerPid = (DWORD)shared->ownerPid;
    ownerRoot = (HWND)(ULONG_PTR)shared->ownerHwnd;

    if (!BridgeIsRootTarget(mouse->hwnd, &targetRoot, &targetPid, &targetTid))
    {
        BRIDGE_PROBE_COUNT(targetLookupFailure);
        BRIDGE_PROBE_SET(lastTargetPid, 0);
        BRIDGE_PROBE_SET(lastTargetTid, 0);
        BRIDGE_PROBE_SET(lastCurrentPid, 0);
        BRIDGE_PROBE_SET(lastCurrentTid, 0);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }
    currentPid = GetCurrentProcessId();
    currentTid = GetCurrentThreadId();
    sourceContext = targetPid == currentPid && targetTid == currentTid;
    BRIDGE_PROBE_SET(lastTargetPid, targetPid);
    BRIDGE_PROBE_SET(lastTargetTid, targetTid);
    BRIDGE_PROBE_SET(lastCurrentPid, currentPid);
    BRIDGE_PROBE_SET(lastCurrentTid, currentTid);
    if (sourceContext)
    {
        BRIDGE_PROBE_COUNT(contextAccepted);
    }
    else
    {
        BRIDGE_PROBE_COUNT(contextSkipped);
#if !defined(_WIN64)
        return CallNextHookEx(NULL, code, wParam, lParam);
#else
        if (currentPid != ownerPid || targetPid == ownerPid)
        {
            return CallNextHookEx(NULL, code, wParam, lParam);
        }
#endif
    }

    if (wParam == WM_MOUSEMOVE || wParam == WM_NCMOUSEMOVE)
    {
        if (!BridgeTryLock(shared))
        {
            BRIDGE_PROBE_COUNT(lockFailure);
            return CallNextHookEx(NULL, code, wParam, lParam);
        }
        BridgeStateOnMove(&shared->state, mouse->pt.x, mouse->pt.y);
        BridgeUnlock(shared);
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    if (BridgeIsRightDown((UINT)wParam))
    {
        BRIDGE_PROBE_COUNT(rightDown);
        if (!BridgeTryLock(shared))
        {
            BRIDGE_PROBE_COUNT(lockFailure);
            return CallNextHookEx(NULL, code, wParam, lParam);
        }
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
        if (candidateStarted)
        {
            BRIDGE_PROBE_COUNT(candidate);
            BridgePostProbe(shared, BRIDGE_PROBE_CONTEXT, sourceContext ? 1 : 2);
            BridgePostProbe(shared, BRIDGE_PROBE_RIGHT_SEEN, 0);
        }
        return CallNextHookEx(NULL, code, wParam, lParam);
    }

    if (BridgeIsRightUp((UINT)wParam) || BridgeIsLeftUp((UINT)wParam))
    {
        BRIDGE_PROBE_COUNT(up);
        LONG button = BridgeIsRightUp((UINT)wParam)
            ? (LONG)BRIDGE_BUTTON_RIGHT
            : (LONG)BRIDGE_BUTTON_LEFT;
        if (!BridgeTryLock(shared))
        {
            BRIDGE_PROBE_COUNT(lockFailure);
            return CallNextHookEx(NULL, code, wParam, lParam);
        }
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
        BRIDGE_PROBE_COUNT(leftDown);
        if (targetPid != ownerPid)
        {
            probeLeft = TRUE;
            sourceForeground = GetForegroundWindow();
        }
        if (!BridgeTryLock(shared))
        {
            BRIDGE_PROBE_COUNT(lockFailure);
            return CallNextHookEx(NULL, code, wParam, lParam);
        }
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
            queuedRightDown = hadCandidate && shared->state.rightDownSeen &&
                shared->state.targetRoot == (BRIDGE_U64)(ULONG_PTR)targetRoot &&
                (DWORD)shared->state.targetPid == targetPid &&
                (DWORD)shared->state.targetTid == targetTid;
            queuedOtherButtons = shared->otherButtons != 0;
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
            BRIDGE_PROBE_COUNT(begin);
            sequence = shared->state.requestSequence;
        }
        BridgeUnlock(shared);
        if (supersededSequence != 0)
        {
            BridgePostOwnerMessage(shared, (DWORD)supersededSequence, BRIDGE_OWNER_COMPLETE);
        }
        if (probeLeft && decision != BRIDGE_DECISION_BEGIN)
        {
            BRIDGE_PROBE_COUNT(rejected);
            BridgePostProbe(shared, BRIDGE_PROBE_LEFT_REJECTED, rejectionDetail);
        }
        if (activeCancelled) return CallNextHookEx(NULL, code, wParam, lParam);
        if (decision != BRIDGE_DECISION_BEGIN) return CallNextHookEx(NULL, code, wParam, lParam);

        BridgePostProbe(shared, BRIDGE_PROBE_LEFT_CANDIDATE, (LPARAM)sequence);
        beginAccepted = BridgeBeginGesture(
            shared,
            (DWORD)sequence,
            targetPid,
            targetTid,
            targetRoot,
            sourceForeground,
            sourceContext,
            currentTid);
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
    shared->otherButtons = BridgeReadOtherButtons();
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
            shared->otherButtons = 0;
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
