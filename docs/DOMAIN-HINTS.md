# Passive domain hints

ZikWall reads existing Windows DNS cache answers and matches their IPs to live
connections. Names appear in collapsed app/process summaries, expanded connection rows, live activity history, tooltips, Details, search
and explicit CSV exports. IP addresses stay visible and remain the identity used
for connection tracking and firewall targets. Saved activity JSON excludes names.

Enable **Monitor options → Website names → Enable domain hints** if Windows
requires elevation to read its cache. The UAC helper authenticates the parent
process and pipe peer, accepts only status and validated IP-cache requests, and
never instantiates the firewall engine or starts ETW. It exits on disconnect,
parent exit or 150 seconds without a request. Startup remains unelevated.

Website names are at the top of Monitor options. The status distinguishes a
successful lookup with no matching names from denied access or a provider failure.
Activity rows refresh names from the current cache without saving them in history.
The encrypted DNS proxy is separate: starting it does not enable elevated cache
access or reveal full HTTPS URLs. Browser-private DNS may leave no Windows cache
entry to display.

## Cost controls

- One background worker; the connection capture/UI path never waits for it.
- First scan immediately, then once per 60 seconds foreground or 120 seconds
  background. No standalone polling timer. The helper also enforces throttling.
- A fixed, read-only query of Microsoft's documented
  [MSFT_DNSClientCache](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/dnsclientcimprov/msft-dnsclientcache)
  provider, using pinned `System.Management` 8.0.0. No PowerShell subprocess,
  network DNS lookup or packet capture in the feature.
- Forward-only batches of 64; at most 16,384 A/AAAA answer records inspected,
  2,048 observed remote IPs retained, three names per IP. Unspecified addresses,
  failed answers, authority/glue records and invalid names are excluded.
- TTL-based expiry capped at two minutes, observed on the next monitor refresh.
  Allocation-free canonical-IP dictionary lookup and throttled refresh checks.
- Missing provider/access failures leave IP monitoring usable. Repeated
  access-denied scans are suppressed until a new reader session.

Windows owns the provider's enumeration and memory use. Its three-second
enumeration timeout is not a hard bound on every Windows operation; slow calls
remain off the UI thread. The limits above bound ZikWall's retained data and
client-side processing. Very large caches can exceed the inspection limit and
leave some names unknown.

## Measurements on this PC

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

## Interpretation

These are cached address-record owner names, not proof of the hostname requested
by an application. Multiple sites share CDN IPs, and private browser DNS/DoH
caches may not be visible to Windows. CNAME aliases are not inferred. Full HTTPS
paths and query strings remain encrypted. New names can take up to a scan interval
plus a connection refresh to appear. The separate manual reverse-DNS action can
still send a query when explicitly clicked.
