# ZikWall compared with Little Snitch for macOS

Research and implementation update: 12 October 2026. This comparison covers the full **Little Snitch 6 for macOS**, using Objective Development's product documentation, not Little Snitch Mini or the Linux edition. ZikWall means this repository's new WinUI 3 application and separate Windows SDK WFP broker. The original C/simplewall engine remains a separate implementation. See [implementation status and validation](FEATURE-STATUS.md).

## Does ZikWall show connection IP addresses?

**Yes.** The startup Connections page shows live TCP IPv4/IPv6 local and remote addresses, ports, process/PID and Windows-reported state. UDP owner tables provide local endpoints only, so ZikWall explicitly leaves their remote destinations unavailable. Sampling defaults to five seconds for lower resource use, with two-/ten-second choices and slower background sampling. This can miss short-lived connections. Observed endpoints are not proof of an allow/block decision. Reverse DNS is optional and clearly labeled as a hint.

Little Snitch does show actual connection IP addresses, TCP/UDP ports, protocol and hostname in its connection inspector. It also exposes process information, traffic totals/rates and last allowed/denied times. [Official connection inspector](https://help.obdev.at/littlesnitch6/lsm-inspector)

The monitor now includes app icons, TCP direction, offline country flags and batched recent-connection notifications; see [monitor details](MONITORING.md).

The new monitor directly calls Windows IP Helper APIs without elevation; see [ConnectionMonitor](../ui/ZikWall.WinUI/Services/ConnectionMonitor.cs). The original engine has separate [network enumeration](../src/network.c), [event structures](../src/main.h) and [logging](../src/log.c).

## Current feature comparison

| Area | Little Snitch for macOS | Current ZikWall |
| --- | --- | --- |
| Live connection monitor | App/domain/server grouping, current data rate, denied-connection indication and connection history. [Connection list](https://help.obdev.at/littlesnitch6/lsm-connection-list) | TCP app icons, IPs/ports, incoming/outgoing direction, offline country flags, app/remote-IP grouping, recent observation alerts, search and CSV export. UDP local endpoints only. Missing reliable requested-domain attribution, denied events and per-process rates. |
| Bandwidth and history | Upload/download charts with up to a year of history; selectable grouping and time filters. [Monitor overview](https://help.obdev.at/littlesnitch6/lsm-overview) | Adapter charts and optional bounded seven-day history, with 1-hour/24-hour/7-day filters. Missing per-app accounting and long-term storage. |
| Geographic view | Map based on locally stored IP geolocation databases. [Monitor overview](https://help.obdev.at/littlesnitch6/lsm-overview) | Bundled offline country flags plus an offline map with user-imported coordinate CSV. Locations are approximate. |
| Connection decisions | Alert mode asks about unmatched connections; silent allow/deny modes record undecided activity for later review. [Operation modes](https://help.obdev.at/littlesnitch6/concepts-opmodes) | Explicit scoped session rules through WFP. Missing unmatched-connection prompts, default-deny modes and silent review. |
| Effective rules | Matching by app, hostname/domain, IPv4/IPv6/range, protocol, port and direction; rule precedence determines the action. [Rules](https://help.obdev.at/littlesnitch6/concepts-rules) | Ordered path/IP/CIDR/protocol/port/direction rules. Outbound IPv4 TCP block/removal runtime-tested. No per-app domain rules; broader WFP cases remain to be validated. |
| Temporary permissions | Rules can expire after a duration, on process exit, logout or restart. [Rules](https://help.obdev.at/littlesnitch6/concepts-rules) | Broker-enforced timestamp expiry runtime-tested; all rules end with the ZikWall session. No target-process-exit lifetime. |
| Network profiles | Separate rule sets with automatic switching when joining an associated network. [Profiles](https://help.obdev.at/littlesnitch6/concepts-profiles) | Saved profiles with opt-in Windows network-GUID associations. Ambiguity/pending edits pause switching. Active-filter switching still needs end-to-end testing. |
| Blocklists and subscriptions | Downloadable domain/IP blocklists with periodic refresh and individual exceptions; remote rule groups are also available. [Blocklists](https://help.obdev.at/littlesnitch6/concepts-blocklists), [management](https://help.obdev.at/littlesnitch6/lsc-blocklists), [product features](https://www.obdev.at/products/littlesnitch/index.html) | Bounded HTTPS subscriptions, optional hash pins, opt-in refresh, DNS-level domain exceptions/blocking. IPs copy to saved policy as a snapshot; no ongoing policy synchronization. |
| Application identity | Signature-derived code IDs can identify apps across moves/updates, and check identity for path rules. [Code identity](https://help.obdev.at/littlesnitch6/adv-code-identity) | Offline Windows signature trust inspection and SHA-256. Enforcement remains path-based; no signature/hash-bound rules. |
| DNS encryption | Built-in DNS proxy supporting DoH, DoT and DoQ with configured exceptions. [DNS encryption settings](https://help.obdev.at/littlesnitch6/pref-dnsencryption) | Local UDP/TCP proxy forwards using validated DoH with no plaintext fallback. Adapter setup is manual; no DoT/DoQ or per-app attribution. |
| Rule maintenance | Redundant/invalid rule discovery, usage statistics and rule suggestions. [Product features](https://www.obdev.at/products/littlesnitch/index.html) | Read-only diagnostics for full scope coverage, temporary coverage, inactive rules and unavailable executables. No usage counts, combined-rule analysis or automatic cleanup. |

The ZikWall column is grounded in [current implementation status](FEATURE-STATUS.md), [WFP broker and runtime tests](WFP-BROKER.md), [DNS and subscriptions](DNS-SUBSCRIPTIONS.md), [map provenance](MAP-DATA.md) and [policy diagnostics](../ui/ZikWall.WinUI/Models/PolicyAnalysis.cs). These are implementation findings, not claims about all versions of upstream simplewall.

## What the existing simplewall code already provides

It would be misleading to say all of the above requires a firewall engine from scratch. The native code already includes:

- WFP application, address, port, protocol and direction filters, with built-in/system/custom rule paths: [wfp.c](../src/wfp.c).
- Application notifications with allow/block decisions and timed permissions: [notifications.c](../src/notifications.c).
- IPv4/IPv6 endpoint enumeration and process paths: [network.c](../src/network.c).
- Traffic-counter code in this checkout, using TCP EStats and a UDP statistics collector: [network.c](../src/network.c), [udpstats.c](../src/udpstats.c). This is not a validated WinUI monitor or a year-long historical database.
- File logging, internal blocklists and signature information: [log.c](../src/log.c), [profile.c](../src/profile.c), [helper.c](../src/helper.c).

These are source-level observations about the original engine. The reviewed public `routine` dependency does not build compatibly with this checkout. ZikWall's new Windows SDK engine avoids that dependency and has separate scoped runtime evidence. [Build and security review](SECURITY-REVIEW.md)

## Hostnames require more than reverse DNS

Little Snitch uses names supplied by macOS, per-process DNS observations and initial protocol headers to associate connections with hostnames. It falls back to IP addresses when no unique name can be established; incoming connections are displayed numerically. It does not decrypt TLS to do this. [Server-name determination](https://help.obdev.at/littlesnitch6/adv-server-names)

The existing simplewall helper includes PTR reverse lookup. A reverse-DNS label is not necessarily the domain an application requested, particularly on shared hosting/CDNs. Therefore a future ZikWall hostname column should distinguish observed/requested names from reverse-DNS hints; domain-based enforcement needs a separate reliable attribution design. [Existing PTR lookup](../src/helper.c), [Little Snitch's explanation of name ambiguity](https://help.obdev.at/littlesnitch6/adv-server-names)

Similarly, displaying an Authenticode signer is not the same as binding a firewall rule to verified application identity. The current native filter path uses WFP application IDs, while signer information is gathered separately. [Native filter construction](../src/wfp.c), [signature checks](../src/helper.c)

## Remaining implementation priorities

These are remaining tasks, not completed functionality:

1. Extend scoped WFP runtime tests to IPv6/inbound/UDP, precedence and adversarial IPC/lifecycle cases.
2. Capture blocked/unmatched events and design connection decisions with explicit fallback behavior.
3. Add per-process traffic accounting and reliable app-requested hostname attribution.
4. Bind policies to verified executable identity and synchronize subscribed IP rules safely.
5. Validate active network-profile switching, add coordinate-level map data and extend DNS/history features.

Windows 11 styling and the ZikWall branding can be complete while these engine features remain unfinished. A native WinUI build alone does not establish Little Snitch feature parity.

## Monitor revision update

The current UI now has expandable package/executable → process → connection rows, tray/sign-in startup and an offline country map with selectable app/IP details and bounded activity pulses. A session-based ETW observed-traffic collector and 30-day per-app daily ledger have been implemented. Native accounting validation remains pending because the UAC diagnostic was canceled; it is not yet established Little Snitch traffic-accounting parity. See [monitor behavior](MONITOR-AND-USAGE.md) and [measurements](PERFORMANCE.md).
