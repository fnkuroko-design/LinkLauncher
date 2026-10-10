#include "BridgeHook.h"

typedef BOOL (__cdecl *BRIDGE_INSTALL_FN)(HWND, DWORD);
typedef void (__cdecl *BRIDGE_STOP_FN)(void);

static BOOL BridgeIsSpace(WCHAR value)
{
    return value == L' ' || value == L'\t' || value == L'\r' || value == L'\n';
}

static BOOL BridgeReadToken(LPCWSTR *cursor, WCHAR *token, SIZE_T capacity)
{
    SIZE_T count = 0;
    BOOL quoted = FALSE;
    LPCWSTR current;
    if (cursor == NULL || *cursor == NULL || token == NULL || capacity == 0) return FALSE;
    current = *cursor;
    while (BridgeIsSpace(*current)) ++current;
    if (*current == L'\0') return FALSE;
    if (*current == L'"')
    {
        quoted = TRUE;
        ++current;
    }
    while (*current != L'\0')
    {
        if (quoted ? (*current == L'"') : BridgeIsSpace(*current)) break;
        if (count + 1 >= capacity) return FALSE;
        token[count++] = *current++;
    }
    if (quoted)
    {
        if (*current != L'"') return FALSE;
        ++current;
    }
    if (count == 0) return FALSE;
    token[count] = L'\0';
    *cursor = current;
    return TRUE;
}

static BOOL BridgeParseHex(LPCWSTR text, ULONGLONG *value)
{
    ULONGLONG result = 0;
    SIZE_T digits = 0;
    if (text == NULL || value == NULL) return FALSE;
    if (text[0] == L'0' && (text[1] == L'x' || text[1] == L'X')) text += 2;
    while (*text != L'\0')
    {
        WCHAR ch = *text++;
        DWORD digit;
        if (ch >= L'0' && ch <= L'9') digit = (DWORD)(ch - L'0');
        else if (ch >= L'a' && ch <= L'f') digit = (DWORD)(ch - L'a' + 10);
        else if (ch >= L'A' && ch <= L'F') digit = (DWORD)(ch - L'A' + 10);
        else return FALSE;
        if (digits >= 16) return FALSE;
        result = (result << 4) | digit;
        ++digits;
    }
    if (digits == 0) return FALSE;
    *value = result;
    return TRUE;
}

static BOOL BridgeParseDecimal(LPCWSTR text, DWORD *value)
{
    DWORD result = 0;
    SIZE_T digits = 0;
    if (text == NULL || value == NULL) return FALSE;
    while (*text != L'\0')
    {
        DWORD digit;
        if (*text < L'0' || *text > L'9') return FALSE;
        digit = (DWORD)(*text++ - L'0');
        if (result > (0xffffffffUL - digit) / 10UL) return FALSE;
        result = result * 10UL + digit;
        ++digits;
    }
    if (digits == 0 || result == 0) return FALSE;
    *value = result;
    return TRUE;
}

static BOOL BridgeParseArguments(ULONGLONG *ownerHwnd, DWORD *ownerPid)
{
    WCHAR executable[1024];
    WCHAR hwndText[32];
    WCHAR pidText[32];
    LPCWSTR commandLine = GetCommandLineW();
    if (commandLine == NULL || ownerHwnd == NULL || ownerPid == NULL) return FALSE;
    if (!BridgeReadToken(&commandLine, executable, sizeof(executable) / sizeof(executable[0])) ||
        !BridgeReadToken(&commandLine, hwndText, sizeof(hwndText) / sizeof(hwndText[0])) ||
        !BridgeReadToken(&commandLine, pidText, sizeof(pidText) / sizeof(pidText[0])))
    {
        return FALSE;
    }
    return BridgeParseHex(hwndText, ownerHwnd) && BridgeParseDecimal(pidText, ownerPid);
}

static BOOL BridgeMakeDllPath(WCHAR *path, SIZE_T capacity)
{
    static const WCHAR filename[] = L"LinkLauncher.MouseHook.x86.dll";
    DWORD length;
    SIZE_T i;
    SIZE_T lastSeparator = (SIZE_T)-1;
    if (path == NULL || capacity == 0) return FALSE;
    length = GetModuleFileNameW(NULL, path, (DWORD)capacity);
    if (length == 0 || length >= capacity) return FALSE;
    for (i = 0; i < length; ++i)
    {
        if (path[i] == L'\\' || path[i] == L'/') lastSeparator = i;
    }
    if (lastSeparator == (SIZE_T)-1) return FALSE;
    if (lastSeparator + 1 + (sizeof(filename) / sizeof(filename[0])) > capacity) return FALSE;
    for (i = 0; i < sizeof(filename) / sizeof(filename[0]); ++i)
    {
        path[lastSeparator + 1 + i] = filename[i];
    }
    return TRUE;
}

void __cdecl BridgeHostEntry(void)
{
    ULONGLONG ownerHwnd = 0;
    DWORD ownerPid = 0;
    HANDLE parentProcess = NULL;
    HMODULE hookModule = NULL;
    BRIDGE_INSTALL_FN installBridge = NULL;
    BRIDGE_STOP_FN stopBridge = NULL;
    WCHAR dllPath[1024];
    DWORD waitResult;
    MSG message;
    BOOL installed = FALSE;

    if (!BridgeParseArguments(&ownerHwnd, &ownerPid) || ownerHwnd == 0 ||
        ownerHwnd > 0xffffffffULL || !BridgeMakeDllPath(dllPath, sizeof(dllPath) / sizeof(dllPath[0])))
    {
        ExitProcess(2);
    }
    parentProcess = OpenProcess(SYNCHRONIZE, FALSE, ownerPid);
    if (parentProcess == NULL)
    {
        ExitProcess(3);
    }
    if (WaitForSingleObject(parentProcess, 0) == WAIT_OBJECT_0)
    {
        CloseHandle(parentProcess);
        ExitProcess(0);
    }

    hookModule = LoadLibraryW(dllPath);
    if (hookModule == NULL)
    {
        CloseHandle(parentProcess);
        ExitProcess(4);
    }
    installBridge = (BRIDGE_INSTALL_FN)GetProcAddress(hookModule, "BridgeInstall");
    stopBridge = (BRIDGE_STOP_FN)GetProcAddress(hookModule, "BridgeStop");
    if (installBridge == NULL || stopBridge == NULL)
    {
        FreeLibrary(hookModule);
        CloseHandle(parentProcess);
        ExitProcess(5);
    }
    installed = installBridge((HWND)(ULONG_PTR)ownerHwnd, ownerPid);
    if (!installed)
    {
        stopBridge();
        FreeLibrary(hookModule);
        CloseHandle(parentProcess);
        ExitProcess(6);
    }

    for (;;)
    {
        waitResult = MsgWaitForMultipleObjects(1, &parentProcess, FALSE, INFINITE, QS_ALLINPUT);
        if (waitResult == WAIT_OBJECT_0 || waitResult == WAIT_FAILED) break;
        if (waitResult == WAIT_OBJECT_0 + 1)
        {
            while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT) goto bridge_host_stop;
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
    }

bridge_host_stop:
    stopBridge();
    FreeLibrary(hookModule);
    CloseHandle(parentProcess);
    ExitProcess(0);
}
