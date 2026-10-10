#include "BridgeHook.h"

#define BRIDGE_LOCK_ATTEMPTS 4096L
#define BRIDGE_MAP_NAME_CAPACITY 96
#define BRIDGE_GESTURE_BUDGET_MS 75UL
#define BRIDGE_MENU_CANCEL_BUDGET_MS 25UL
#define BRIDGE_MENU_FOCUS_WAIT_MS 50UL
/* Nonzero still means "already attempted" to the pure guard. Value 2 is a
 * short-lived request for the source thread to renew our return permission. */
#define BRIDGE_FOCUS_RETURN_GRANT_PENDING 2
#define BRIDGE_PROBE_MESSAGE (WM_APP + 0x3A)
#define BRIDGE_PROBE_RIGHT_SEEN 10
#define BRIDGE_PROBE_LEFT_CANDIDATE 20
#define BRIDGE_PROBE_LEFT_REJECTED 21
#define BRIDGE_PROBE_SEND_FAILED 30
#define BRIDGE_PROBE_ACK 31
#define BRIDGE_PROBE_CAPTURE_FAILED 32
#define BRIDGE_PROBE_FOREGROUND 40
#define BRIDGE_PROBE_CONTEXT 41
#define BRIDGE_TRACE_MESSAGE (WM_APP + 0x3B)
#define BRIDGE_TRACE_WINDOW_MS 10000UL

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
    if (((USHORT)GetAsyncKeyState(VK_MBUTTON) & 0x8000U) != 0)
        buttons |= BRIDGE_OTHER_BUTTON_MIDDLE;
    if (((USHORT)GetAsyncKeyState(VK_XBUTTON1) & 0x8000U) != 0)
        buttons |= BRIDGE_OTHER_BUTTON_X1;
    if (((USHORT)GetAsyncKeyState(VK_XBUTTON2) & 0x8000U) != 0)
        buttons |= BRIDGE_OTHER_BUTTON_X2;
    return buttons;
}
#endif

static HINSTANCE g_module = NULL;
static HHOOK g_mouseHook = NULL;
static HHOOK g_callWndHook = NULL;
#if defined(_WIN64)
static HWINEVENTHOOK g_menuEventHook = NULL;
static BOOL g_menuEventCancelling = FALSE;
static HWND g_menuFocusWindow = NULL;
static DWORD g_menuFocusEventTick = 0;
#endif
#if defined(BRIDGE_INPUT_PROBE) && defined(_WIN64)
static HHOOK g_getMessageProbeHook = NULL;
static BOOL g_probeNextMove = FALSE;
#endif
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

/* Temporary passive ordering trace. Shared only by this preview DLL's copies. */
#pragma data_seg(".LLTRC")
static volatile LONG g_tracePid = 0;
static volatile LONG g_traceTid = 0;
static volatile LONG g_traceTick = 0;
#pragma data_seg()
#pragma comment(linker, "/SECTION:.LLTRC,RWS")

#if defined(_WIN64)
static BOOL BridgeTraceIsCurrent(DWORD pid, DWORD tid)
{
    DWORD traceTick = (DWORD)InterlockedCompareExchange(&g_traceTick, 0, 0);
    return pid == (DWORD)InterlockedCompareExchange(&g_tracePid, 0, 0) &&
        tid == (DWORD)InterlockedCompareExchange(&g_traceTid, 0, 0) &&
        (DWORD)(GetTickCount() - traceTick) <= BRIDGE_TRACE_WINDOW_MS;
}
#endif

static void BridgeStartTrace(DWORD pid, DWORD tid)
{
    InterlockedExchange(&g_traceTick, (LONG)GetTickCount());
    InterlockedExchange(&g_traceTid, (LONG)tid);
    InterlockedExchange(&g_tracePid, (LONG)pid);
}

static void BridgePostTrace(BRIDGE_SHARED *shared, UINT kind, UINT message, DWORD detail)
{
#if defined(_WIN64)
    HWND owner;
    ULONGLONG payload;
    if (shared == NULL || InterlockedCompareExchange(&shared->enabled, 0, 0) == 0) return;
    if (InterlockedCompareExchange(&g_tracePid, 0, 0) == 0 ||
        (DWORD)(GetTickCount() - (DWORD)InterlockedCompareExchange(&g_traceTick, 0, 0)) > BRIDGE_TRACE_WINDOW_MS ||
        (GetCurrentProcessId() != (DWORD)InterlockedCompareExchange(&g_tracePid, 0, 0) &&
            GetCurrentProcessId() != (DWORD)shared->ownerPid)) return;
    owner = (HWND)(ULONG_PTR)shared->ownerHwnd;
    payload = ((ULONGLONG)GetTickCount() << 32) | (ULONGLONG)detail;
    if (owner != NULL)
        PostMessageW(owner, BRIDGE_TRACE_MESSAGE,
            (WPARAM)((kind << 16) | (message & 0xffffU)), (LPARAM)payload);
#else
    (void)shared; (void)kind; (void)message; (void)detail;
#endif
}

#else
#define BridgePostProbe(shared, stage, detail) ((void)0)
#define BridgeStartTrace(pid, tid) ((void)0)
#define BridgePostTrace(shared, kind, message, detail) ((void)0)
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

#if defined(BRIDGE_INPUT_PROBE) && defined(_WIN64)
/* Read queued pointer/mouse paths only. Do not modify or consume any MSG. */
static LRESULT CALLBACK BridgeGetMessageProbeProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION && wParam == PM_REMOVE && lParam != 0 &&
        BridgeTraceIsCurrent(GetCurrentProcessId(),
            (DWORD)InterlockedCompareExchange(&g_traceTid, 0, 0)))
    {
        const MSG *event = (const MSG *)lParam;
        BOOL record = FALSE;
        switch (event->message)
        {
        case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_LBUTTONDOWN: case WM_LBUTTONUP:
        case WM_NCRBUTTONDOWN: case WM_NCRBUTTONUP: case WM_NCLBUTTONDOWN: case WM_NCLBUTTONUP:
        case WM_CONTEXTMENU: case WM_POINTERDOWN: case WM_POINTERUP: case WM_POINTERCAPTURECHANGED:
            record = TRUE;
            break;
        case WM_MOUSEMOVE: case WM_NCMOUSEMOVE: case WM_POINTERUPDATE:
            if (g_probeNextMove) { record = TRUE; g_probeNextMove = FALSE; }
            break;
        default:
            break;
        }
        if (event->message == WM_RBUTTONUP || event->message == WM_NCRBUTTONUP ||
            event->message == WM_POINTERUP) g_probeNextMove = TRUE;
        if (event->message == WM_RBUTTONDOWN || event->message == WM_NCRBUTTONDOWN ||
            event->message == WM_POINTERDOWN) g_probeNextMove = FALSE;
        if (record)
        {
            BRIDGE_SHARED *shared = BridgeEnsureShared();
            HWND root = NULL;
            BridgePostTrace(shared, 14, event->message, (DWORD)event->wParam);
            if (event->hwnd != NULL) root = GetAncestor(event->hwnd, GA_ROOT);
            BridgePostTrace(shared, 15, event->message, (DWORD)(ULONG_PTR)root);
            BridgePostTrace(shared, 16, event->message, GetCurrentThreadId());
        }
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}
#endif

#if defined(_WIN64)
static BOOL BridgeMenuEventTargetMatches(BRIDGE_SHARED *shared, HWND window,
    HWND expectedRoot, DWORD expectedPid, DWORD expectedTid, DWORD eventTick)
{
    HWND root = NULL;
    DWORD pid = 0;
    DWORD tid = 0;
    BOOL matched;
    BOOL visible;
    if (shared == NULL || InterlockedCompareExchange(&shared->enabled, 0, 0) == 0 ||
        !BridgeIsRootTarget(window, &root, &pid, &tid) || root != expectedRoot ||
        pid != expectedPid || tid != expectedTid || pid == (DWORD)shared->ownerPid)
        return FALSE;
    visible = IsWindowVisible((HWND)(ULONG_PTR)shared->ownerHwnd);
    if (!BridgeTryLock(shared)) return FALSE;
    matched = BridgeMenuGuardMatchesEvent(&shared->menuGuard,
        (BRIDGE_U64)(ULONG_PTR)root, pid, tid, visible, eventTick, GetTickCount());
    BridgeUnlock(shared);
    return matched;
}

/* Owner-thread event delivery covers menu frameworks which do not emit
 * WM_ENTERMENULOOP/WM_INITMENUPOPUP. Cancel only the accepted source, never
 * close/destroy a popup or synthesize escape/click/button releases. */
static void CALLBACK BridgeMenuEventProc(HWINEVENTHOOK hook, DWORD event,
    HWND window, LONG objectId, LONG childId, DWORD eventTid, DWORD eventTick)
{
    BRIDGE_SHARED *shared;
    HWND root = NULL;
    DWORD pid = 0;
    DWORD tid = 0;
    DWORD started;
    DWORD elapsed;
    DWORD_PTR result = 0;
    LRESULT sent;
    (void)hook; (void)objectId; (void)childId;
    if ((event != EVENT_SYSTEM_MENUSTART && event != EVENT_SYSTEM_MENUPOPUPSTART) ||
        window == NULL || g_menuEventCancelling ||
        !BridgeIsRootTarget(window, &root, &pid, &tid) || tid != eventTid)
        return;
    shared = BridgeEnsureShared();
    if (!BridgeMenuEventTargetMatches(shared, window, root, pid, tid, eventTick)) return;
    started = GetTickCount();
    g_menuEventCancelling = TRUE;
    sent = SendMessageTimeoutW(window, WM_CANCELMODE, 0, 0,
        SMTO_ABORTIFHUNG | SMTO_BLOCK, BRIDGE_MENU_CANCEL_BUDGET_MS, &result);
    BridgePostTrace(shared, 17, (UINT)event, sent != 0 ? 1UL : 0UL);
    elapsed = GetTickCount() - started;
    if (window != root && elapsed < BRIDGE_MENU_CANCEL_BUDGET_MS &&
        BridgeMenuEventTargetMatches(shared, root, root, pid, tid, eventTick))
    {
        sent = SendMessageTimeoutW(root, WM_CANCELMODE, 0, 0,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, BRIDGE_MENU_CANCEL_BUDGET_MS - elapsed, &result);
        BridgePostTrace(shared, 18, (UINT)event, sent != 0 ? 1UL : 0UL);
    }
    if (event == EVENT_SYSTEM_MENUPOPUPSTART &&
        BridgeMenuEventTargetMatches(shared, window, root, pid, tid, eventTick))
    {
        /* Synchronous owner-thread scope lets WPF suspend only our temporary
         * deactivation. The export cannot act without this live event request. */
        DWORD sequence = 0;
        if (BridgeTryLock(shared))
        {
            sequence = (DWORD)shared->state.sequence;
            BridgeUnlock(shared);
        }
        g_menuFocusWindow = window;
        g_menuFocusEventTick = eventTick;
        if (sequence != 0)
            SendMessageW((HWND)(ULONG_PTR)shared->ownerHwnd, BRIDGE_OWNER_MESSAGE,
                (WPARAM)sequence, BRIDGE_OWNER_MENU_FOCUS);
        g_menuFocusWindow = NULL;
        g_menuFocusEventTick = 0;
    }
    g_menuEventCancelling = FALSE;
}
#endif

UINT __cdecl BridgeRestoreMenuFocus(void)
{
#if defined(_WIN64)
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    HWND root = NULL;
    HWND focusRoot = NULL;
    HWND owner;
    HWND foreground;
    DWORD pid = 0;
    DWORD tid = 0;
    DWORD focusPid = 0;
    DWORD focusTid = 0;
    DWORD ownerTid;
    DWORD ownerPid = 0;
    DWORD_PTR ignored = 0;
    GUITHREADINFO info;
    LASTINPUTINFO inputBefore;
    LASTINPUTINFO inputAfter;
    BOOL take;
    BOOL buttonsDown;
    BOOL grantPending = FALSE;
    UINT flags = 0;
    if (!g_menuEventCancelling || g_menuFocusWindow == NULL || shared == NULL ||
        (DWORD)shared->ownerPid != GetCurrentProcessId() ||
        !BridgeIsRootTarget(g_menuFocusWindow, &root, &pid, &tid) ||
        !IsWindowVisible(root)) return 0;
    owner = (HWND)(ULONG_PTR)shared->ownerHwnd;
    ownerTid = GetWindowThreadProcessId(owner, &ownerPid);
    if (ownerTid != GetCurrentThreadId() || ownerPid != GetCurrentProcessId()) return 0;
    BridgeZeroMemory((volatile unsigned char *)&info, sizeof(info));
    info.cbSize = (DWORD)sizeof(info);
    if (!GetGUIThreadInfo(tid, &info)) return 0;
    buttonsDown = ((USHORT)GetAsyncKeyState(VK_RBUTTON) & 0x8000U) != 0 ||
        ((USHORT)GetAsyncKeyState(VK_LBUTTON) & 0x8000U) != 0 || BridgeReadOtherButtons() != 0;
    inputBefore.cbSize = (UINT)sizeof(inputBefore);
    if (!GetLastInputInfo(&inputBefore) || !BridgeTryLock(shared)) return 0;
    take = BridgeMenuGuardTakeFocusTransfer(&shared->menuGuard,
        (BRIDGE_U64)(ULONG_PTR)root, pid, tid, IsWindowVisible(owner),
        GetForegroundWindow() == owner, buttonsDown,
        (info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE | GUI_SYSTEMMENUMODE)) != 0,
        g_menuFocusEventTick, GetTickCount());
    BridgeUnlock(shared);
    if (!take) return 0;
    /* Keep our foreground permission for the return leg, rather than merging
     * input queues, faking activation messages, or changing system settings. */
    if (!AllowSetForegroundWindow(ownerPid)) return 0;
    flags |= 8U;
    if (!BridgeMenuEventTargetMatches(shared, g_menuFocusWindow, root, pid, tid,
            g_menuFocusEventTick) || GetForegroundWindow() != owner) return flags;
    if (!BridgeTryLock(shared)) return flags;
    if (shared->menuGuard.focusAttempted == 1 &&
        BridgeMenuGuardMatchesEvent(&shared->menuGuard,
            (BRIDGE_U64)(ULONG_PTR)root, pid, tid, IsWindowVisible(owner),
            g_menuFocusEventTick, GetTickCount()))
    {
        shared->menuGuard.focusAttempted = BRIDGE_FOCUS_RETURN_GRANT_PENDING;
        grantPending = TRUE;
    }
    BridgeUnlock(shared);
    if (!grantPending) return flags;
    flags |= 16U;
    SetForegroundWindow(root);
    /* Cross-queue activation is asynchronous. A bounded harmless message
     * waits for the source to process that activation before the return. */
    if (SendMessageTimeoutW(root, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK,
            BRIDGE_MENU_FOCUS_WAIT_MS, &ignored) != 0 && GetForegroundWindow() == root)
    {
        flags |= 1U;
        BridgeZeroMemory((volatile unsigned char *)&info, sizeof(info));
        info.cbSize = (DWORD)sizeof(info);
        if (GetGUIThreadInfo(tid, &info) &&
            BridgeIsRootTarget(info.hwndFocus, &focusRoot, &focusPid, &focusTid) &&
            focusRoot == root && focusPid == pid && focusTid == tid) flags |= 2U;
    }
    /* Never leave a live grant request behind if the state lock is busy.
     * CAS preserves a cleared/rearmed guard and the source's consumed request. */
    InterlockedCompareExchange(&shared->menuGuard.focusAttempted,
        1, BRIDGE_FOCUS_RETURN_GRANT_PENDING);
    inputAfter.cbSize = (UINT)sizeof(inputAfter);
    foreground = GetForegroundWindow();
    if (GetLastInputInfo(&inputAfter) && inputAfter.dwTime == inputBefore.dwTime &&
        BridgeMenuEventTargetMatches(shared, g_menuFocusWindow, root, pid, tid,
            g_menuFocusEventTick) && IsWindowVisible(owner) &&
        (foreground == owner || foreground == root))
    {
        /* Also supersede a timed-out asynchronous source activation request. */
        if (SetForegroundWindow(owner)) flags |= 64U;
        if (GetForegroundWindow() == owner) flags |= 4U;
    }
    else flags |= 32U; /* New input, hide, stale request or unrelated foreground. */
    BridgePostTrace(shared, 19, EVENT_SYSTEM_MENUPOPUPSTART, flags);
    return flags;
#else
    return 0;
#endif
}

static LRESULT CALLBACK BridgeCallWndProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION && lParam != 0)
    {
        const CWPSTRUCT *event = (const CWPSTRUCT *)lParam;
        BRIDGE_SHARED *shared;
#if defined(_WIN64)
        if (event->message == WM_NULL)
        {
            HWND root = NULL;
            DWORD pid = 0;
            DWORD tid = 0;
            BOOL grantReturn = FALSE;
            shared = BridgeEnsureShared();
            /* Only the live focus round-trip can arm this request. Grant from
             * the actual foreground source thread after activation is processed.
             * This neither activates a window nor consumes/modifies WM_NULL. */
            if (shared != NULL && InterlockedCompareExchange(&shared->enabled, 0, 0) != 0 &&
                BridgeIsRootTarget(event->hwnd, &root, &pid, &tid) && event->hwnd == root &&
                pid != (DWORD)shared->ownerPid && pid == GetCurrentProcessId() &&
                tid == GetCurrentThreadId() && GetForegroundWindow() == root &&
                ((USHORT)GetAsyncKeyState(VK_RBUTTON) & 0x8000U) == 0 &&
                ((USHORT)GetAsyncKeyState(VK_LBUTTON) & 0x8000U) == 0 &&
                BridgeReadOtherButtons() == 0 && BridgeTryLock(shared))
            {
                if (shared->menuGuard.focusAttempted == BRIDGE_FOCUS_RETURN_GRANT_PENDING &&
                    BridgeMenuGuardMatches(&shared->menuGuard,
                        (BRIDGE_U64)(ULONG_PTR)root, pid, tid,
                        IsWindowVisible((HWND)(ULONG_PTR)shared->ownerHwnd), GetTickCount()))
                {
                    shared->menuGuard.focusAttempted = 1;
                    grantReturn = TRUE;
                }
                BridgeUnlock(shared);
            }
            if (grantReturn)
            {
                BOOL granted = AllowSetForegroundWindow((DWORD)shared->ownerPid);
                BridgePostTrace(shared, 23, WM_NULL, granted ? 1UL : 0UL);
            }
        }
#endif
#if defined(BRIDGE_INPUT_PROBE) && defined(_WIN64)
        switch (event->message)
        {
        case WM_RBUTTONDOWN: case WM_RBUTTONUP:
        case WM_LBUTTONDOWN: case WM_LBUTTONUP:
        case WM_CONTEXTMENU: case WM_CANCELMODE: case WM_CAPTURECHANGED:
        case WM_ENTERMENULOOP: case WM_EXITMENULOOP: case WM_INITMENUPOPUP:
            if (BridgeTraceIsCurrent(GetCurrentProcessId(), GetCurrentThreadId()))
                BridgePostTrace(BridgeEnsureShared(), 1, event->message, GetCurrentProcessId());
            break;
        default:
            break;
        }
#endif
        /* Standard keyboard context menus are a new user action too. */
        if (event->message == WM_CONTEXTMENU && event->lParam == (LPARAM)-1)
        {
            HWND root = NULL;
            DWORD pid = 0;
            DWORD tid = 0;
            shared = BridgeEnsureShared();
            if (shared != NULL && BridgeIsRootTarget(event->hwnd, &root, &pid, &tid) &&
                BridgeTryLock(shared))
            {
                if (shared->menuGuard.targetRoot == (BRIDGE_U64)(ULONG_PTR)root &&
                    (DWORD)shared->menuGuard.targetPid == pid &&
                    (DWORD)shared->menuGuard.targetTid == tid)
                    BridgeMenuGuardClear(&shared->menuGuard);
                BridgeUnlock(shared);
            }
        }
        if ((event->message == WM_ENTERMENULOOP && event->wParam != 0) ||
            event->message == WM_INITMENUPOPUP)
        {
            HWND root = NULL;
            DWORD pid = 0;
            DWORD tid = 0;
            BOOL cancelMenu = FALSE;
            BOOL ownerVisible;
#if defined(BRIDGE_INPUT_PROBE)
            BOOL probeMenu = FALSE;
            DWORD detail = 0;
#endif
            shared = BridgeEnsureShared();
            ownerVisible = shared != NULL && IsWindowVisible((HWND)(ULONG_PTR)shared->ownerHwnd);
            if (shared != NULL &&
                InterlockedCompareExchange(&shared->enabled, 0, 0) != 0 &&
                BridgeIsRootTarget(event->hwnd, &root, &pid, &tid) &&
                pid != (DWORD)shared->ownerPid &&
                pid == GetCurrentProcessId() && tid == GetCurrentThreadId() &&
                BridgeTryLock(shared))
            {
                cancelMenu = BridgeMenuGuardMatches(&shared->menuGuard,
                    (BRIDGE_U64)(ULONG_PTR)root, pid, tid, ownerVisible, GetTickCount());
#if defined(BRIDGE_INPUT_PROBE)
                if (shared->menuGuard.valid && (DWORD)shared->menuGuard.targetPid == pid)
                {
                    probeMenu = TRUE;
                    detail = (cancelMenu ? 1UL : 0UL) |
                        (ownerVisible ? 2UL : 0UL) |
                        (shared->menuGuard.targetRoot == (BRIDGE_U64)(ULONG_PTR)root ? 4UL : 0UL) |
                        ((DWORD)shared->menuGuard.targetTid == tid ? 8UL : 0UL);
                }
#endif
                BridgeUnlock(shared);
            }
#if defined(BRIDGE_INPUT_PROBE)
            if (probeMenu)
            {
                BridgePostTrace(shared, 8, event->message, detail);
                BridgePostTrace(shared, 9, event->message, (DWORD)(ULONG_PTR)root);
                BridgePostTrace(shared, 10, event->message, tid);
            }
#endif
            if (cancelMenu)
            {
                BOOL ended = EndMenu();
                BridgePostTrace(shared, 7, event->message, ended ? 1UL : 0UL);
            }
        }
    }
    /* Preserve message delivery and hook-chain results. EndMenu is scoped
       separately to the accepted gesture's original thread and window. */
    return CallNextHookEx(NULL, code, wParam, lParam);
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
        DWORD remaining;
        DWORD_PTR cancelResult = 0;
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
        remaining = BridgeRemainingGestureBudget(startTick);
        if (remaining == 0)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_BUDGET;
            return FALSE;
        }
        /* ReleaseCapture alone can complete a pending right-click gesture.
           Ask its owning window to cancel the operation before accepting it. */
        if (SendMessageTimeoutW(capture, WM_CANCELMODE, 0, 0,
                SMTO_ABORTIFHUNG, remaining, &cancelResult) == 0)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_CANCEL;
            return FALSE;
        }
        if (GetCapture() != NULL)
        {
            if (failureDetail != NULL) *failureDetail = BRIDGE_PROBE_CAPTURE_FAILURE_REMAINS;
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

/* Mouse delivery identifies the gesture source. A background window (or a
 * window reached just after our Hide) need not own the foreground. Keep the
 * delivery target alive and reject a foreground change during cancellation. */
static BOOL BridgeGestureSourceIsCurrent(HWND sourceWindow, HWND sourceRoot,
    DWORD sourcePid, DWORD sourceTid, HWND sourceForeground)
{
    HWND root = NULL;
    DWORD pid = 0;
    DWORD tid = 0;
    return GetForegroundWindow() == sourceForeground &&
        BridgeIsRootTarget(sourceWindow, &root, &pid, &tid) &&
        root == sourceRoot && pid == sourcePid && tid == sourceTid &&
        IsWindowVisible(root);
}

static BOOL BridgeBeginGesture(
    BRIDGE_SHARED *shared,
    DWORD sequence,
    DWORD sourcePid,
    DWORD sourceTid,
    HWND sourceRoot,
    HWND sourceWindow,
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
    if (foreground != NULL) GetWindowThreadProcessId(foreground, &foregroundPid);
    if (foregroundPid != sourcePid)
    {
        BridgePostTrace(shared, 20, 0, (DWORD)(ULONG_PTR)foreground);
        BridgePostTrace(shared, 21, 0, foregroundPid);
        BridgePostTrace(shared, 22, 0, (DWORD)(ULONG_PTR)sourceRoot);
    }
    if (owner == NULL || ownerPid == 0 ||
        !BridgeGestureSourceIsCurrent(sourceWindow, sourceRoot, sourcePid, sourceTid, sourceForeground))
    {
        BridgePostProbe(shared, BRIDGE_PROBE_SEND_FAILED, 1);
        BridgeRejectSequence(shared, sequence);
        return FALSE;
    }

    if (sourceContext)
    {
        AllowSetForegroundWindow(ownerPid);
    }

    BridgePostTrace(shared, 4, 0, sourcePid);
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
    BridgePostTrace(shared, 5, 0, sourcePid);

    if (!BridgeGestureSourceIsCurrent(sourceWindow, sourceRoot, sourcePid, sourceTid, sourceForeground))
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
            BridgeMenuGuardArm(&shared->menuGuard,
                (BRIDGE_U64)(ULONG_PTR)sourceRoot, sourcePid, sourceTid,
                uiResult == 1, tick);
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
    BridgePostTrace(shared, 11, 0, (DWORD)(ULONG_PTR)sourceRoot);
    BridgePostTrace(shared, 12, 0, sourceTid);

    if (uiResult == 1)
    {
        foreground = GetForegroundWindow();
        if (foreground == sourceForeground)
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
#if defined(BRIDGE_INPUT_PROBE) && defined(_WIN64)
        if (lParam != 0 &&
            (BridgeIsRightDown((UINT)wParam) || BridgeIsLeftDown((UINT)wParam) ||
                BridgeIsRightUp((UINT)wParam) || BridgeIsLeftUp((UINT)wParam)) &&
            BridgeTraceIsCurrent(GetCurrentProcessId(), GetCurrentThreadId()))
            BridgePostTrace(BridgeEnsureShared(), 2, (UINT)wParam, GetCurrentProcessId());
#endif
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
            BridgeStartTrace(targetPid, targetTid);
            BridgePostTrace(shared, 3, (UINT)wParam, targetPid);
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
        BridgePostTrace(shared, 6, (UINT)wParam,
            ((DWORD)decision & 0xffUL) | (targetMatches ? 0x100UL : 0UL) |
                (sourceContext ? 0x200UL : 0UL));
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
        BridgePostTrace(shared, 3, (UINT)wParam, targetPid);
        beginAccepted = BridgeBeginGesture(
            shared,
            (DWORD)sequence,
            targetPid,
            targetTid,
            targetRoot,
            mouse->hwnd,
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

    g_callWndHook = SetWindowsHookExW(WH_CALLWNDPROC, BridgeCallWndProc, g_module, 0);
    if (g_callWndHook == NULL)
    {
        DWORD hookError = GetLastError();
        UnhookWindowsHookEx(g_mouseHook);
        g_mouseHook = NULL;
        if ((DWORD)currentPid == ownerPid)
        {
            InterlockedExchange(&shared->enabled, 0);
            shared->statusBits = 0;
        }
        BridgeCloseLocalMapping();
        g_cfgOwnerPid = 0;
        g_cfgOwnerHwnd = 0;
        SetLastError(hookError);
        return FALSE;
    }

    if (!BridgeTryLock(shared))
    {
        UnhookWindowsHookEx(g_callWndHook);
        g_callWndHook = NULL;
        UnhookWindowsHookEx(g_mouseHook);
        g_mouseHook = NULL;
        BridgeCloseLocalMapping();
        return FALSE;
    }
    if ((DWORD)currentPid == ownerPid)
    {
        shared->statusBits |= (LONG)BRIDGE_STATUS_HOOK64;
        shared->statusBits |= (LONG)BRIDGE_STATUS_MENU_HOOK;
        shared->statusBits |= (LONG)BRIDGE_STATUS_ENABLED;
        InterlockedExchange(&shared->enabled, 1);
    }
    else
    {
        shared->statusBits |= (LONG)BRIDGE_STATUS_HOOK32;
    }
    BridgeUnlock(shared);
    BridgePostProbe(shared, 54, 1);
#if defined(_WIN64)
    g_menuEventHook = SetWinEventHook(EVENT_SYSTEM_MENUSTART, EVENT_SYSTEM_MENUPOPUPSTART,
        NULL, BridgeMenuEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    if (g_menuEventHook == NULL)
    {
        DWORD eventError = GetLastError();
        BridgeStop();
        SetLastError(eventError);
        return FALSE;
    }
    InterlockedOr(&shared->statusBits, (LONG)BRIDGE_STATUS_MENU_EVENT_HOOK);
    BridgePostProbe(shared, 56, 1);
#endif
#if defined(BRIDGE_INPUT_PROBE) && defined(_WIN64)
    g_getMessageProbeHook = SetWindowsHookExW(WH_GETMESSAGE, BridgeGetMessageProbeProc, g_module, 0);
    BridgePostProbe(shared, 55, g_getMessageProbeHook != NULL ? 1 : 0);
#endif
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
                (LONG)BRIDGE_STATUS_MENU_HOOK |
                (LONG)BRIDGE_STATUS_MENU_EVENT_HOOK |
                (LONG)BRIDGE_STATUS_ENABLED | (LONG)BRIDGE_STATUS_FOREGROUND_PROMOTED);
            BridgeStateCancel(&shared->state);
            BridgeMenuGuardClear(&shared->menuGuard);
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
    if (g_callWndHook != NULL)
    {
        UnhookWindowsHookEx(g_callWndHook);
        g_callWndHook = NULL;
    }
#if defined(_WIN64)
    if (g_menuEventHook != NULL)
    {
        UnhookWinEvent(g_menuEventHook);
        g_menuEventHook = NULL;
    }
    g_menuEventCancelling = FALSE;
    g_menuFocusWindow = NULL;
    g_menuFocusEventTick = 0;
#endif
#if defined(BRIDGE_INPUT_PROBE) && defined(_WIN64)
    if (g_getMessageProbeHook != NULL)
    {
        UnhookWindowsHookEx(g_getMessageProbeHook);
        g_getMessageProbeHook = NULL;
    }
    g_probeNextMove = FALSE;
#endif
#if defined(BRIDGE_INPUT_PROBE) && defined(_WIN64)
    if (isOwner)
    {
        InterlockedExchange(&g_tracePid, 0);
        InterlockedExchange(&g_traceTid, 0);
        InterlockedExchange(&g_traceTick, 0);
    }
#endif
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

void __cdecl BridgeClearMenuGuard(void)
{
    BRIDGE_SHARED *shared = BridgeEnsureShared();
    if (shared == NULL || !BridgeTryLock(shared)) return;
    BridgeMenuGuardClear(&shared->menuGuard);
    BridgeUnlock(shared);
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
