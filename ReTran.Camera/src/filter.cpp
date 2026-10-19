//------------------------------------------------------------------------------
// File: filter.cpp
//
// Desc: ReTran virtual camera - source filter and output pin implementation.
//------------------------------------------------------------------------------

#include "filter.h"

#include <mmsystem.h>

namespace
{
/// Shared-memory names mirrored from ReTran.App/Services/Camera/CameraProtocol.cs.
const wchar_t kFrameMapName[] = L"Local\\ReTran.Camera.V1";
const wchar_t kHeartbeatMapName[] = L"Local\\ReTran.Camera.Heartbeat";

/// How often the pin re-checks whether the app that feeds the frames is still alive.
const DWORD kHeartbeatCheckMs = 500;

/// Fixed header layout of the shared-memory frame (see CameraProtocol.cs).
const int kOffsetMagic = 0;
const int kOffsetSeq = 24;
const int kOffsetPixels = 48;
const unsigned int kFrameMagic = 0x4D414352;

/// Seqlock read attempts before the pin keeps showing its previous frame.
const int kMaxReadAttempts = 4;

/// YUY2 limited-range black (BT.601). Chroma must be neutral: a zeroed buffer is *not* black, it
/// decodes to saturated green, so the app-closed state has to be filled field by field.
const BYTE kBlackY = 16;
const BYTE kBlackU = 128;
const BYTE kBlackV = 128;

/// Fills a YUY2 buffer with limited-range black.
void FillBlackYuy2(BYTE* yuy2, size_t bytes)
{
    for (size_t i = 0; i + 3 < bytes; i += 4)
    {
        yuy2[i] = kBlackY;
        yuy2[i + 1] = kBlackU;
        yuy2[i + 2] = kBlackY;
        yuy2[i + 3] = kBlackV;
    }
}

/// Converts BGRA32 pixels to YUY2 (BT.601 limited range), two pixels per 4-byte group.
/// U/V are averaged over the pair so a two-pixel group never favours one side.
void ConvertBgraToYuy2Impl(const BYTE* bgra, BYTE* yuy2, unsigned int pixelCount)
{
    for (unsigned int i = 0; i + 1 < pixelCount; i += 2)
    {
        const BYTE* p0 = bgra + (static_cast<size_t>(i) * 4);
        const BYTE* p1 = bgra + (static_cast<size_t>(i + 1) * 4);
        const int b0 = p0[0], g0 = p0[1], r0 = p0[2];
        const int b1 = p1[0], g1 = p1[1], r1 = p1[2];
        BYTE* out = yuy2 + (static_cast<size_t>(i / 2) * 4);
        out[0] = static_cast<BYTE>((((66 * r0) + (129 * g0) + (25 * b0) + 128) >> 8) + 16);
        out[2] = static_cast<BYTE>((((66 * r1) + (129 * g1) + (25 * b1) + 128) >> 8) + 16);
        out[1] = static_cast<BYTE>((((-38 * r0) - (74 * g0) + (112 * b0)
                                     - (38 * r1) - (74 * g1) + (112 * b1) + 256) >> 9) + 128);
        out[3] = static_cast<BYTE>((((112 * r0) - (94 * g0) - (18 * b0)
                                     + (112 * r1) - (94 * g1) - (18 * b1) + 256) >> 9) + 128);
    }
}
}

extern "C" __declspec(dllexport) void __stdcall ReTranCamera_ConvertBgraToYuy2(
    const unsigned char* bgra, unsigned char* yuy2, unsigned int pixelCount)
{
    if (bgra == nullptr || yuy2 == nullptr || pixelCount < 2)
    {
        return;
    }
    ConvertBgraToYuy2Impl(bgra, yuy2, pixelCount);
}

CReTranCameraStream::CReTranCameraStream(HRESULT* phr, CSource* pParent, LPCWSTR pName)
    : CSourceStream(NAME("ReTranCameraStream"), phr, pParent, pName)
{
    // Black until the app publishes its first frame (the app-closed state is also black).
    // Đen cho tới khi app xuất bản khung đầu tiên (trạng thái app đóng cũng là đen).
    m_frame.assign(static_cast<size_t>(kCameraWidth) * kCameraHeight * 2, 0);
    m_scratch.assign(m_frame.size(), 0);
    FillBlackFrame();
}

void CReTranCameraStream::FillBlackFrame()
{
    FillBlackYuy2(m_frame.data(), m_frame.size());
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

    // Live frames while the app is alive; black once it exits; the last converted frame is kept
    // when a read loses the seqlock race (hold).
    // Khung sống khi app còn chạy; đen khi app thoát; giữ khung đã chuyển đổi gần nhất khi đọc
    // thua seqlock (hold).
    RefreshWriterState(timeGetTime());
    if (m_writerAlive)
    {
        ReadSharedFrame();
    }
    else
    {
        FillBlackFrame();
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

void CReTranCameraStream::RefreshWriterState(DWORD now)
{
    if (m_lastHeartbeatCheckMs != 0 && (now - m_lastHeartbeatCheckMs) < kHeartbeatCheckMs)
    {
        return;
    }
    m_lastHeartbeatCheckMs = now;

    // The heartbeat mapping is created and held by the app only, so a failed open means the app
    // exited. Re-opening the frame section after a transition also picks up a restarted app.
    // Mapping heartbeat chỉ do app tạo và giữ, nên mở thất bại nghĩa là app đã thoát. Mở lại vùng
    // khung sau mỗi lần đổi trạng thái cũng bắt được app khởi động lại.
    HANDLE heartbeat = OpenFileMappingW(FILE_MAP_READ, FALSE, kHeartbeatMapName);
    const bool alive = (heartbeat != nullptr);
    if (heartbeat != nullptr)
    {
        CloseHandle(heartbeat);
    }

    if (alive != m_writerAlive)
    {
        m_writerAlive = alive;
        CloseSharedFrame();
    }
}

bool CReTranCameraStream::ReadSharedFrame()
{
    if (m_frameView == nullptr)
    {
        m_frameMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, kFrameMapName);
        if (m_frameMapping == nullptr)
        {
            return false;
        }
        m_frameView = static_cast<const BYTE*>(MapViewOfFile(m_frameMapping, FILE_MAP_READ, 0, 0, 0));
        if (m_frameView == nullptr)
        {
            CloseHandle(m_frameMapping);
            m_frameMapping = nullptr;
            return false;
        }
    }

    const volatile unsigned int* seq =
        reinterpret_cast<const volatile unsigned int*>(m_frameView + kOffsetSeq);
    const volatile unsigned int* magic =
        reinterpret_cast<const volatile unsigned int*>(m_frameView + kOffsetMagic);

    for (int attempt = 0; attempt < kMaxReadAttempts; ++attempt)
    {
        const unsigned int before = *seq;
        if ((before & 1u) != 0)
        {
            continue; // writer is mid-copy
        }
        MemoryBarrier();
        if (*magic != kFrameMagic)
        {
            continue; // section exists but no frame was published yet
        }

        // Convert into the scratch buffer so a lost race never leaves a torn frame on screen.
        // Chuyển đổi vào buffer tạm để lần thua race không để lại khung bị xé trên màn hình.
        ConvertBgraToYuy2Impl(
            m_frameView + kOffsetPixels,
            m_scratch.data(),
            static_cast<unsigned int>(kCameraWidth) * kCameraHeight);
        MemoryBarrier();

        if (*seq == before)
        {
            m_frame.swap(m_scratch);
            return true;
        }
    }
    return false;
}

void CReTranCameraStream::CloseSharedFrame()
{
    if (m_frameView != nullptr)
    {
        UnmapViewOfFile(const_cast<BYTE*>(m_frameView));
        m_frameView = nullptr;
    }
    if (m_frameMapping != nullptr)
    {
        CloseHandle(m_frameMapping);
        m_frameMapping = nullptr;
    }
}

HRESULT CReTranCameraStream::OnThreadCreate()
{
    timeBeginPeriod(1);
    return S_OK;
}

HRESULT CReTranCameraStream::OnThreadDestroy()
{
    CloseSharedFrame();
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
