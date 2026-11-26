// AST's items in Windows 11's File Explorer right-click menu: "Pin to taskbar (AST)" and "Pin to Start (AST)".
// Windows 11 only shows items from packaged apps in its short menu, so AST registers a small package that points
// File Explorer here. Choosing an item starts AST.exe, from the same folder as this file, with the file to pin.
// AST.exe does the pinning, exactly as for the items in the full menu (Shift+right-click).

#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <new>
#include <string>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

// Must match the ids in the package manifest.
static const CLSID CLSID_PinTaskbar = { 0xf745421b, 0x4cc8, 0x4e78, { 0xb5, 0x11, 0xbc, 0xec, 0x0e, 0x45, 0xec, 0xa8 } };
static const CLSID CLSID_PinStart = { 0xa97f2428, 0x9da9, 0x4c86, { 0xb0, 0x5a, 0xf7, 0xd8, 0x58, 0x38, 0x46, 0xae } };

static HMODULE g_module;
static long g_objects;
static long g_locks;

static std::wstring ModuleFolder()
{
    wchar_t path[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    if (n == 0 || n >= ARRAYSIZE(path)) return L"";
    PathRemoveFileSpecW(path);
    return path;
}

static std::wstring AstExe() { return ModuleFolder() + L"\\AST.exe"; }

class PinCommand : public IExplorerCommand
{
public:
    PinCommand(const wchar_t* title, const wchar_t* argument) : _refs(1), _title(title), _argument(argument)
    {
        InterlockedIncrement(&g_objects);
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&_refs); }

    IFACEMETHODIMP_(ULONG) Release()
    {
        long refs = InterlockedDecrement(&_refs);
        if (refs == 0) delete this;
        return refs;
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) { return SHStrDupW(_title, name); }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon)
    {
        std::wstring path = AstExe() + L",0";
        return SHStrDupW(path.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) { *tip = nullptr; return E_NOTIMPL; }

    IFACEMETHODIMP GetCanonicalName(GUID* name) { *name = GUID_NULL; return S_OK; }

    // Hidden if AST has been removed but Windows still has the package.
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state)
    {
        *state = PathFileExistsW(AstExe().c_str()) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*)
    {
        if (!items) return S_OK;
        DWORD count = 0;
        if (FAILED(items->GetCount(&count))) return S_OK;
        std::wstring exe = AstExe();
        for (DWORD i = 0; i < count; i++)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            LPWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
            {
                std::wstring command = L"\"" + exe + L"\" " + _argument + L" \"" + path + L"\"";
                STARTUPINFOW si = { sizeof(si) };
                PROCESS_INFORMATION pi = {};
                if (CreateProcessW(exe.c_str(), &command[0], nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
                {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
                CoTaskMemFree(path);
            }
            item->Release();
        }
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) { *flags = ECF_DEFAULT; return S_OK; }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) { *commands = nullptr; return E_NOTIMPL; }

private:
    ~PinCommand() { InterlockedDecrement(&g_objects); }

    long _refs;
    const wchar_t* _title;
    const wchar_t* _argument;
};

class Factory : public IClassFactory
{
public:
    explicit Factory(REFCLSID clsid) : _refs(1), _clsid(clsid) { InterlockedIncrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&_refs); }

    IFACEMETHODIMP_(ULONG) Release()
    {
        long refs = InterlockedDecrement(&_refs);
        if (refs == 0) delete this;
        return refs;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        PinCommand* command = _clsid == CLSID_PinTaskbar
            ? new (std::nothrow) PinCommand(L"Pin to taskbar (AST)", L"--pin-taskbar")
            : new (std::nothrow) PinCommand(L"Pin to Start (AST)", L"--pin-start");
        if (!command) return E_OUTOFMEMORY;
        HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock)
    {
        if (lock) InterlockedIncrement(&g_locks);
        else InterlockedDecrement(&g_locks);
        return S_OK;
    }

private:
    ~Factory() { InterlockedDecrement(&g_objects); }

    long _refs;
    CLSID _clsid;
};

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_PinTaskbar && clsid != CLSID_PinStart) return CLASS_E_CLASSNOTAVAILABLE;
    Factory* factory = new (std::nothrow) Factory(clsid);
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE; }
