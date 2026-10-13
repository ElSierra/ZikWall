# Hostnames, My activity and Log analysis

Implemented October 13, 2026. The three supplied screenshots define the visual
reference. ZikWall uses its own WinUI controls, assets and monitoring data.

## Investigation and evidence

REA/Ghidra 12.1.4 inspected the installed x64 artifacts without executing or
changing GlassWire:

| Artifact | SHA-256 | REA overview evidence |
| --- | --- | --- |
| `C:\Program Files\GlassWire\GWCtlSrv.exe` | `e13893ac68ccdcc48840193793384b200398c94cf9af47106e4fd038cb893095` | `ev_53edd2b0060d7d7375207fe18936f6403c2877d6e96c5373f0965f9d9fe0fe8e` |
| `C:\Program Files\GlassWire\GlassWire.exe` | `857c6ef88a4caaff9b9b13507492d1c9699122eb1393ff72e20dcaf33458372c` | `ev_bad6cfde3d78ab6d011b0b44f27429482717c7fe34bf4b83da6ca3fc065553f1` |

Both have `.themida` and `.boot` sections. The service exposes only 32 analyzed
procedures / 105 defined strings; the UI exposes 66 / 133. Focused DNS/hostname
searches returned no defined-string matches (`ev_6008ac14efc5764d6a2affd4e4c7650bef3b082738cebb41b1851ed795bf32e3`
and `ev_f4378cb2666955bd6a41d2df0cdb7bebd016950ec7ec8f675454c08dcdd96563`).
This is limited coverage of protected artifacts, not evidence that they lack
hostname resolution. At this static-analysis stage the resolver implementation,
cache priority, TTL, packet inspection and runtime attribution were unresolved.
The later controlled DNS investigation below establishes packet capture and
hostname learning. No decompiled service-resolver claim is made. The native REA
session was closed after exporting its
four records to `temp/glasswire-rea-evidence.json`.

The shipped Qt translation catalog supplies additional static UI evidence. A
bounded parser verified the QM magic and message-block/tag lengths, then decoded
source/context strings in `C:\Program Files\GlassWire\lang\de_de.qm`
(SHA-256 `a2e2dde3183d5ea792a9ba3e901e988d5e8765cd54546bb9865b1d6ae90d1fc2`).
Offsets below identify the start of the containing message record:

| Offset | Context | Source text / observation |
| --- | --- | --- |
| 139887 | `Strings::SettingsDialog` | “Look up DNS names for hosts (nslookup)” — establishes an optional name-lookup setting, not its internal algorithm. |
| 85855 | `Strings::FirewallPage` | “Hosts” — app host column. |
| 104810 | `Strings::HostInfoListWidget` | “%1 (%2 hosts)” — grouped host summary. |
| 41284 | `Strings::AlertsPage` | “%1 initiated its first network connection to %2”. |
| 43723 | `Strings::AlertsPage` | “Mark all as read”. |
| 138221 | `Strings::SettingsDialog` | Important alerts are unread by default and have a dedicated Important section. |
| 139560 | `Strings::SettingsDialog` | “Log Analysis”. |
| 139715 | `Strings::SettingsDialog` | “Logs alerts are read by default.” |
| 167940 | `Strings::UsagePage` | “Hosts” — host usage ranking. |

The parser and extracted observations are retained in `temp/inspect-glasswire-qm.py`
and `temp/glasswire-ui-evidence.json`. These catalog observations corroborate the
screenshots; they do not prove the running application used any particular branch.

### Follow-up: REA GitHub runtime support

Checked repository commit `30e9b83a68c4729745ca6413ea306dbffb30f07a` on October 13,
2026. The earlier statement that REA's native runtime tracing is macOS-only was
too broad: it applies to `observe_native_calls`, whose provider explicitly checks
`platform === "darwin"`, but repository main now has a separate Frida provider.

- [Frida support commit](https://github.com/morluto/rea/commit/4a4ae9b2b9712f7ac16f2d23c71b5c29b0c23d2f)
  added instrumentation on October 12. The implementation attaches by PID,
  creates/loads a caller-supplied script, captures messages and detaches during
  cleanup. Its adapter has no macOS-only platform gate.
- [Provider guide](https://github.com/morluto/rea/blob/30e9b83a68c4729745ca6413ea306dbffb30f07a/docs/frida-instrumentation.md)
  documents eight MCP tools, including `start_frida_session` and
  `load_frida_script`, plus the `instrument-with-frida` CLI command. The optional
  Node dependency is Frida 17.23.2. This is a separate path from Ghidra/LLDB.
- The connected server is the installed npm **6.4.0**, exposing 170 MCP tools
  without any Frida tools. Inspection of its package and compiled files also
  found no Frida dependency/provider. npm's current `latest` metadata reports
  6.4.0 at release commit `a56f7bb668ab23fec8d33f7af353ac7ed2fe1895`, with no
  Frida optional dependency. Repository main still carries the same version
  number, so version strings alone do not establish capability parity.
- [Frida itself supports Windows](https://frida.re/docs/installation/), while REA's
  guide documents its real-provider verification on Linux. At source-discovery
  time, Windows/GlassWire attach compatibility had not been tested here. The
  subsequent Windows verification and successful service attachment are recorded
  below; attachment does not by itself recover GlassWire's resolver algorithm.

The concrete next path is an isolated build of the pinned REA main revision with
its optional Frida binding, a harmless Windows fixture to verify attach/message/
detach behavior, then narrowly scoped DNS API observation of the selected
GlassWire process. Attaching to its SYSTEM service may require administrator
rights. Observe lookup/cache-related calls and correlate with controlled host
display changes; absence of a hooked call alone would not establish absence of
DNS observation. A normal `@latest` reinstall or client restart cannot supply
code absent from the published package.

The initial follow-up downloaded and read the public repository and release
metadata. The authorized build follow-up installed locked dependencies, including
Frida 17.23.2, only in `temp/rea-source`; the installed REA package and MCP
registration remain unchanged.

### Windows Frida verification

The pinned checkout initially failed TypeScript compilation in the unrelated
Android adapter: the result schema permits an absent `direct_invokes_exhaustive`
field, whereas the adapter promises JSON values. An isolated local patch in
`src/android/JadxAnalysis.ts` normalizes that field to `null` after parsing.
`npm run build:cached` then passed. This checkout is therefore the pinned commit
plus that nine-line diff, not an unmodified upstream build.

- `scripts/verify-frida.mjs` passed on `win32`: spawned Node, loaded the marker
  script, received the expected PID, and reported `cleanup_error: null`.
- A separate real CLI attach test against an owned Node fixture resolving
  `localhost` captured eight `GetAddrInfoW`, sixteen `DnsQueryEx`, and eight
  `DnsQueryConfig` calls in five seconds. It reported no script errors, no
  truncated messages, and no cleanup error. Evidence:
  `ev_7eaa7e48d3c54cd530cf05b6a853311711860b4000f0bd63753d04ca99c657dc`.
  The fixture was stopped by its owning harness after the CLI detached.
- Artifacts: `temp/rea-frida-windows-verification.json`,
  `temp/rea-frida-dns-fixture.json`, `temp/verify-rea-dns-hook.mjs`, and
  `temp/glasswire-dns-trace.js`. The hook observes DNS/cache APIs without changing
  arguments or return values. Capture is capped at 500 script messages.
- The non-admin GlassWire attach returned `Process not found` before injection.
  Windows CIM still saw `GWCtlSrv.exe` PID 6544 and the running `GlassWire`
  LocalSystem service, while Frida's process list omitted it. This demonstrates
  limited visibility under that token; it does not demonstrate Themida blocking
  instrumentation. A Windows UAC-elevated, 30-second trace was then launched,
  with executable path and SHA-256 checks before attachment.

### GlassWire runtime results

Windows UAC elevation supplied a real administrator token. The same REA provider
then attached to the hash-verified `GWCtlSrv.exe` LocalSystem service successfully.
The initial 30-second observation captured 55 `getaddrinfo` and 55 nested
`GetAddrInfoW` calls; every argument was a numeric IP address. These are address
parsing observations, **not evidence of hostname discovery or reverse DNS**.
Fourteen DNS/Winsock hooks loaded successfully, with no script errors or message
truncation. Cleanup returned `null`; the service remained Running with PID 6544.
Evidence: `ev_4ea2f4742cf7ab3a256e84a1f39453589d57447c1392749433fae5f19dde04cb`
in `temp/glasswire-frida-admin-first.json`.

A second 30-second pass added DNS packet parser/builder API hooks. It captured
94 calls to each address-parsing API and no calls to the hooked DNS query,
reverse lookup, cache enumeration, or packet parser APIs. Evidence:
`ev_5c107fef5a31b2ca82a6b1d7e83c94d04302bf391dfb2694ffde8adf2c5853dc`
in `temp/glasswire-frida-admin-second.json`.

A third pass ran controlled DNS lookups and short TCP connections to port 443
of `www.iana.org`, `www.example.org`, and `www.example.net`. All three connected;
no application request or payload was sent. The service observation again only
captured numeric address parsing (87 calls to each API), with clean detach and
the original service PID still running. Evidence:
`ev_a5f98dfdb903d8411bf9187d762aa71d4d71879cba96cf225895631d4b3c0de9`
in `temp/glasswire-controlled-trace.json`. The read-only database comparison
found no matching example-domain rows before or after this window, and the
hostname-row count remained 382,050. Delayed persistence, collection scope, and
other data paths remain possible; this is not proof of missing live collection.

A final pass added `sendto`, `recvfrom`, `WSASendTo`, and `WSARecvFrom` hooks,
filtering captured payloads to DNS questions on port 53. All 22 hooks loaded;
there were 34 calls to each address-parsing API and no captured DNS datagrams.
This observer does not cover pending overlapped receive completions, connected
UDP `send`/`recv`, TCP DNS framing, encrypted DNS, kernel packet capture, or mDNS.
It is a bounded negative result, not proof that the service never handles DNS
packets. Evidence:
`ev_e7cc2f937f60657f03ae8c81f14f21e02b755f5f273be2d9590eb244649759cc`
in `temp/glasswire-frida-admin.json`. Cleanup succeeded and the service remained
Running with its original PID. The expanded script was first reverified on the
owned Node fixture (22 hooks, no script errors, clean detach), evidence
`ev_34d84fdba565eb04d815df783a4a61bdce1769d2bb68518e184267480dd25a92`.

Read-only inspection of the service's configuration and SQLite schema established:

- `NslookupEnabled = false` on this installation. Optional active name lookup was
  disabled during these observations; settings were not changed.
- `NslookupResolversLimit = 5`, `NslookupCacheLifetime = 600`, and
  `NetworkEnableMdns = true`. These stored values do not establish runtime
  concurrency, time units, or which code paths were executed.
- `glasswire.db` contains `CREATE TABLE hostnames (address BLOB, hostname BLOB,
  type INTEGER)`. Two bounded encoding samples showed plaintext hostname bytes
  and structured address blobs. All counted rows had type 1; its semantic meaning
  is unknown. This establishes persistent hostname storage, not how each stored
  name was obtained. `storage.db` could not be opened by the SQLite reader.

The conclusion of these initial API traces was that **REA's new Frida provider works on this Windows
host, and administrator access allows observation of the running GlassWire
service**. The original installed REA release lacked this provider. Neither
Themida nor Windows prevented this bounded observation. The exact passive
hostname acquisition, attribution, cache precedence, and historical record origins
were still unresolved; no claim that GlassWire uses the same implementation as
ZikWall follows from these traces. Local evidence and the bounded UAC helper are
under `temp/`; no installed files, firewall rules, DNS settings, or GlassWire
settings were modified.

### Controlled DNS response and cache investigation

**Confirmed mechanism: GlassWire learns hostnames from captured DNS responses,
independently of its optional reverse lookup.** The following evidence separates
the vendor's description, this installation's runtime behavior, and the remaining
implementation details.

The decisive test on **GlassWire 3.10.1138.0** followed the same fresh name through
the entire observed path:

| Stage (UTC, October 13, 2026) | Observation |
| --- | --- |
| Before test | No name or IPv4-address record for the selected `1.0.0.1` in `hostnames`. |
| 15:03:13.485 | The owned Node probe queried `rea-mv6t90tb.1-0-0-1.sslip.io`; the router returned A=`1.0.0.1`. A second direct query to Cloudflare returned the same answer. |
| 15:03:13.971 | Frida observed that exact question and A answer in the output buffer received from `\Device\GlassWireGeneric`, control `0x126006`; response TTL was 3600. |
| After lookup | The probe completed a TCP connection to `1.0.0.1:443`, sending no HTTPS request. |
| 15:03:38.720 | The same unique marker appeared in a 4,096-byte service write buffer, with the same database-writing call stack identified in the earlier WAL trace. |
| After trace | A read-only SQL query found the exact hostname in `hostnames`, address blob `08011081808008`, type 1. |

Reverse lookup remained disabled. The trace reported no script errors or cleanup
error; the service remained Running with its original PID. Evidence:
`ev_bb520f05d871ae3f8e06183ca79e53210f9ac89b928877537831244b48a85895`.
The compact, reproducible finding record is
[GLASSWIRE-HOSTNAME-TRACE.json](GLASSWIRE-HOSTNAME-TRACE.json); full local artifacts
are `temp/glasswire-marker-flow.json`, `temp/glasswire-marker-probe.json`,
`temp/glasswire-marker-trace.js`, and `temp/glasswire-marker-status.json`.
The unique-name marker was the only payload selected for this final trace.

This directly establishes DNS-response delivery from GlassWire's driver followed
by persistence of the learned hostname. It does not recover every internal
parser function or the exact alias-selection/persistence policy. A preceding
reserved-address test received the DNS response but timed out its TCP attempt
and produced no persistent row within 45 seconds; that contrast does not prove
successful connection is the sole persistence condition. That experiment is
retained in `temp/glasswire-marker-flow-reserved.json`, evidence
`ev_7bdf585e01741397664d75018ca9a708ce9f016d7dd4e11e39106adbf2dc0492`.

GlassWire's [official v2.1.166 changelog](https://www.glasswire.com/changes/)
(September 17, 2019) explicitly says hostname display can continue with nslookup
disabled because of DNS packet inspection. In a
[September 23, 2019 staff explanation](https://forum.glasswire.com/t/any-feedback-yet-on-update-v2-1-166-released-sept-17th/8865),
Ken_GlassWire describes parsing forward DNS replies to obtain the queried name
and returned IP addresses, separately from reverse `.in-addr.arpa` requests.
That post also identifies encrypted browser DNS as a limitation when reverse
lookup is disabled. A [January 2022 staff explanation](https://forum.glasswire.com/t/how-does-glasswire-capture-https-traffic/13116)
says HTTPS contents are not decrypted. These are primary-source explanations,
not proof of an exact internal function in the current binary.

The current installation was then tested with unique names under `sslip.io`,
using a Node application-local DNS resolver explicitly targeting the router and
Cloudflare. This does not modify Windows resolver settings or use the Windows
DNS cache to supply the answers. No HTTPS application payload was sent.

- `rea-mv6sts7w.1-1-1-1.sslip.io` was absent from the GlassWire database before
  the probe. Both resolvers returned `1.1.1.1`; after a short TCP connection and
  a bounded observation, the **exact name appeared in `hostnames`**, with type 1
  and address blob `08011081828408`. The service's database WAL writes were
  observed. This is a fresh learned name, not merely an old cached hostname.
  Artifacts: `temp/glasswire-fresh-probe.json`, `temp/glasswire-cache-flow.json`.
  Evidence: `ev_6a52fed7a74de2ce8b284b17759a5c40a45fd3287cd24b0b7ef5e5268042a238`.
  This exploratory script stopped emitting after 600 observations, so its event
  list is intentionally incomplete; the final marker trace above did not hit its cap.
- A second unique query, `rea-mv6szuwx.1-1-1-1.sslip.io`, was observed inside a
  **DNS response in the service's driver output buffer**. The handle was resolved
  at runtime to `\Device\GlassWireGeneric`; the receive control was `0x126006`,
  using a 1 MiB output buffer and asynchronous status `0x103` (`STATUS_PENDING`).
  The captured DNS header had response flags `0x8180`, one question and one
  answer. Its A answer was `1.1.1.1`, with TTL 3600. The marker was found at
  buffer offset 97 before the next receive reused the buffer. Service call
  stack RVAs included `GWCtlSrv.exe+0x10690e2`, `+0x105fa8b`, and `+0x1061352`.
  Evidence: `ev_aa4fd1822d5153aedf8ef2d0c8c5ee1a7ab096dfdc02f39138b2d3c880881681`
  in `temp/glasswire-marker-flow-first.json`. This second name did not appear in
  the persistent table within 45 seconds; it used the same IP as the first test.
  This does not establish an alias replacement or persistence policy.
- The running `gwdrv` service points to
  `C:\Windows\System32\drivers\gwdrv.sys`. Its SHA-256 matches the shipped
  x64 driver: `b4dc2ba0e2a8a35711815f94a583ef2f7cf9c46a901542e5eaa04f2fe96a74b3`.

The driver imports WFP callout/packet APIs and `NdisGetDataBuffer`; Ghidra
identified 344 procedures without the restricted coverage of the protected
service. Function `0x140004abc` copies data from network buffers into an owned
buffer. This supports the
packet-collection path but does not identify the service's complete DNS parser.
Driver evidence was exported to `temp/glasswire-driver-rea-evidence.json` and the
Ghidra session closed. No kernel debugger or driver modification was used.

For ZikWall, the important gap is **passive DNS-answer collection and retention**.
Its existing Windows-cache hints and reverse-DNS fallback are different sources.
Capturing the DNS question plus A/AAAA/CNAME answers can retain the original name
before the OS cache expires or omits it, then match returned IPs to connections
from browsers and desktop apps. A shared IP may have several valid names; the
specific application's requested name is not always provable from an IP match.
Encrypted DNS that bypasses observable plaintext replies remains a separate
coverage limitation. No SNI-based mechanism was established by this investigation.

## ZikWall behavior

- **Connections → Hosts / status:** name or IP, flag and `+N more`; select the
  host cell for addresses and connection counts. Hosts are distinct IPs, not
  aliases or ports. The flyout bounds display to 128 entries and labels that cap.
- **Monitor options → Website names → Capture website names** now learns names
  from captured ordinary DNS replies, including app-local resolvers. Captured
  question names take precedence over Windows cache hints; both keep optional
  reverse DNS as a fallback. This uses Windows' built-in capture provider with
  session-only TTL caching, not GlassWire's driver or proprietary code. Source
  labels distinguish observed replies, Windows cache and reverse DNS. See
  [implementation and coverage](DOMAIN-HINTS.md).
- **Monitor options →
  Website names → Look up missing hostnames** enables an optional reverse-DNS
  fallback. It defaults off, persists its setting, sends public IP lookups through
  Windows DNS, and never requires the elevated cache helper. Cache and reverse
  results are labelled separately; neither proves the website requested by an app.
- Reverse lookups run off the UI thread, at most eight IPs per minute (two minutes
  in background), with a two-second cancellation deadline per request. Positive
  results remain for ten minutes, failures for two; memory is capped at 2,048
  IPs. The OS owns cancellation of any underlying resolver operation. Local,
  reserved, listening and unavailable UDP destinations are excluded. Disabling
  clears results and cancels pending work. IP identity and firewall targeting
  are unchanged. Names are omitted from activity JSON; explicit CSV export has
  separate domain/reverse hint columns and a hostname-source column.
- **My activity:** responsive Apps / Hosts / Traffic type / Countries rankings,
  yellow download and pink upload bars, search, scope filter and 5-minute,
  1-hour / Today ranges. App bytes come from ETW counters. Short ranges use
  completed counter samples; initial snapshots establish a baseline. Host,
  protocol and country rankings explicitly count observed connections because
  the existing collector does not attribute bytes to remote endpoints. Current
  endpoints are included. Enabling usage follows the existing usage flow.
- **Log analysis:** Today/Yesterday/date groups, category/time/search/unread
  filters, event details, and Mark all as read. First app observations in retained
  history are important/unread; ordinary logs start read. This is deliberately
  described as a retained-history observation, not the app's first-ever network
  connection. Only the read-through timestamp is stored separately. History
  keeps the existing opt-in persistence, seven-day/10,000-event limit; display is
  capped at the newest 1,000 matches with the full matching count shown.

## Validation

The Release WinUI build passes with no warnings. The full model/service suite
passes, including real loopback TCP/UDP checks, 17 hostname/reverse-DNS checks,
13 overview checks and 17 log checks. Reverse-DNS unit tests inject a resolver;
they do not send public DNS queries.

Native diagnostics: `--test-activity-overview`, `--test-log-analysis`,
`--test-monitor-options` and `--test-usage-view`. They exercise real WinUI rows,
grouping, filtering and responsive layouts with fixture data. The first two save
PNG previews under `artifacts/ZikWall`. No firewall rules, DNS adapter settings,
notifications or installed application files are changed by these diagnostics.
