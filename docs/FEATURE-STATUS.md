# ZikWall implementation status

## Delivered

- Native Windows 11 WinUI 3 UI, light/dark/system themes and ZikWall branding.
- Live IPv4/IPv6 TCP local/remote addresses and ports, app icons, process/PID/path, incoming/outgoing direction from Windows SYN options, country flags and reported state.
- Offline bundled country lookup, Internet/local and direction filters, in-app recent-connection alerts and opt-in Windows notifications. See [monitor behavior and provenance](MONITORING.md).
- UDP local endpoints and ownership, with remote destinations explicitly unavailable.
- Search, pause/resume, selectable connection details and CSV export.
- Application/remote-IP grouping, 1-hour/24-hour/7-day history filters and peak-preserving chart aggregation over retained samples.
- Offline Windows signature trust inspection and SHA-256 hashing of the selected executable. These describe the inspected file; rules are still path-based.
- Right-click apps, subprocesses and connections to review and apply app/endpoint blocks, with optional expiry; inspect executables, open file locations, and copy addresses/PIDs/paths. Path rules affect every matching process. See [scope and validation](MONITOR-AND-USAGE.md#right-click-actions).
- Read-only saved-policy diagnostics for redundant/overridden scopes, temporary coverage, inactive rules and unavailable executables. Checks are bounded and partial results labeled; no automatic deletion or claim of observed connection decisions.
- Optional reverse DNS lookup, labeled as a hint rather than an app-requested domain.
- Selected network adapter download/upload rates and a rolling native chart. These are adapter totals, not per-process traffic accounting.
- Connection observation/state/disappearance history. Optional local JSON persistence, atomic replacement, seven-day retention and bounded counts (10,000 events / 15,000 traffic samples). Saving defaults off each launch; Clear removes the saved file.
- Sample rule editor and read-only simplewall XML profile import.
- HTTPS blocklist subscriptions with optional SHA-256 pins, bounded parsing, opt-in refresh and domain exceptions. IP entries can be copied into a policy for explicit application.
- Loopback UDP/TCP DNS-over-HTTPS proxy with resolver-level domain blocking. Adapter DNS changes are manual; DoT/DoQ and per-app DNS attribution are not implemented.
- Offline geographic view with a public-domain world outline and imported IPv4/IPv6 geolocation CSV. Bundled country data supports monitor flags; map coordinates still require an import. No external IP lookup service.

Connections is the startup page. The monitor defaults to five-second samples, with two-/ten-second choices and 15-second sampling when its page is hidden or the window minimized. It cannot guarantee capture of short-lived connections. It does not identify blocked traffic or infer allow/block decisions. Windows sometimes returns an unknown listener state on this host; ZikWall preserves it rather than inventing a state. Monitoring continues until paused or the app closes. See [resource measurements and benchmark commands](PERFORMANCE.md).

## Firewall implementation and runtime evidence

- Separate Windows SDK WFP engine and elevated broker, with validated bounded IPC, dynamic session cleanup and transactional rule replacement.
- Saved profiles, ordered app/IP/CIDR/port/direction allow/block rules and broker-managed timed expiry.
- Opt-in Windows network GUID associations and automatic switching; ambiguous networks/pending edits pause switching.

On 12 October 2026, user-approved UAC tests verified actual outbound IPv4 TCP blocking scoped to the test executable and one loopback port, removal and dynamic-session cleanup. A separate diagnostic passed 15 checks using the real unelevated ZikWall client and elevated broker: identity-checked pipe handshake, enforcement, invalid replacement preservation, empty-policy restoration, timed expiry without UI requests and cleanup on disconnect. See [WFP broker design and remaining release gate](WFP-BROKER.md). Broader rule types and full UI/profile lifecycle testing remain outstanding.

## Remaining

- Scoped privileged validation of IPv6/inbound/UDP and multi-rule precedence; connection prompts and silent-mode review.
- Per-process traffic accounting, requested-domain grouping and history beyond the bounded seven-day store.
- Reliable requested-domain attribution and domain-based policy.
- End-to-end validation of automatic profile switching with active filtering.
- Bundled coordinate-level geolocation data, subscription-to-policy synchronization, verified app identity rules, DoT/DoQ and reliable requested-domain attribution.

The legacy simplewall/routine compatibility problem remains. These features must not be represented as working merely because a UI control exists.

## Validation

The local suite passed 19 profile/model checks, 32 monitor/history checks plus 39 direction/country/filter/notification checks, 22 policy/IPC/network-identity checks, 19 rule-analysis checks, 13 DNS/blocklist/geolocation checks and one executable-trust check (145 total). The WinUI Release build succeeded without warnings; startup on live Connections and normal close also passed (exit code 0). The monitor also passed native icon/flag decoding, notification registration/submission and XAML rendering; compositor/chrome and actual toast display/activation were not independently verified. A native row-churn regression covers the icon-reuse crash found during benchmarking. An additional explicit smoke test resolved example.com through the local proxy to Cloudflare over validated HTTPS. Static supply-chain checks passed. The two user-approved privileged tests temporarily installed only executable-and-loopback-port-scoped filters and verified cleanup. No adapter DNS settings changed, and no runtime activity data is committed to Git.
