//------------------------------------------------------------------------------
// File: filter.cpp
//
// Desc: ReTran virtual camera - source filter and output pin implementation.
//------------------------------------------------------------------------------

#include "filter.h"

#include <mmsystem.h>

namespace
{
/// YUY2 encoding of #22C55E (RGB 34,197,94) under BT.601: Y=136 U=104 V=55.
const BYTE kGreenY = 136;
const BYTE kGreenU = 104;
const BYTE kGreenV = 55;
}

CReTranCameraStream::CReTranCameraStream(HRESULT* phr, CSource* pParent, LPCWSTR pName)
    : CSourceStream(NAME("ReTranCameraStream"), phr, pParent, pName)
{
    m_frame.resize(static_cast<size_t>(kCameraWidth) * kCameraHeight * 2);
    for (size_t i = 0; i + 3 < m_frame.size(); i += 4)
    {
        m_frame[i] = kGreenY;
        m_frame[i + 1] = kGreenU;
        m_frame[i + 2] = kGreenY;
        m_frame[i + 3] = kGreenV;
    }
}

HRESULT CReTranCameraStream::GetMediaType(int iPosition, CMediaType* pMediaType)
{
    CheckPointer(pMediaType, E_POINTER);
    if (iPosition != 0)
    {
        return E_INVALIDARG;
    }

    pMediaType->SetType(&MEDIATYPE_Video);
    pMediaType->SetSubtype(&MEDIASUBTYPE_YUY2);
    pMediaType->SetFormatType(&FORMAT_VideoInfo);

    pMediaType->SetSampleSize(static_cast<long>(m_frame.size()));
    pMediaType->SetTemporalCompression(FALSE);

    VIDEOINFOHEADER* pvi = reinterpret_cast<VIDEOINFOHEADER*>(
        pMediaType->AllocFormatBuffer(sizeof(VIDEOINFOHEADER)));
    if (pvi == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    ZeroMemory(pvi, sizeof(VIDEOINFOHEADER));
    pvi->AvgTimePerFrame = kFrameDuration;
    pvi->dwBitRate = static_cast<DWORD>(m_frame.size() * 8LL * 30);
    pvi->dwBitErrorRate = 0;
    pvi->rcSource.right = kCameraWidth;
    pvi->rcSource.bottom = kCameraHeight;
    pvi->rcTarget = pvi->rcSource;
    pvi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pvi->bmiHeader.biWidth = kCameraWidth;
    pvi->bmiHeader.biHeight = kCameraHeight; // positive (video consumers read YUY2 rows top-down; ffmpeg dshow passes biHeight through without abs())
    pvi->bmiHeader.biPlanes = 1;
    pvi->bmiHeader.biBitCount = 16;
    pvi->bmiHeader.biCompression = MAKEFOURCC('Y', 'U', 'Y', '2');
    pvi->bmiHeader.biSizeImage = static_cast<DWORD>(m_frame.size());
    return S_OK;
}

HRESULT CReTranCameraStream::CheckMediaType(const CMediaType* pMediaType)
{
    CheckPointer(pMediaType, E_POINTER);
    if (*pMediaType->Type() != MEDIATYPE_Video ||
        *pMediaType->Subtype() != MEDIASUBTYPE_YUY2 ||
        *pMediaType->FormatType() != FORMAT_VideoInfo)
    {
        return E_FAIL;
    }
    if (pMediaType->Format() == nullptr ||
        pMediaType->FormatLength() < sizeof(VIDEOINFOHEADER))
    {
        return E_FAIL;
    }

    const VIDEOINFOHEADER* pvi = reinterpret_cast<const VIDEOINFOHEADER*>(pMediaType->Format());
    if (pvi->bmiHeader.biWidth != kCameraWidth ||
        abs(pvi->bmiHeader.biHeight) != kCameraHeight ||
        pvi->bmiHeader.biCompression != MAKEFOURCC('Y', 'U', 'Y', '2'))
    {
        return E_FAIL;
    }
    return S_OK;
}

HRESULT CReTranCameraStream::DecideBufferSize(IMemAllocator* pAlloc, ALLOCATOR_PROPERTIES* pRequest)
{
    CheckPointer(pAlloc, E_POINTER);
    CheckPointer(pRequest, E_POINTER);
    pRequest->cBuffers = 4;
    pRequest->cbBuffer = static_cast<long>(m_frame.size());
    pRequest->cbAlign = 1;
    pRequest->cbPrefix = 0;

    ALLOCATOR_PROPERTIES actual = {};
    HRESULT hr = pAlloc->SetProperties(pRequest, &actual);
    if (FAILED(hr))
    {
        return hr;
    }
    return (actual.cbBuffer >= pRequest->cbBuffer) ? S_OK : E_FAIL;
}

HRESULT CReTranCameraStream::FillBuffer(IMediaSample* pSample)
{
    CheckPointer(pSample, E_POINTER);

    // Pace the delivery to ~30 fps based on the frame index so the wall-clock
    // schedule does not drift; timeBeginPeriod(1) keeps Sleep accurate.
    const LONGLONG frameIndex = m_mediaTime / kFrameDuration;
    const DWORD due = m_baseMs + static_cast<DWORD>((frameIndex * kFrameDuration) / 10000);
    const DWORD now = timeGetTime();
    if (static_cast<LONG>(due - now) > 0)
    {
        Sleep(due - now);
    }
    else if (static_cast<LONG>(now - due) > 500)
    {
        // Downstream stalled badly; shift the schedule to avoid a catch-up burst.
        m_baseMs += (now - due);
    }

    BYTE* pBuffer = nullptr;
    HRESULT hr = pSample->GetPointer(&pBuffer);
    if (FAILED(hr))
    {
        return hr;
    }
    if (pSample->GetSize() < static_cast<long>(m_frame.size()))
    {
        return E_FAIL;
    }
    CopyMemory(pBuffer, m_frame.data(), m_frame.size());

    LONGLONG end = m_mediaTime + kFrameDuration;
    pSample->SetTime(&m_mediaTime, &end);
    pSample->SetSyncPoint(TRUE);
    pSample->SetDiscontinuity(FALSE);
    pSample->SetPreroll(FALSE);
    m_mediaTime = end;
    return S_OK;
}

HRESULT CReTranCameraStream::OnThreadCreate()
{
    timeBeginPeriod(1);
    return S_OK;
}

HRESULT CReTranCameraStream::OnThreadDestroy()
{
    timeEndPeriod(1);
    return S_OK;
}

HRESULT CReTranCameraStream::OnThreadStartPlay()
{
    m_mediaTime = 0;
    m_baseMs = timeGetTime();
    return S_OK;
}

STDMETHODIMP CReTranCameraStream::NonDelegatingQueryInterface(REFIID riid, void** ppv)
{
    CheckPointer(ppv, E_POINTER);
    if (riid == IID_IAMStreamConfig)
    {
        return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
    }
    if (riid == IID_IKsPropertySet)
    {
        return GetInterface(static_cast<IKsPropertySet*>(this), ppv);
    }
    return CSourceStream::NonDelegatingQueryInterface(riid, ppv);
}

STDMETHODIMP CReTranCameraStream::SetFormat(AM_MEDIA_TYPE* pmt)
{
    if (pmt == nullptr)
    {
        return S_OK; // null resets to the (only) built-in format
    }
    CMediaType mt(*pmt);
    return SUCCEEDED(CheckMediaType(&mt)) ? S_OK : E_INVALIDARG;
}

STDMETHODIMP CReTranCameraStream::GetFormat(AM_MEDIA_TYPE** ppmt)
{
    CheckPointer(ppmt, E_POINTER);
    *ppmt = nullptr;
    CMediaType mt;
    HRESULT hr = GetMediaType(0, &mt);
    if (FAILED(hr))
    {
        return hr;
    }
    *ppmt = CreateMediaType(&mt);
    return (*ppmt != nullptr) ? S_OK : E_OUTOFMEMORY;
}

STDMETHODIMP CReTranCameraStream::GetNumberOfCapabilities(int* piCount, int* piSize)
{
    CheckPointer(piCount, E_POINTER);
    CheckPointer(piSize, E_POINTER);
    *piCount = 1;
    *piSize = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP CReTranCameraStream::GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC)
{
    CheckPointer(ppmt, E_POINTER);
    CheckPointer(pSCC, E_POINTER);
    if (iIndex != 0)
    {
        return E_INVALIDARG;
    }
    *ppmt = nullptr;

    CMediaType mt;
    HRESULT hr = GetMediaType(0, &mt);
    if (FAILED(hr))
    {
        return hr;
    }
    *ppmt = CreateMediaType(&mt);
    if (*ppmt == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    VIDEO_STREAM_CONFIG_CAPS* caps = reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(pSCC);
    ZeroMemory(caps, sizeof(*caps));
    caps->guid = FORMAT_VideoInfo;
    caps->InputSize.cx = kCameraWidth;
    caps->InputSize.cy = kCameraHeight;
    caps->MinCroppingSize.cx = caps->MaxCroppingSize.cx = kCameraWidth;
    caps->MinCroppingSize.cy = caps->MaxCroppingSize.cy = kCameraHeight;
    caps->CropGranularityX = caps->CropGranularityY = 1;
    caps->CropAlignX = caps->CropAlignY = 1;
    caps->MinOutputSize.cx = caps->MaxOutputSize.cx = kCameraWidth;
    caps->MinOutputSize.cy = caps->MaxOutputSize.cy = kCameraHeight;
    caps->OutputGranularityX = caps->OutputGranularityY = 1;
    caps->StretchTapsX = caps->StretchTapsY = 0;
    caps->ShrinkTapsX = caps->ShrinkTapsY = 0;
    caps->MinFrameInterval = caps->MaxFrameInterval = kFrameDuration;
    const LONG bitsPerSecond = static_cast<LONG>(kCameraWidth) * kCameraHeight * 16 * 30;
    caps->MinBitsPerSecond = caps->MaxBitsPerSecond = bitsPerSecond;
    return S_OK;
}

STDMETHODIMP CReTranCameraStream::Set(REFGUID guidPropSet, DWORD, LPVOID, DWORD, LPVOID, DWORD)
{
    if (guidPropSet != AMPROPSETID_Pin)
    {
        return E_PROP_SET_UNSUPPORTED;
    }
    return E_PROP_ID_UNSUPPORTED; // the pin category is not settable
}

STDMETHODIMP CReTranCameraStream::QuerySupported(REFGUID guidPropSet, DWORD dwPropID, DWORD* pTypeSupport)
{
    if (guidPropSet != AMPROPSETID_Pin)
    {
        return E_PROP_SET_UNSUPPORTED;
    }
    if (dwPropID != AMPROPERTY_PIN_CATEGORY)
    {
        return E_PROP_ID_UNSUPPORTED;
    }
    if (pTypeSupport != nullptr)
    {
        *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    }
    return S_OK;
}

STDMETHODIMP CReTranCameraStream::Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID,
                                      DWORD, LPVOID pPropData, DWORD cbPropData, DWORD* pcbReturned)
{
    if (guidPropSet != AMPROPSETID_Pin)
    {
        return E_PROP_SET_UNSUPPORTED;
    }
    if (dwPropID != AMPROPERTY_PIN_CATEGORY)
    {
        return E_PROP_ID_UNSUPPORTED;
    }
    if (pPropData == nullptr || cbPropData < sizeof(GUID))
    {
        return E_INVALIDARG;
    }
    *static_cast<GUID*>(pPropData) = PIN_CATEGORY_CAPTURE;
    if (pcbReturned != nullptr)
    {
        *pcbReturned = sizeof(GUID);
    }
    return S_OK;
}

CReTranCamera::CReTranCamera(LPUNKNOWN lpunk, HRESULT* phr)
    : CSource(NAME("ReTran Camera"), lpunk, CLSID_ReTranCamera, phr)
{
    CAutoLock cAutoLock(&m_cStateLock);
    new CReTranCameraStream(phr, this, L"Capture");
    if (phr != nullptr && FAILED(*phr))
    {
        // The class factory deletes the partially constructed object.
        return;
    }
}

CUnknown* WINAPI CReTranCamera::CreateInstance(LPUNKNOWN lpunk, HRESULT* phr)
{
    CheckPointer(phr, nullptr);
    *phr = S_OK;
    return new CReTranCamera(lpunk, phr);
}
