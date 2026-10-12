#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <appmodel.h>
#include <objbase.h>
#include <cwctype>
#include <evntrace.h>
#include <evntcons.h>
#include <string>
#include <unordered_map>
#include <vector>
#include <mutex>
#include <thread>
#include <memory>
#include <algorithm>
#include <atomic>

// Metadata only: no payload capture, ETL file, provider ACL change or kernel driver.
namespace {
const GUID Network = {0x7dd42a49,0x5329,0x4832,{0x8d,0xfd,0x43,0xd9,0x79,0x15,0x3a,0x88}};
struct UsageRow { wchar_t path[520]; wchar_t package[128]; ULONGLONG received, sent; };
struct Identity {
    HANDLE handle = nullptr; ULONGLONG created = 0; std::wstring key;
    ~Identity() { if (handle) CloseHandle(handle); }
};
struct Collector {
    TRACEHANDLE session = 0, consumer = INVALID_PROCESSTRACE_HANDLE;
    std::wstring name;
    std::vector<BYTE> properties;
    std::thread thread;
    std::mutex mutex;
    std::unordered_map<DWORD, std::unique_ptr<Identity>> processes;
    std::unordered_map<std::wstring, UsageRow> apps;
    ULONGLONG unattributed = 0, rejected = 0, events = 0;
    std::atomic<DWORD> consumerStatus{ERROR_IO_PENDING};
    EVENT_TRACE_PROPERTIES* Props() { return reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties.data()); }
    void Stop() {
        if (session) { ControlTraceW(session, name.c_str(), Props(), EVENT_TRACE_CONTROL_STOP); session = 0; }
        if (consumer != INVALID_PROCESSTRACE_HANDLE) { CloseTrace(consumer); consumer = INVALID_PROCESSTRACE_HANDLE; }
        if (thread.joinable()) thread.join();
    }
    ~Collector() { Stop(); }
    void Event(EVENT_RECORD* record) {
        if (record->EventHeader.ProviderId != Network) return;
        const auto id = record->EventHeader.EventDescriptor.Id;
        bool send = id == 10 || id == 26 || id == 42 || id == 58;
        bool receive = id == 11 || id == 27 || id == 43 || id == 59;
        if (!send && !receive) return; // Never count retransmit/copy as ordinary usage.
        std::lock_guard lock(mutex);
        if (record->EventHeader.EventDescriptor.Version != 0 || record->UserDataLength < 20) { ++rejected; return; }
        DWORD pid = 0, bytes = 0;
        memcpy(&pid, record->UserData, 4); memcpy(&bytes, static_cast<BYTE*>(record->UserData) + 4, 4);
        ++events;
        const ULONGLONG time = static_cast<ULONGLONG>(record->EventHeader.TimeStamp.QuadPart);
        auto found = processes.find(pid);
        // A retained handle identifies one process incarnation. Buffered events from
        // an exited process remain attributable only through its exit timestamp.
        if (found != processes.end()) {
            FILETIME created{}, exited{}, kernel{}, user{};
            if (!GetProcessTimes(found->second->handle, &created, &exited, &kernel, &user)) { processes.erase(found); found = processes.end(); }
            else {
                ULARGE_INTEGER end{}; end.LowPart = exited.dwLowDateTime; end.HighPart = exited.dwHighDateTime;
                if (end.QuadPart && time > end.QuadPart) { processes.erase(found); found = processes.end(); }
            }
        }
        if (found == processes.end()) {
            auto identity = std::make_unique<Identity>();
            identity->handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            FILETIME created{}, exited{}, kernel{}, user{};
            UsageRow row{}; DWORD length = 520;
            if (!identity->handle || !GetProcessTimes(identity->handle, &created, &exited, &kernel, &user) || !QueryFullProcessImageNameW(identity->handle, 0, row.path, &length)) { unattributed += bytes; return; }
            ULARGE_INTEGER start{}; start.LowPart = created.dwLowDateTime; start.HighPart = created.dwHighDateTime;
            identity->created = start.QuadPart;
            if (time < identity->created) { unattributed += bytes; return; }
            UINT32 familyLength = 128;
            if (GetPackageFamilyName(identity->handle, &familyLength, row.package) != ERROR_SUCCESS) row.package[0] = 0;
            identity->key = row.package[0] ? std::wstring(L"package:") + row.package : std::wstring(L"path:") + row.path;
            std::transform(identity->key.begin(), identity->key.end(), identity->key.begin(), [](wchar_t c) { return static_cast<wchar_t>(towupper(c)); });
            if (!apps.contains(identity->key)) {
                if (apps.size() >= 512) { unattributed += bytes; return; }
                apps.emplace(identity->key, row);
            }
            // Bounded handle cache; evicting metadata never discards app totals.
            if (processes.size() >= 2048) processes.clear();
            found = processes.emplace(pid, std::move(identity)).first;
        }
        if (time < found->second->created) { unattributed += bytes; return; }
        auto& row = apps.at(found->second->key);
        if (send) row.sent += bytes; else row.received += bytes;
    }
    static void WINAPI Callback(EVENT_RECORD* record) noexcept {
        auto* self = static_cast<Collector*>(record->UserContext);
        try { self->Event(record); } catch (...) { std::lock_guard lock(self->mutex); ++self->rejected; }
    }
};
std::unique_ptr<Collector> collector;
}

extern "C" __declspec(dllexport) DWORD __cdecl zw_usage_start() noexcept {
    if (collector) return ERROR_ALREADY_EXISTS;
    try {
        auto next = std::make_unique<Collector>();
        GUID guid{}; if (CoCreateGuid(&guid) != S_OK) return ERROR_GEN_FAILURE;
        wchar_t unique[40]{}; StringFromGUID2(guid, unique, 40);
        next->name = std::wstring(L"ZikWall.Usage.") + unique;
        next->properties.resize(sizeof(EVENT_TRACE_PROPERTIES) + (next->name.size() + 1) * sizeof(wchar_t));
        auto* props = next->Props();
        props->Wnode.BufferSize = static_cast<ULONG>(next->properties.size());
        props->Wnode.Flags = WNODE_FLAG_TRACED_GUID; props->Wnode.ClientContext = 2; props->Wnode.Guid = guid;
        props->BufferSize = 64; props->MinimumBuffers = 16; props->MaximumBuffers = 64;
        props->LogFileMode = EVENT_TRACE_REAL_TIME_MODE; props->FlushTimer = 1;
        props->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
        DWORD error = StartTraceW(&next->session, next->name.c_str(), props);
        if (error) { next->session = 0; return error; }
        error = EnableTraceEx2(next->session, &Network, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 4, 0x30, 0, 0, nullptr);
        if (error) return error;
        EVENT_TRACE_LOGFILEW log{}; log.LoggerName = next->name.data();
        log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
        log.EventRecordCallback = Collector::Callback; log.Context = next.get();
        next->consumer = OpenTraceW(&log);
        if (next->consumer == INVALID_PROCESSTRACE_HANDLE) return GetLastError();
        auto* pointer = next.get();
        next->thread = std::thread([pointer] { auto handle = pointer->consumer; pointer->consumerStatus.store(ProcessTrace(&handle, 1, nullptr, nullptr)); });
        collector = std::move(next); return ERROR_SUCCESS;
    } catch (...) { return ERROR_NOT_ENOUGH_MEMORY; }
}

extern "C" __declspec(dllexport) DWORD __cdecl zw_usage_read(UsageRow* rows, UINT32 capacity, UINT32* count, ULONGLONG* events, ULONGLONG* unattributed, ULONGLONG* rejected, ULONG* lost, ULONG* buffers) noexcept {
    if (!collector || !rows || !count || !events || !unattributed || !rejected || !lost || !buffers) return ERROR_INVALID_PARAMETER;
    auto* self = collector.get();
    DWORD state = self->consumerStatus.load();
    if (state != ERROR_IO_PENDING) return state ? state : ERROR_OPERATION_ABORTED;
    DWORD error = ControlTraceW(self->session, self->name.c_str(), self->Props(), EVENT_TRACE_CONTROL_QUERY);
    if (error) return error;
    std::lock_guard lock(self->mutex);
    if (self->apps.size() > capacity) return ERROR_INSUFFICIENT_BUFFER;
    *count = 0; for (const auto& pair : self->apps) rows[(*count)++] = pair.second;
    *events = self->events; *unattributed = self->unattributed; *rejected = self->rejected;
    *lost = self->Props()->EventsLost + self->Props()->LogBuffersLost + self->Props()->RealTimeBuffersLost;
    *buffers = self->Props()->NumberOfBuffers * self->Props()->BufferSize;
    return ERROR_SUCCESS;
}
extern "C" __declspec(dllexport) void __cdecl zw_usage_stop() noexcept { collector.reset(); }
