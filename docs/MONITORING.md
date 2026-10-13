# ZikWall connection monitoring

Connections opens by default and shows Internet endpoints. Select All endpoints to include local connections, listening TCP sockets and UDP bindings. Search includes application, PID, path, address/port, protocol, state, direction and country. Direction and scope filters also apply to history and exported CSV.

The native WinUI list shows executable icons, application/PID, incoming/outgoing direction, local and remote IP/port, country flag/name and Windows TCP state. Long addresses have tooltips and selectable full text in Details. Protected/inaccessible executables use a generic icon. Icons come from the Windows shell; ZikWall does not launch those executables. Only realized rows request icons, with two concurrent loads, at most 32 pending requests and a 256-path cache of 32×32 pixel buffers (4 KiB each). Every realized row owns its native bitmap; native image-source objects are not shared across recycled rows.

Secondary controls and adapter charts are collapsed under **Monitor options and adapter traffic**, leaving space for the list. Refresh defaults to five seconds, with two-/ten-second choices and 15-second hidden/minimized sampling. Sampling can miss short connections. The Internet filter classifies remote address space; it does not prove which physical route carried traffic.

## Website names

Connection rows show **DNS cache hints** beside the original IP and port. Search
accepts these names; Details explains their origin, and CSV includes a
`DomainCacheHint` column. Expand an app and process, or select the flat view, to
see individual destinations. Full HTTPS URLs, paths and query strings are not
visible. Shared IPs may have several names, and browser-private encrypted DNS
can leave names unknown. These hints do not identify which hostname an app
requested and never become firewall rule targets.

The implementation reads the documented local
[MSFT_DNSClientCache provider](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/dnsclientcimprov/msft-dnsclientcache).
It sends no DNS requests and captures no packets. On Windows versions that
restrict cache access, use **Monitor options → Website names → Enable domain
hints** and accept Windows UAC. This starts a separate read-only helper for the
session; it cannot apply firewall rules or enable traffic collection. The main
app and sign-in startup stay unelevated. Closing the app releases the helper;
a long monitoring pause can require enabling it again.

One background worker reads the cache immediately, then at most once per
60 seconds while visible or 120 seconds while hidden/minimized. Hints may take
another normal connection refresh to appear. Reads stream through at most
16,384 successful A/AAAA answer records, retain at most 2,048 active remote IPs
and three names per IP, and expire hints at the record TTL or two minutes,
whichever is sooner. Names are address-record owner names; CNAME aliases are
not inferred. Unsupported/unavailable provider access leaves the IP list usable.
Repeated access-denied calls are suppressed until a new reader session.

Only in-memory hints and explicit CSV exports contain these names. Saved activity
JSON omits them. The separate, explicitly clicked reverse-DNS action still makes
a network lookup and remains labeled as a hint. See [domain performance and
validation](DOMAIN-HINTS.md).

## Direction and observation limits

TCP direction uses the read-only `ActiveOpen` field in Windows `TCP_ESTATS_SYN_OPTS_ROS_v0`: initiating sockets are Outgoing and accepted sockets Incoming. IPv4 and IPv6 have separate SDK structures and live loopback tests. A listening socket is labeled Listening; unavailable EStats results remain Unknown. Reading SYN options does not require enabling traffic statistics, elevation or firewall changes.

IP Helper's UDP ownership table provides local bindings only. ZikWall does not invent UDP remote addresses or directions; these bindings appear under All endpoints or Local / listening. QUIC and other UDP traffic therefore lack destination visibility in this implementation. Connection snapshots are not firewall allow/block evidence. Per-app bandwidth, blocked/unmatched events and reliable requested-domain attribution remain separate unfinished work.

Microsoft references: [SYN options](https://learn.microsoft.com/en-us/windows/win32/api/tcpestats/ns-tcpestats-tcp_estats_syn_opts_ros_v0), [IPv4 EStats](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getpertcpconnectionestats), [IPv6 EStats](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getpertcp6connectionestats).

## Countries and provenance

Country lookups are offline: observed IP addresses are never submitted to a location service. Bundled DB-IP IP to Country Lite, October 2026, is [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), with attribution in the UI and [asset notice](../ui/ZikWall.WinUI/Assets/Countries/NOTICE.md). The dataset contains 362,122 IPv4 and 348,712 IPv6 ranges, stored as two memory-mapped binary tables totaling 15,477,444 bytes. Binary search uses a bounded 4,096-address result cache. Missing/corrupt data leaves monitoring functional with countries unknown.

Network owners are offline too. Bundled DB-IP IP to ASN Lite, October 2026 ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), [asset notice](../ui/ZikWall.WinUI/Assets/Networks/NOTICE.md)), maps 402,389 IPv4 and 74,973 IPv6 ranges to 82,135 autonomous systems, stored in three memory-mapped tables totaling 9,989,645 bytes. Names drop legal suffixes so rows read "Microsoft · GB" rather than "Microsoft Corporation"; details and the CSV export keep the ASN. The owner is whoever announces the address. Cloud and CDN networks (Amazon, Microsoft, Cloudflare, Akamai) host many unrelated services, so an owner says where traffic went, not which site it was. Rebuild with `make network-data INPUT=<pinned CSV.gz>`.

Country is an approximate database assignment, not proof of a server's actual location. VPNs, proxies, CDNs and anycast can make it misleading. For example, this pinned dataset places Google's IPv6 DNS range in Canada and its IPv4 8.8.8.8 range in the US. Do not treat either as a measurement of the responding anycast node. The map still needs imported latitude/longitude data; a country table does not supply coordinates.

Flags come from MIT-licensed [lipis/flag-icons](https://github.com/lipis/flag-icons), version 7.2.3, commit `5667d75be0c0396f29fdc73e2b2e341d0e702843`. The [asset notice and license](../ui/ZikWall.WinUI/Assets/Flags/NOTICE.md) are bundled. SVG extraction accepts only bounded, two-letter country assets and rejects active/external content. All 258 SVGs, including ZikWall's original unknown-location globe, decoded successfully with WinUI's SVG renderer.

The conversion scripts verify pinned download SHA-256 values before parsing. They do not execute downloaded code. Generated country and flag assets have recorded hashes checked by `tools/test-supply-chain.ps1`. There are no runtime data downloads or additional NuGet dependencies for this feature. Hashes establish consistency with the reviewed inputs, not publisher authenticity or an absence of compromise.

Rebuild from the documented downloads with `tools/build-country-data.py` and `tools/extract-monitor-flags.py`; paths and hashes are recorded beside the assets. Country-data updates are explicit build-time changes requiring new provenance and validation.

## Recent connection notifications

In-app recent alerts default on. Windows notifications are separately opt-in under Monitor options. Both report **newly observed Internet endpoints**, not exact connection creation times or firewall decisions. Startup/resume/enabling alerts suppresses the next baseline, local endpoints are excluded, repeated app/destination/port/direction combinations are deduplicated for five minutes, and batches are limited to once per 30 seconds. Tracking and pending samples are bounded. Windows alerts are silent and replace the previous monitor toast. Clicking one activates the app. Windows notification settings or Focus Assist can prevent display even after submission succeeds.

Notifications use a compact three-line layout: "Network activity" (or "First network activity" for the new-app notification mode), app name, and remote IP. Additional batched connections appear as "+N more" on the address line. The bundled country flag remains beside the content, with an accessible country description and a neutral globe for unknown countries. Ports, hostname explanations and country prose remain in connection details. Windows controls the native toast frame and app attribution. Formatting uses existing snapshot data and local images; it makes no DNS, signature or image requests and does not invent an unsigned badge.

Activity remains the detailed record. Saved history stays opt-in; notification settings are saved with monitor preferences. Notifications can contain app names, cached domain hints and IPs, including in Windows Notification Center when enabled.

Monitor options → Notifications → **Notify me about** selects all new Internet
connections, **New apps only**, outgoing only, or incoming only. The selection
applies to both the in-app banner and optional Windows notifications and survives
restart. Direction-specific choices exclude unknown direction. Existing installs
keep the all-connections default.

New-app detection remembers application paths (case-insensitively) or package
identities for the current session; unidentified processes use PID and start
time. A different destination or restarted process from the same known path does
not announce another new app. Startup/resume baselines, muted apps and apps seen
while notifications are off are remembered silently. Changing notification type
discards the old pending batch. The memory is capped at 16,384 identities; once
full, additional apps are not announced as new until the next session. It is not
a persistent inventory of installed applications or a firewall connection prompt.

## Validation

The automated suite covers real IPv4/IPv6 initiating and accepted sockets, country table boundaries and invalid headers, all dataset country-code/flag mappings, private/reserved/mapped IP classification, backward-compatible history JSON, filters, startup suppression, cooldown/deduplication, disabled alerts and degraded country-data behavior.

The event-notification formatter has 19 additional checks for state/direction wording, cached-domain hints, IPv6 formatting, batch counts, unknown countries, bundled flag paths, XML escaping, bounded labels and missing assets. The native diagnostic records the production toast XML and submits a labeled example through the same notification service. Windows notification submission and SVG decoding are tested; desktop appearance remains outside the automated render check.

The explicit `ZikWall.exe --test-monitor` diagnostic loads the application's real executable icon, decodes every bundled SVG, registers with Windows notifications, submits one labeled silent test notification and renders the native window to a local PNG. It changes no firewall/DNS settings. Diagnostic outputs stay under ignored `artifacts/ZikWall`; rendered live data is not committed. Submission/registration succeeded locally; actual desktop toast appearance and activation were not independently observed. RenderTargetBitmap validates XAML layout/content but does not capture compositor Mica or all native window chrome.

See [performance measurements](PERFORMANCE.md) and [remaining Little Snitch gaps](LITTLE-SNITCH-COMPARISON.md).

The native churn regression runs 500 deterministic insert/remove/replace/reset cycles with executable icons, flags and alert-banner resizing. During validation, a live run failed inside WinUI with `0xc000027b`; this stress test reproduced it quickly. Disabling banner resizing or replacing incremental updates did not fix it; disabling executable icons did. Separate per-row bitmaps improved isolation but did not resolve the complete test. Deferring icon visual changes through the UI dispatcher, outside the dependency-property callback used during native template binding/layout, removed the reproduced failure. Icons retain a bounded pixel cache and per-row native bitmap ownership. `tools/test-monitor-ui.ps1` runs the regression twice by default; `-RenderAndNotify` also runs the explicit render/toast diagnostic. Temporary exception logging and isolation switches were removed.

### Compact notification validation (October 13, 2026)

The Release build and full model/service checks pass. The focused
`ZikWall.exe --test-notification` diagnostic successfully submitted a labeled,
silent Windows notification using three text lines and the bundled country flag.
[Submitted payload](benchmarks/2026-10-13-simple-notification.json). Windows owns
the toast frame; submission confirms API acceptance, not a desktop screenshot.
The broader monitor render diagnostic timed out; the focused notification check
completed successfully without that unrelated rendering path.
