//------------------------------------------------------------------------------
// File: filter.h
//
// Desc: ReTran virtual camera - DirectShow source filter exposing a single
//       YUY2 1280x720 @ 30 fps output pin.
//------------------------------------------------------------------------------

#pragma once

#include <streams.h>
#include <vector>

/// Friendly name shown by capture apps (OBS/Zoom/Discord).
extern const WCHAR kCameraFriendlyName[];

/// CLSID of the ReTranCamera filter, {B7E6A1C4-52D8-4F3A-9C11-6D2E8F40A917}.
extern const CLSID CLSID_ReTranCamera;

/// Fixed output geometry: 1280x720.
const int kCameraWidth = 1280;
const int kCameraHeight = 720;
/// Frame duration in 100-ns units (~30 fps).
const LONGLONG kFrameDuration = 333333;

/// Output pin producing the camera frames.
///
/// Frames come from the shared-memory section written by the app
/// (Local\ReTran.Camera.V1). The heartbeat mapping (Local\ReTran.Camera.Heartbeat)
/// is created and held by the app only, so a missing heartbeat means the app
/// exited and the pin must show black instead of a frozen frame. A frame that
/// cannot be read (seqlock race) keeps the previously converted frame (hold).
/// Also exposes IAMStreamConfig (fixed single format) - capture apps
/// (OBS/Zoom/Discord/ffmpeg) require it to query and set the format.
class CReTranCameraStream : public CSourceStream, public IAMStreamConfig, public IKsPropertySet
{
public:
    CReTranCameraStream(HRESULT* phr, CSource* pParent, LPCWSTR pName);

    DECLARE_IUNKNOWN

    /// Advertises the single supported media type (YUY2 1280x720).
    HRESULT GetMediaType(int iPosition, CMediaType* pMediaType) override;
    /// Accepts only the advertised media type.
    HRESULT CheckMediaType(const CMediaType* pMediaType) override;
    /// Negotiates fixed-size buffers of one full frame (4 buffers).
    HRESULT DecideBufferSize(IMemAllocator* pAlloc, ALLOCATOR_PROPERTIES* pRequest) override;
    /// Fills the sample with the current frame and paces it to ~30 fps.
    HRESULT FillBuffer(IMediaSample* pSample) override;

    HRESULT OnThreadCreate() override;
    HRESULT OnThreadDestroy() override;
    HRESULT OnThreadStartPlay() override;

    /// Exposes IAMStreamConfig alongside the base pin interfaces.
    STDMETHODIMP NonDelegatingQueryInterface(REFIID riid, void** ppv) override;

    /// Accepts only the fixed format (or null to reset).
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* pmt) override;
    /// Returns a copy of the current (only) format.
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** ppmt) override;
    /// Reports one fixed capability entry.
    STDMETHODIMP GetNumberOfCapabilities(int* piCount, int* piSize) override;
    /// Fills the fixed capability (1280x720 YUY2 @ 30 fps) for entry 0.
    STDMETHODIMP GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC) override;

    /// IKsPropertySet — no settable properties.
    STDMETHODIMP Set(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
                     DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData) override;
    /// IKsPropertySet — reports the pin category (capture), which clients
    /// like ffmpeg/OBS require to accept the pin.
    STDMETHODIMP Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
                     DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData,
                     DWORD* pcbReturned) override;
    /// IKsPropertySet — reports GET support for the pin-category property.
    STDMETHODIMP QuerySupported(REFGUID guidPropSet, DWORD dwPropID, DWORD* pTypeSupport) override;

private:
    /// Re-checks the app heartbeat (at most every 500 ms) and reopens/closes the frame section on a
    /// liveness transition. Dò lại heartbeat của app (tối đa mỗi 500 ms) và mở/đóng lại vùng khung
    /// khi trạng thái sống đổi.
    void RefreshWriterState(DWORD now);
    /// Converts the newest complete shared-memory frame into the output buffer. Chuyển khung hoàn
    /// chỉnh mới nhất trong shared memory vào buffer đầu ra.
    bool ReadSharedFrame();
    /// Releases the frame-section handle and view. Giải phóng handle và view của vùng khung.
    void CloseSharedFrame();
    /// Fills the output frame with YUY2 limited-range black. Điền khung đầu ra bằng màu đen
    /// limited-range của YUY2.
    void FillBlackFrame();

    std::vector<BYTE> m_frame;
    std::vector<BYTE> m_scratch;
    HANDLE m_frameMapping = nullptr;
    const BYTE* m_frameView = nullptr;
    LONGLONG m_mediaTime = 0;
    DWORD m_baseMs = 0;
    DWORD m_lastHeartbeatCheckMs = 0;
    bool m_writerAlive = false;
};

/// Converts BGRA32 pixels to YUY2 (BT.601 limited range). Exported for the C# interop test.
/// Chuyển pixel BGRA32 sang YUY2 (BT.601 limited range). Export để test interop C#.
extern "C" __declspec(dllexport) void __stdcall ReTranCamera_ConvertBgraToYuy2(
    const unsigned char* bgra, unsigned char* yuy2, unsigned int pixelCount);

/// The virtual camera filter: a CSource with exactly one output pin.
class CReTranCamera : public CSource
{
public:
    /// Factory used by the COM class factory (baseclasses g_Templates).
    static CUnknown* WINAPI CreateInstance(LPUNKNOWN lpunk, HRESULT* phr);

private:
    CReTranCamera(LPUNKNOWN lpunk, HRESULT* phr);
};
