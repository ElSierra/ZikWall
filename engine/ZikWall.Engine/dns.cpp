#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <string>
#include <vector>
#include <thread>
#include <memory>
#include <atomic>

namespace {
const GUID Ndis = {0x2ed6006e,0x4729,0x4609,{0xb4,0x23,0x3e,0xe7,0xbc,0xd6,0x78,0xef}};
// Media: 0 Ethernet, 1 raw IP, 2 native 802.11 (without radiotap).
using PacketCallback = void(__cdecl*)(const BYTE*, ULONG, int);
struct DnsCapture {
    TRACEHANDLE consumer = INVALID_PROCESSTRACE_HANDLE;
    std::wstring name;
    std::vector<BYTE> properties;
    std::thread thread;
    PacketCallback callback = nullptr;
    std::atomic<ULONGLONG> packets{0}, rejected{0};
    std::atomic<DWORD> state{ERROR_IO_PENDING};
    EVENT_TRACE_PROPERTIES* Props() { return reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties.data()); }
    ~DnsCapture() {
        if (consumer != INVALID_PROCESSTRACE_HANDLE) CloseTrace(consumer);
        if (thread.joinable()) thread.join();
    }
    static void WINAPI Event(EVENT_RECORD* record) noexcept {
        auto* self = static_cast<DnsCapture*>(record->UserContext);
        if (record->EventHeader.ProviderId != Ndis || record->EventHeader.EventDescriptor.Id != 1001) return;
        const auto keywords = record->EventHeader.EventDescriptor.Keyword;
        // Complete, untruncated Ethernet, raw-IP or native Wi-Fi packet events.
        if (record->EventHeader.EventDescriptor.Version != 0 || record->UserDataLength < 12 ||
            (keywords & 0xc0000000ULL) != 0xc0000000ULL || (keywords & 0x2000000ULL)) { ++self->rejected; return; }
        ULONG size = 0; memcpy(&size, static_cast<BYTE*>(record->UserData) + 8, 4);
        if (size > static_cast<ULONG>(record->UserDataLength - 12)) { ++self->rejected; return; }
        ++self->packets;
        self->callback(static_cast<BYTE*>(record->UserData) + 12, size, (keywords & 0x10000) ? 2 : (keywords & 0x200) ? 1 : 0);
    }
};
std::unique_ptr<DnsCapture> capture;
}

extern "C" __declspec(dllexport) DWORD __cdecl zw_dns_start(const wchar_t* name, PacketCallback callback) noexcept {
    if (!name || wcslen(name) > 80 || wcsncmp(name, L"ZikWall.Dns.", 12) != 0 || !callback || capture) return ERROR_INVALID_PARAMETER;
    try {
        auto next = std::make_unique<DnsCapture>(); next->callback = callback;
        next->name = name;
        next->properties.resize(sizeof(EVENT_TRACE_PROPERTIES) + (next->name.size() + 1) * sizeof(wchar_t));
        auto* props = next->Props(); props->Wnode.BufferSize = static_cast<ULONG>(next->properties.size());
        props->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
        EVENT_TRACE_LOGFILEW log{}; log.LoggerName = next->name.data();
        log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
        log.EventRecordCallback = DnsCapture::Event; log.Context = next.get();
        next->consumer = OpenTraceW(&log);
        if (next->consumer == INVALID_PROCESSTRACE_HANDLE) return GetLastError();
        auto* pointer = next.get();
        next->thread = std::thread([pointer] { auto handle = pointer->consumer; pointer->state = ProcessTrace(&handle, 1, nullptr, nullptr); });
        capture = std::move(next); return ERROR_SUCCESS;
    } catch (...) { return ERROR_NOT_ENOUGH_MEMORY; }
}
extern "C" __declspec(dllexport) DWORD __cdecl zw_dns_status(ULONGLONG* packets, ULONGLONG* rejected, ULONG* lost) noexcept {
    if (!capture || !packets || !rejected || !lost) return ERROR_INVALID_PARAMETER;
    auto* self = capture.get();
    if (self->state != ERROR_IO_PENDING) return self->state == 0 ? ERROR_OPERATION_ABORTED : self->state.load();
    DWORD error = ControlTraceW(0, self->name.c_str(), self->Props(), EVENT_TRACE_CONTROL_QUERY);
    if (error) return error;
    *packets = self->packets; *rejected = self->rejected;
    *lost = self->Props()->EventsLost + self->Props()->LogBuffersLost + self->Props()->RealTimeBuffersLost;
    return ERROR_SUCCESS;
}
extern "C" __declspec(dllexport) void __cdecl zw_dns_stop() noexcept { capture.reset(); }
