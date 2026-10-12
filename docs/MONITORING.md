# ZikWall connection monitoring

Connections opens by default and shows Internet endpoints. Select All endpoints to include local connections, listening TCP sockets and UDP bindings. Search includes application, PID, path, address/port, protocol, state, direction and country. Direction and scope filters also apply to history and exported CSV.

The native WinUI list shows executable icons, application/PID, incoming/outgoing direction, local and remote IP/port, country flag/name and Windows TCP state. Long addresses have tooltips and selectable full text in Details. Protected/inaccessible executables use a generic icon. Icons come from the Windows shell; ZikWall does not launch those executables. Only realized rows request icons, with two concurrent loads, at most 32 pending requests and a 256-path cache of 32×32 pixel buffers (4 KiB each). Every realized row owns its native bitmap; native image-source objects are not shared across recycled rows.

Secondary controls and adapter charts are collapsed under **Monitor options and adapter traffic**, leaving space for the list. Refresh defaults to five seconds, with two-/ten-second choices and 15-second hidden/minimized sampling. Sampling can miss short connections. The Internet filter classifies remote address space; it does not prove which physical route carried traffic.

## Direction and observation limits

TCP direction uses the read-only `ActiveOpen` field in Windows `TCP_ESTATS_SYN_OPTS_ROS_v0`: initiating sockets are Outgoing and accepted sockets Incoming. IPv4 and IPv6 have separate SDK structures and live loopback tests. A listening socket is labeled Listening; unavailable EStats results remain Unknown. Reading SYN options does not require enabling traffic statistics, elevation or firewall changes.

IP Helper's UDP ownership table provides local bindings only. ZikWall does not invent UDP remote addresses or directions; these bindings appear under All endpoints or Local / listening. QUIC and other UDP traffic therefore lack destination visibility in this implementation. Connection snapshots are not firewall allow/block evidence. Per-app bandwidth, blocked/unmatched events and reliable requested-domain attribution remain separate unfinished work.

Microsoft references: [SYN options](https://learn.microsoft.com/en-us/windows/win32/api/tcpestats/ns-tcpestats-tcp_estats_syn_opts_ros_v0), [IPv4 EStats](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getpertcpconnectionestats), [IPv6 EStats](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getpertcp6connectionestats).

## Countries and provenance

Country lookups are offline: observed IP addresses are never submitted to a location service. Bundled DB-IP IP to Country Lite, October 2026, is [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), with attribution in the UI and [asset notice](../ui/ZikWall.WinUI/Assets/Countries/NOTICE.md). The dataset contains 362,122 IPv4 and 348,712 IPv6 ranges, stored as two memory-mapped binary tables totaling 15,477,444 bytes. Binary search uses a bounded 4,096-address result cache. Missing/corrupt data leaves monitoring functional with countries unknown.

Country is an approximate database assignment, not proof of a server's actual location. VPNs, proxies, CDNs and anycast can make it misleading. For example, this pinned dataset places Google's IPv6 DNS range in Canada and its IPv4 8.8.8.8 range in the US. Do not treat either as a measurement of the responding anycast node. The map still needs imported latitude/longitude data; a country table does not supply coordinates.

Flags come from MIT-licensed [lipis/flag-icons](https://github.com/lipis/flag-icons), version 7.2.3, commit `5667d75be0c0396f29fdc73e2b2e341d0e702843`. The [asset notice and license](../ui/ZikWall.WinUI/Assets/Flags/NOTICE.md) are bundled. SVG extraction accepts only bounded, two-letter country assets and rejects active/external content. All 258 SVGs, including ZikWall's original unknown-location globe, decoded successfully with WinUI's SVG renderer.

The conversion scripts verify pinned download SHA-256 values before parsing. They do not execute downloaded code. Generated country and flag assets have recorded hashes checked by `tools/test-supply-chain.ps1`. There are no runtime data downloads or additional NuGet dependencies for this feature. Hashes establish consistency with the reviewed inputs, not publisher authenticity or an absence of compromise.

Rebuild from the documented downloads with `tools/build-country-data.py` and `tools/extract-monitor-flags.py`; paths and hashes are recorded beside the assets. Country-data updates are explicit build-time changes requiring new provenance and validation.

## Recent connection notifications

In-app recent alerts default on. Windows notifications are separately opt-in under Monitor options. Both report **newly observed Internet endpoints**, not exact connection creation times or firewall decisions. Startup/resume/enabling alerts suppresses the next baseline, local endpoints are excluded, repeated app/destination/port/direction combinations are deduplicated for five minutes, and batches are limited to once per 30 seconds. Tracking and pending samples are bounded. Windows alerts are silent and replace the previous monitor toast. Clicking one activates the app. Windows notification settings or Focus Assist can prevent display even after submission succeeds.

Activity remains the detailed record. Saved history stays opt-in; notification settings currently reset on app restart. Notifications can contain app names and IPs, including in Windows Notification Center when enabled.

## Validation

The automated suite covers real IPv4/IPv6 initiating and accepted sockets, country table boundaries and invalid headers, all dataset country-code/flag mappings, private/reserved/mapped IP classification, backward-compatible history JSON, filters, startup suppression, cooldown/deduplication, disabled alerts and degraded country-data behavior.

The explicit `ZikWall.exe --test-monitor` diagnostic loads the application's real executable icon, decodes every bundled SVG, registers with Windows notifications, submits one labeled silent test notification and renders the native window to a local PNG. It changes no firewall/DNS settings. Diagnostic outputs stay under ignored `artifacts/ZikWall`; rendered live data is not committed. Submission/registration succeeded locally; actual desktop toast appearance and activation were not independently observed. RenderTargetBitmap validates XAML layout/content but does not capture compositor Mica or all native window chrome.

See [performance measurements](PERFORMANCE.md) and [remaining Little Snitch gaps](LITTLE-SNITCH-COMPARISON.md).

The native churn regression runs 500 deterministic insert/remove/replace/reset cycles with executable icons, flags and alert-banner resizing. During validation, a live run failed inside WinUI with `0xc000027b`; this stress test reproduced it quickly. Disabling banner resizing or replacing incremental updates did not fix it; disabling executable icons did. Separate per-row bitmaps improved isolation but did not resolve the complete test. Deferring icon visual changes through the UI dispatcher, outside the dependency-property callback used during native template binding/layout, removed the reproduced failure. Icons retain a bounded pixel cache and per-row native bitmap ownership. `tools/test-monitor-ui.ps1` runs the regression twice by default; `-RenderAndNotify` also runs the explicit render/toast diagnostic. Temporary exception logging and isolation switches were removed.
