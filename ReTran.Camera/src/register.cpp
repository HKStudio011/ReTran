//------------------------------------------------------------------------------
// File: register.cpp
//
// Desc: ReTran virtual camera - per-user COM registration. The filter is
//       registered under CLSID_VideoInputDeviceCategory so capture apps
//       (OBS/Zoom/Discord) list it as a camera device. Writes to
//       HKEY_CLASSES_ROOT only redirect to HKCU\Software\Classes when the
//       exact key already exists in that hive, so registration pre-creates the
//       HKCU keys first (cleanup runs before the pre-create, otherwise cleanup
//       deletes the pre-created keys again) and requires no elevation.
//------------------------------------------------------------------------------

#include <streams.h>
#include <dshow.h>
#include <strsafe.h>

#include "filter.h"

extern HINSTANCE g_hInst;

// Helpers defined in baseclasses/dllsetup.cpp; not exposed by a header.
STDAPI AMovieSetupRegisterServer(CLSID clsServer,
                                 LPCWSTR szDescription,
                                 LPCWSTR szFileName,
                                 LPCWSTR szThreadingModel = L"Both",
                                 LPCWSTR szServerType = L"InprocServer32");
STDAPI AMovieSetupUnregisterServer(CLSID clsServer);

namespace
{
/// Pre-creates a key under HKCU\Software\Classes.
/// Writes to HKEY_CLASSES_ROOT only redirect to the per-user hive when the exact
/// key already exists there; otherwise they target HKLM and fail without elevation.
/// Tạo trước một key dưới HKCU\Software\Classes — ghi vào HKCR chỉ redirect sang
/// hive per-user khi chính key đó đã tồn tại, ngược lại ghi HKLM và thiếu quyền.
HRESULT PreCreateUserClassesKey(const WCHAR* userRelativePath)
{
    HKEY hKey = nullptr;
    const LONG err = RegCreateKeyExW(HKEY_CURRENT_USER, userRelativePath, 0, nullptr,
                                     REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (err != ERROR_SUCCESS)
    {
        return AmHresultFromWin32(err);
    }
    RegCloseKey(hKey);
    return S_OK;
}

/// RAII wrapper around CoInitializeEx for the registration calls.
class ComScope
{
public:
    ComScope()
        : m_hr(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)),
          m_owns(SUCCEEDED(m_hr))
    {
    }
    ~ComScope()
    {
        if (m_owns)
        {
            CoUninitialize();
        }
    }
    /// Returns S_OK when COM is usable (initialised here or already inited).
    HRESULT Result() const
    {
        return (SUCCEEDED(m_hr) || m_hr == RPC_E_CHANGED_MODE) ? S_OK : m_hr;
    }

private:
    HRESULT m_hr;
    bool m_owns;
};
}

/// Registers the ReTran Camera CLSID and its video-capture category moniker.
/// Đăng ký CLSID và moniker hạng mục capture của ReTran Camera (per-user).
/// @return S_OK on success; a failure HRESULT otherwise.
STDAPI ReTranCamera_Register()
{
    WCHAR modulePath[MAX_PATH];
    const DWORD pathLen = GetModuleFileNameW(g_hInst, modulePath, MAX_PATH);
    if (pathLen == 0 || pathLen >= MAX_PATH)
    {
        return AmHresultFromWin32(GetLastError());
    }

    ComScope com;
    HRESULT hr = com.Result();
    if (FAILED(hr))
    {
        return hr;
    }

    IFilterMapper2* mapper = nullptr;
    hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
                          IID_IFilterMapper2, reinterpret_cast<void**>(&mapper));
    if (FAILED(hr))
    {
        return hr;
    }

    // Cleanup MUST run before the HKCU pre-create: deleting through HKCR
    // removes the existing HKCU copy, which would destroy the keys pre-created
    // below and make the subsequent HKCR writes target HKLM (access denied).
    mapper->UnregisterFilter(&CLSID_VideoInputDeviceCategory, nullptr, CLSID_ReTranCamera);
    AMovieSetupUnregisterServer(CLSID_ReTranCamera);

    WCHAR clsidText[CHARS_IN_GUID];
    WCHAR categoryText[CHARS_IN_GUID];
    StringFromGUID2(CLSID_ReTranCamera, clsidText, CHARS_IN_GUID);
    StringFromGUID2(CLSID_VideoInputDeviceCategory, categoryText, CHARS_IN_GUID);

    WCHAR keyPath[128];
    StringCchPrintfW(keyPath, NUMELMS(keyPath), L"Software\\Classes\\CLSID\\%ls\\InprocServer32", clsidText);
    hr = PreCreateUserClassesKey(keyPath);
    if (SUCCEEDED(hr))
    {
        StringCchPrintfW(keyPath, NUMELMS(keyPath), L"Software\\Classes\\CLSID\\%ls\\Instance\\%ls",
                         categoryText, clsidText);
        hr = PreCreateUserClassesKey(keyPath);
    }
    if (FAILED(hr))
    {
        mapper->Release();
        return hr;
    }

    hr = AMovieSetupRegisterServer(CLSID_ReTranCamera, kCameraFriendlyName, modulePath);
    if (FAILED(hr))
    {
        mapper->Release();
        return hr;
    }

    static const REGPINTYPES pinTypes[] = { { &MEDIATYPE_Video, &MEDIASUBTYPE_YUY2 } };

    REGFILTERPINS2 pin = {};
    pin.dwFlags = REG_PINFLAG_B_OUTPUT;
    pin.cInstances = 1;
    pin.nMediaTypes = 1;
    pin.lpMediaType = pinTypes;
    pin.nMediums = 0;
    pin.lpMedium = nullptr;
    pin.clsPinCategory = &PIN_CATEGORY_CAPTURE;

    REGFILTER2 filter = {};
    filter.dwVersion = 2;
    filter.dwMerit = 0x400000; // merit 0x400000 per design spec
    filter.cPins = 1;
    filter.rgPins2 = &pin;

    // Register under the video-capture category so camera apps list
    // "ReTran Camera". szInstance = nullptr makes the instance subkey the
    // filter CLSID ({...}\Instance\{CLSID}), which the pre-create above
    // prepared for the HKCR write redirection.
    hr = mapper->RegisterFilter(CLSID_ReTranCamera, kCameraFriendlyName, nullptr,
                                &CLSID_VideoInputDeviceCategory, nullptr, &filter);
    mapper->Release();

    if (FAILED(hr))
    {
        AMovieSetupUnregisterServer(CLSID_ReTranCamera);
    }
    return hr;
}

/// Removes the ReTran Camera registration (category moniker and CLSID).
/// Gỡ đăng ký ReTran Camera (moniker hạng mục và CLSID).
/// @return S_OK on success; a failure HRESULT otherwise.
STDAPI ReTranCamera_Unregister()
{
    ComScope com;
    HRESULT hr = com.Result();
    if (FAILED(hr))
    {
        return hr;
    }

    IFilterMapper2* mapper = nullptr;
    hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
                          IID_IFilterMapper2, reinterpret_cast<void**>(&mapper));
    if (SUCCEEDED(hr))
    {
        const HRESULT hrUnreg = mapper->UnregisterFilter(
            &CLSID_VideoInputDeviceCategory, nullptr, CLSID_ReTranCamera);
        mapper->Release();
        // ERROR_FILE_NOT_FOUND (never registered) is fine to ignore.
        if (FAILED(hrUnreg) &&
            hrUnreg != HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) &&
            hrUnreg != HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND))
        {
            hr = hrUnreg;
        }
    }

    const HRESULT hrServer = AMovieSetupUnregisterServer(CLSID_ReTranCamera);
    if (SUCCEEDED(hr))
    {
        hr = hrServer;
    }
    return hr;
}

extern "C" STDAPI DllRegisterServer()
{
    return ReTranCamera_Register();
}

extern "C" STDAPI DllUnregisterServer()
{
    return ReTranCamera_Unregister();
}
