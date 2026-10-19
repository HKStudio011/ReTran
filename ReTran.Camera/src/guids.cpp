//------------------------------------------------------------------------------
// File: guids.cpp
//
// Desc: ReTran virtual camera - GUIDs, friendly name and COM class table.
//------------------------------------------------------------------------------

#include <initguid.h>
#include <streams.h>

#include "filter.h"

const WCHAR kCameraFriendlyName[] = L"ReTran Camera";

// {B7E6A1C4-52D8-4F3A-9C11-6D2E8F40A917}
const CLSID CLSID_ReTranCamera =
    { 0xb7e6a1c4, 0x52d8, 0x4f3a, { 0x9c, 0x11, 0x6d, 0x2e, 0x8f, 0x40, 0xa9, 0x17 } };

/// COM class table consumed by DllGetClassObject (baseclasses dllentry.cpp).
/// Registration data is handled by ReTranCamera_Register, so the setup-filter
/// slot stays null.
CFactoryTemplate g_Templates[] =
{
    { kCameraFriendlyName, &CLSID_ReTranCamera, CReTranCamera::CreateInstance, nullptr, nullptr },
};

int g_cTemplates = sizeof(g_Templates) / sizeof(g_Templates[0]);
