//------------------------------------------------------------------------------
// File: dllmain.cpp
//
// Desc: ReTran virtual camera - DLL entry point forwarding to the baseclasses
//       DllEntryPoint (debug init, g_hInst, class template init).
//------------------------------------------------------------------------------

#include <streams.h>

extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE hInstance, ULONG ulReason, LPVOID pvReserved);

extern "C" BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD ulReason, LPVOID pvReserved)
{
    return DllEntryPoint(hInstance, ulReason, pvReserved);
}
