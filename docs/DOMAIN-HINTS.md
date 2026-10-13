# Hostname hints

Connections now has a Hosts column with a name/IP and an expandable host list.
The existing Windows cache provider below remains the unelevated default. An optional
**Look up missing hostnames** switch adds a bounded reverse-DNS fallback and
sends public-IP lookups through the Windows resolver. It is off by default.
See [GlassWire investigation and implementation](GLASSWIRE-FEATURES.md) for
evidence, lookup limits, source labels and validation.

ZikWall reads existing Windows DNS cache answers and matches their IPs to live
connections. Names appear in collapsed app/process summaries, expanded connection rows, live activity history, tooltips, Details, search
and explicit CSV exports. IP addresses stay visible and remain the identity used
for connection tracking and firewall targets. Saved activity JSON excludes names.

Enable **Monitor options → Website names → Capture website names** to learn
names from ordinary DNS replies, including replies to applications that bypass
the Windows resolver cache. The UAC helper authenticates the parent process and
pipe peer, accepts only status and validated IP requests, and starts a separate
passive capture session without instantiating the firewall engine. It exits on
disconnect, parent exit or 150 seconds without a request. Startup remains unelevated.

The helper uses the documented Windows NetEventPacketCapture controls and an
NDIS ETW consumer. Its uniquely named real-time session captures IPv4/IPv6 UDP
packets; the consumer rejects non-DNS traffic without retaining it. Kernel capture
is **UDP-wide**, not port-53-only. No third-party driver is installed and no packet
file is written. A hidden Windows PowerShell controller configures the built-in
provider and removes only its own session when the broker closes stdin or exits.
Capture across network layers is enabled: on the tested Intel Wi-Fi adapter,
single-layer capture saw the test query but missed its reply. Layer duplicates
update the same cache entries; the status counts observations, not unique replies.
An occupied/unsupported provider produces a visible failure and leaves Windows
cache hints available. It never stops someone else's capture to claim the provider.

Complete DNS responses on UDP source port 53 supply A/AAAA addresses and the
original question name through a validated CNAME chain. Ethernet, up to two VLAN
tags, raw-IP media, native Wi-Fi data/QoS frames carrying decoded LLC/SNAP, IPv4
options and bounded IPv6 extension headers are supported.
Truncated packets, IP/Wi-Fi fragments requiring reassembly, Wi-Fi aggregation,
DNS over TCP, encrypted Wi-Fi payloads and virtual-switch event formats are not decoded. DoH/DoT cannot be read
by this capture. Names are IP associations, not per-process proof of a website.

Observed replies take precedence over Windows cache hints, followed by optional
reverse DNS. The UI labels the source and CSV includes `HostNameSource`. The
observed cache holds at most 8,192 address/name pairs, returns up to three recent
names per requested address, and expires by the minimum answer/CNAME TTL capped
at one hour. It lives only for the capture session; payloads and names are never
written to activity JSON. Explicit CSV exports can contain displayed names.

Website names are at the top of Monitor options. The status distinguishes a
successful lookup with no matching names from denied access or a provider failure.
Activity rows refresh names from the current cache without saving them in history.
The encrypted DNS proxy is separate: starting it does not enable elevated cache
access or reveal full HTTPS URLs. Browser-private DNS may leave no Windows cache
entry to display.

## Cost controls

- One background worker; the connection capture/UI path never waits for it.
- Without elevated capture, first scan immediately, then once per 60 seconds
  foreground or 120 seconds background. With capture, memory snapshots may refresh
  every two seconds foreground / ten seconds background, driven by monitor ticks.
  The helper still limits expensive Windows cache enumeration to once per minute.
  Observed snapshots are authoritative, so removal/expiry cannot renew a stale hint.
- A fixed, read-only query of Microsoft's documented
  [MSFT_DNSClientCache](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/dnsclientcimprov/msft-dnsclientcache)
  provider, using pinned `System.Management` 8.0.0. Cache reads make no DNS requests.
- Forward-only batches of 64; at most 16,384 A/AAAA answer records inspected,
  2,048 observed remote IPs retained, three names per IP. Unspecified addresses,
  failed answers, authority/glue records and invalid names are excluded.
- Windows-cache TTL expiry is capped at two minutes; captured replies at one hour,
  observed on the next monitor refresh.
  Allocation-free canonical-IP dictionary lookup and throttled refresh checks.
- Missing provider/access failures leave IP monitoring usable. Repeated
  access-denied scans are suppressed until a new reader session.

Windows owns the provider's enumeration and memory use. Its three-second
enumeration timeout is not a hard bound on every Windows operation; slow calls
remain off the UI thread. The limits above bound ZikWall's retained data and
client-side processing. Very large caches can exceed the inspection limit and
leave some names unknown.

Capture adds ETW processing, a broker and a Windows PowerShell controller. Its
requested buffer pool is at most 32 × 64 KiB, subject to Windows' minimums; it is
not the total process footprint. Capture loss/unsupported-packet counters appear
in status after a matching reply. No zero-overhead or throughput guarantee is made.
The older measurements below concern Windows cache lookup, not this capture path.

## Measurements on this PC

October 13 capture validation: a unique direct UDP query bypassing the Windows
resolver cache was matched to its observed DNS reply and published through actual
connection enrichment in 1,813 ms after the test received its reply. No events
were lost in that short run. Firewall commands and invalid address requests were
rejected, and no new owned trace remained after disconnect. This verifies IPv4
on this PC; IPv6 framing/AAAA is covered by deterministic fixtures, not a live
IPv6 network test. [Integration report](benchmarks/2026-10-13-passive-dns.json).

The cause of the first failed integration test was capture-layer coverage:
`MultiLayer=false` saw the test question but missed its reply, while `true`
passed the same test. The exact driver/offload responsible was not established.

October 12, 2026, Release build, Windows 11 build 28120, 28 logical processors.
The core benchmark ran alongside a release build, so these are local samples
under load rather than hardware-independent latency guarantees.

| Operation | Measured cost |
| --- | --- |
| 10,000 cached name matches | 0.291 ms median; 0.403 ms p95 |
| 10,000 refresh requests before the next scan is due | 0.738 ms median; 0.902 ms p95 |
| Provider scan for 50 live remote IPs, five scans | 281 ms mean; 304 ms maximum |
| Read-only helper request including IPC | 373 ms, one sample |

The provider's hundreds of milliseconds run in the background once per minute,
or every two minutes when hidden. They are elapsed times, not CPU utilization
measurements. Cached display lookups are a separate, much cheaper operation.
No traffic is routed through this feature; these tests do not claim zero whole-app
overhead or measure Internet throughput with every optional feature enabled.

[Lookup samples](benchmarks/2026-10-12-domain-lookups.json) and
[provider/helper result](benchmarks/2026-10-12-domain-provider.json) contain no
domain names or browsing IP addresses. The final helper test returned 13 matching
hints, rejected firewall commands and invalid addresses, and enforced its rate
limit. Five direct scans returned 51 hints in total as the live cache changed.

Run `make check` for deterministic scheduling, single-flight, bounded-memory,
TTL, normalization, failure, disposal, identity, search, JSON and provider-decoding
checks. Run `make benchmark` for the core measurements. After `make build`, run
`make test-domains` for the read-only UAC/provider diagnostic, or pass an extracted
payload directory to `tools/test-domain-hints.ps1`. This diagnostic uses existing
records and makes no DNS requests or settings changes. An empty live cache cannot
demonstrate a positive match; the report includes match counts for that reason.

`tools/test-passive-dns.ps1` is the separate elevated integration diagnostic. It
sends one unique A question directly to Cloudflare's `1.1.1.1` resolver, verifies
the reply and passive observation, checks connection enrichment and request
validation, and checks that the owned trace disappears after disconnect. It uses
`sslip.io` for a known answer and does not change DNS settings or firewall rules.
The deterministic suite also covers malformed packets/compression, CNAME scope,
IPv4/IPv6 framing, TTL/capacity, provenance, display identity and JSON omission.

## Interpretation

Windows-cache hints are address-record owner names; capture hints retain the
question name through the reply's CNAME chain. Neither proves which name a
particular application used when several names share an IP. Private browser
DoH caches may not be visible to Windows, and encrypted traffic cannot supply
plaintext DNS replies. Full HTTPS paths and query strings remain encrypted.
New names take a capture flush plus monitor refresh to appear. Reverse DNS is
separately opt-in and sends lookups to the configured resolver.
