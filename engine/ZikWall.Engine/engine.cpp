#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <initguid.h>
#include <fwpmu.h>
#include <vector>
#include <new>

// This module uses only documented Windows SDK APIs. No routine/builder dependency.
static const GUID zikSublayer = {0xdb537ec1,0x9946,0x4f01,{0xa3,0x92,0x70,0xa6,0x58,0x4f,0x15,0xc1}};
struct Engine { HANDLE handle = nullptr; std::vector<UINT64> filters; };
struct Rule {
    const wchar_t* name;
    const wchar_t* application;
    const wchar_t* address;
    UINT32 prefix;
    UINT32 protocol;
    UINT32 port;
    UINT32 direction; // 1=outbound, 2=inbound, 3=both
    UINT32 block;
    UINT64 priority;
};

extern "C" __declspec(dllexport) DWORD __cdecl ZikOpen(Engine** result) {
    if (!result) return ERROR_INVALID_PARAMETER;
    *result = nullptr;
    auto engine = new(std::nothrow) Engine();
    if (!engine) return ERROR_OUTOFMEMORY;
    FWPM_SESSION0 session{};
    session.displayData.name = const_cast<wchar_t*>(L"ZikWall session");
    session.flags = FWPM_SESSION_FLAG_DYNAMIC;
    DWORD status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, &session, &engine->handle);
    if (status == ERROR_SUCCESS) {
        FWPM_SUBLAYER0 sublayer{};
        sublayer.subLayerKey = zikSublayer;
        sublayer.displayData.name = const_cast<wchar_t*>(L"ZikWall");
        sublayer.weight = 0x8000;
        status = FwpmSubLayerAdd0(engine->handle, &sublayer, nullptr);
    }
    if (status) { if (engine->handle) FwpmEngineClose0(engine->handle); delete engine; return status; }
    *result = engine;
    return ERROR_SUCCESS;
}

static DWORD AddRule(Engine* engine, const Rule& rule, bool ipv6, bool inbound, std::vector<UINT64>& ids) {
    FWPM_FILTER_CONDITION0 conditions[4]{};
    UINT32 count = 0;
    FWP_BYTE_BLOB* appId = nullptr;
    if (rule.application && rule.application[0]) {
        DWORD status = FwpmGetAppIdFromFileName0(rule.application, &appId);
        if (status) return status;
        auto& c = conditions[count++]; c.fieldKey = FWPM_CONDITION_ALE_APP_ID;
        c.matchType = FWP_MATCH_EQUAL; c.conditionValue.type = FWP_BYTE_BLOB_TYPE; c.conditionValue.byteBlob = appId;
    }
    FWP_V4_ADDR_AND_MASK v4{}; FWP_V6_ADDR_AND_MASK v6{};
    if (rule.address && rule.address[0]) {
        auto& c = conditions[count++]; c.fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; c.matchType = FWP_MATCH_EQUAL;
        if (ipv6) {
            if (rule.prefix > 128 || InetPtonW(AF_INET6, rule.address, v6.addr) != 1) { if (appId) FwpmFreeMemory0(reinterpret_cast<void**>(&appId)); return ERROR_INVALID_PARAMETER; }
            v6.prefixLength = static_cast<UINT8>(rule.prefix);
            c.conditionValue.type = FWP_V6_ADDR_MASK; c.conditionValue.v6AddrMask = &v6;
        } else {
            IN_ADDR address{};
            if (rule.prefix > 32 || InetPtonW(AF_INET, rule.address, &address) != 1) { if (appId) FwpmFreeMemory0(reinterpret_cast<void**>(&appId)); return ERROR_INVALID_PARAMETER; }
            v4.addr = ntohl(address.s_addr); v4.mask = rule.prefix == 0 ? 0 : (0xffffffffu << (32 - rule.prefix));
            c.conditionValue.type = FWP_V4_ADDR_MASK; c.conditionValue.v4AddrMask = &v4;
        }
    }
    if (rule.protocol) { auto& c = conditions[count++]; c.fieldKey = FWPM_CONDITION_IP_PROTOCOL; c.matchType = FWP_MATCH_EQUAL; c.conditionValue.type = FWP_UINT8; c.conditionValue.uint8 = static_cast<UINT8>(rule.protocol); }
    if (rule.port) { auto& c = conditions[count++]; c.fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; c.matchType = FWP_MATCH_EQUAL; c.conditionValue.type = FWP_UINT16; c.conditionValue.uint16 = static_cast<UINT16>(rule.port); }
    FWPM_FILTER0 filter{};
    filter.displayData.name = const_cast<wchar_t*>(rule.name);
    filter.subLayerKey = zikSublayer;
    filter.layerKey = ipv6 ? (inbound ? FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 : FWPM_LAYER_ALE_AUTH_CONNECT_V6) : (inbound ? FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 : FWPM_LAYER_ALE_AUTH_CONNECT_V4);
    filter.action.type = rule.block ? FWP_ACTION_BLOCK : FWP_ACTION_PERMIT;
    // Soft permits do not bypass another provider's block. Ordering applies within our sublayer.
    UINT64 priority = rule.priority;
    filter.weight.type = FWP_UINT64; filter.weight.uint64 = &priority;
    filter.numFilterConditions = count; filter.filterCondition = conditions;
    UINT64 id = 0;
    DWORD status = FwpmFilterAdd0(engine->handle, &filter, nullptr, &id);
    if (appId) FwpmFreeMemory0(reinterpret_cast<void**>(&appId));
    if (!status) ids.push_back(id);
    return status;
}

extern "C" __declspec(dllexport) DWORD __cdecl ZikApply(Engine* engine, const Rule* rules, UINT32 count) {
    if (!engine || count > 10000 || (count && !rules)) return ERROR_INVALID_PARAMETER;
    // Validate before opening a transaction; managed callers also validate the complete schema.
    for (UINT32 i = 0; i < count; i++) {
        const auto& r = rules[i];
        if (!r.name || !r.application || !r.address || r.direction < 1 || r.direction > 3 || r.block > 1 || r.port > 65535
            || (r.protocol != 0 && r.protocol != 6 && r.protocol != 17) || (r.port && !r.protocol) || !r.priority
            || (!r.application[0] && !r.address[0])) return ERROR_INVALID_PARAMETER;
    }
    DWORD status = FwpmTransactionBegin0(engine->handle, 0);
    if (status) return status;
    std::vector<UINT64> replacement;
    try {
        replacement.reserve(static_cast<size_t>(count) * 4);
        for (auto id : engine->filters) { status = FwpmFilterDeleteById0(engine->handle, id); if (status) break; }
        for (UINT32 i = 0; !status && i < count; i++) {
            const auto& r = rules[i];
            const bool hasAddress = r.address[0] != 0;
            const bool addressV6 = hasAddress && wcschr(r.address, L':');
            for (int family = 0; !status && family < 2; family++) {
                const bool ipv6 = family == 1;
                if (hasAddress && ipv6 != addressV6) continue;
                if (r.direction & 1) status = AddRule(engine, r, ipv6, false, replacement);
                if (!status && (r.direction & 2)) status = AddRule(engine, r, ipv6, true, replacement);
            }
        }
    } catch (const std::bad_alloc&) { status = ERROR_OUTOFMEMORY; }
    if (status) { FwpmTransactionAbort0(engine->handle); return status; }
    status = FwpmTransactionCommit0(engine->handle);
    if (!status) engine->filters = std::move(replacement);
    else FwpmTransactionAbort0(engine->handle);
    return status;
}

extern "C" __declspec(dllexport) void __cdecl ZikClose(Engine* engine) {
    if (engine) { FwpmEngineClose0(engine->handle); delete engine; }
}
