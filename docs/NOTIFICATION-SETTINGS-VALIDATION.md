# Notification settings and policy layout — 13 October 2026

The Firewall policy profile controls now share a control baseline. The profile
selector, profile actions, network toggle and filtering button stack into rows
below 900 DIP. The original native diagnostic failed at 1800 DIP because the
profile actions ended roughly 13 DIP above the selector. The fixed controls
passed baseline/bounds checks at 1800/1000/720/480 DIP, including the filtering
button and toggle at wide sizes. The cause was centered actions inside a
horizontal panel whose height was set by the labeled toggle.

Connections → gear → Notifications offers all new Internet connections, new apps
only, outgoing only and incoming only. The choice affects both notification
channels and saves immediately. New-app identity is session-based; see
[notification semantics](MONITORING.md#recent-connection-notifications).

Validation completed:

- Release build: zero warnings/errors; native engine and broker built.
- 337 model/service checks, including new-app grouping, startup/muting/resume,
  direction filtering, queued-batch replacement and preference migration.
- Native preference recreation, invalid DNS-exception error handling, 500 row
  churn iterations, context menus, usage and system-app visibility checks.
- Native options controls fit 600/440/360 DIP presenters with no horizontal
  overflow and a reachable bottom. The previous diagnostic intermittently lost
  its popup when replacing its presenter style between opens. The diagnostic now
  resizes one open presenter and waits for layout; production popup behavior is
  unchanged.
- Live HTTPS DNS forwarding of `example.com` and a real HTTPS list download
  containing 2,852 domains. No adapter settings or subscriptions were changed.
- Deterministic subscription refresh, hash-pin, error retention and persistence
  checks. Invalid subscription content and domain exceptions now show an error
  rather than escaping the UI event handler.

The native render checks measure XAML geometry; they do not validate compositor
Mica or Windows toast appearance. DNS use still requires the adapter to point at
127.0.0.1. Automatic refresh is opt-in, and this validation does not wait 24 hours
to exercise the timer interval. See [DNS details](DNS-SUBSCRIPTIONS.md).

The core benchmark's new-app filter processed 10,000 already-observed connection
events in **0.646 ms median / 1.032 ms p95**. Local DNS blocking completed 1,000
UDP round trips at **0.0684 ms median / 0.1609 ms p95**, about **11,827 queries/s**.
Parsing 100,000 blocklist domains took **172.89 ms median**. These are warmed
microbenchmarks, not Internet DNS latency or sustained whole-app throughput.
The baseline and updated core runs used changing host traffic; other validation
work overlapped parts of these runs, so they are not evidence of a speedup.

Raw measurements:

- [Core baseline](benchmarks/2026-10-13-notification-types-before.json)
- [Updated core and notification filters](benchmarks/2026-10-13-notification-types-after.json)
- [App resource baseline](benchmarks/2026-10-13-notification-types-app-before.json)
- [Final installer resource measurement](benchmarks/2026-10-13-notification-types-app-after.json)

The final installer payload ran for **60.66 seconds** after a 10-second warmup:
**0.824% of one logical CPU core**, **139.41 MiB peak private memory**,
**198.82 MiB peak working set**, and **4.38 MiB private-memory growth**. It passed
the enforced 2%-of-one-core / 200 MiB private-memory budgets. No build or other
diagnostic ran alongside this final measurement. This was a hidden window with
15-second monitoring, filtering/DNS/usage collection and Windows notifications
off; it does not measure combined feature load or long-term memory stability.
The earlier resource baseline (1.055% CPU / 137.24 MiB private memory) overlapped
other validation work and is not a controlled comparison.

Installer `artifacts/installer/setup.msi`, version **0.10.17978**, passed extraction
and verification of 801 payload file hashes. The benchmark assembly SHA-256 is
`B0263A148F3C20CECEFAC4AA14A0D07656A0386D6D4EDC5845E953D82A705107`, matching
the installer manifest. The installer was built, not installed on this machine.

Reproduce model/network checks with
`dotnet run --project tests/WinUI.Models.Tests -c Release -- bin/profile_internal.xml --doh --subscriptions`.
Run `tools/test-monitor-ui.ps1` for native diagnostics and
`dotnet run --project tests/ZikWall.Benchmarks -c Release -- artifacts/benchmarks/core.json`
for the core benchmark. Use `tools/benchmark-app.ps1 -Background -EnforceBudgets`
for a 60-second resource measurement.
