# ZikWall performance and resource budgets

## Separate tool windows, 14 October 2026

Monitor options, hosts, usage history, connection/executable details, log details,
rule details, the rule editor and rule-check results now use resizable windows in
the existing process. Each tool is created on demand, repeated clicks reuse its
window, theme subscriptions are removed on close, and owner exit closes all
children. List-based tools retain virtualization. Opening a tool does not start
another connection monitor or collector.

An unchanged history refresh previously replaced its native list source. The
native regression reproduced that replacement before the fix. It now retains
rows, scroll position and selection until the displayed events or hostname hints
change; the test also verifies that changed history still refreshes.

The live grouping path also allocated formatted socket keys even when its new-
connection set was empty, and formatted more keys just to count distinct sockets
per host. Empty-set checks now short-circuit, and host counting uses the existing
value-based transport identity. A fixed 10,000-connection / 100-app workload
reduced allocation from **9,542,107 to 7,778,605 bytes per refresh (18.5%)**. The
median over 100 warmed Release iterations changed from **11.0238 to 8.3848 ms**;
these are microbenchmark results, not a whole-app speedup claim. Socket counts,
new markers, clearing markers and row identity have regression coverage.

Reproduce with `dotnet run --project tests/ZikWall.Benchmarks -c Release -- artifacts/benchmarks/activity.json --activity-only`.
The command fails above its 8 MB allocation budget or if the fixture result
changes. [Before](benchmarks/2026-10-14-activity-allocations-before.json) ·
[After](benchmarks/2026-10-14-activity-allocations-after.json).

The native suite passed 500 live-list churn iterations, options layout at
600/440/360 DIP, window reuse and close/reopen, theme propagation, child cleanup,
host snapshots, rule editor save/cancel, usage sorting, preferences, context
menus, responsive policy controls, Activity and Log Analysis. The model/service
suite and dependency/asset integrity checks passed. An interactive desktop
inspection verified the actual options window; the XAML bitmap export cannot
reliably reproduce its compositor colors.

The benchmark now discovers and verifies its owned native window, checks that
foreground runs remain visible and unminimized, and rejects sampling gaps over
10 seconds. An early sandboxed run had no visible window and is invalid. A
later repeat crossed system suspension (44,400 seconds reported elapsed) and is
also invalid; its tiny CPU average must not be interpreted as an improvement.

The earlier 181.69-second run failed at 5.848% of one core and 203.05 MiB private
memory. Its first 16 seconds included a large CPU burst. Short list/chart
suppression experiments did not establish a consistent culprit. The managed
trace is dominated by native WinUI and waiting stacks, so it does not establish
CPU attribution. These observations remain evidence, not a claimed causal
speedup. All temporary suppression switches were removed before the final build.

The 181.76-second optimized-candidate run measured **2.304% of one core** and
**194.24 MiB peak private memory**. Memory passed the 200 MiB budget; CPU still
failed the 2% budget. The preceding clean run measured 2.029% / 201.2 MiB.
Changing live host traffic prevents a controlled whole-app CPU comparison; the
allocation improvement above is deterministic, while overall CPU compliance
remains unresolved. The benchmark's background warmup now accounts for WinUI
activating after its HWND is created; samples still reject a window becoming
visible after warmup.

[Optimized candidate](benchmarks/2026-10-14-windows-optimized-foreground.json) ·
[Before allocation changes](benchmarks/2026-10-14-windows-before-allocations.json)

The exact final assembly passed separate 60.54-second visible and hidden runs,
after 15-second warmups:

| Release scenario | CPU, one core | Peak private memory | Peak working set |
| --- | ---: | ---: | ---: |
| Foreground | 1.445% | 183.30 MiB | 254.56 MiB |
| Hidden | 0.826% | 162.15 MiB | 230.84 MiB |

Assembly SHA-256: `A7EF12F5DFCC6E6A5B11D5EBD650E97CBB683DFE66D1D6A3D1D651FC23BFA205`.
These short passes do not erase the longer candidate's CPU failure or establish
multi-hour stability. Filtering, the DNS proxy and the ETW collector were off;
normal live host traffic and retained local history were present. No build,
UI inspection or other diagnostic touched the app during these samples.

[Release foreground](benchmarks/2026-10-14-windows-release-foreground.json) ·
[Release hidden](benchmarks/2026-10-14-windows-release-background.json)

Installer **0.10.19294** contains the exact measured assembly. All 803 extracted
MSI file hashes and 27 standalone installer diagnostics passed; setup extraction,
native screens and embedded MSI integrity were checked without installing it.
[Release integrity record](benchmarks/2026-10-14-tool-windows-release.json).

[Native validation](benchmarks/2026-10-14-tool-windows-ui.txt) ·
[Earlier failing run](benchmarks/2026-10-13-windows-final-foreground.json) ·
[Managed trace summary](benchmarks/2026-10-13-tool-windows-managed-trace.txt)


## Current-source MSI retest, 13 October 2026

MSI **0.10.18411** builds successfully, but its foreground resource measurement
**fails** the existing 2%-of-one-core / 200 MiB private-memory budgets. Over
181.86 seconds after 15 seconds of warmup it averaged **3.188% of one core**
(0.114% of the 28-thread machine), with **249.18 MiB peak private memory**,
316.55 MiB peak working set and 80.67 MiB private growth. This run does not
establish a memory leak or isolate the remaining foreground cost.

The separate hidden-window run **passed** both budgets over 60.57 seconds
after a 15-second warmup: **0.671% of one core**, **138.11 MiB peak private
memory**, 198.43 MiB peak working set and -0.91 MiB private growth. The harness
verified the window stayed hidden. Both resource reports identify the same
assembly; this short run does not establish multi-hour stability.

The earlier interrupted chat's benchmark also finished and failed: 5.823% of
one core and 248.26 MiB peak private memory over 60.65 seconds. Its assembly was
different. Both reports are retained; their changing live traffic and different
durations do not establish a controlled speedup between versions.

The fresh installer includes the current UI and readable-app-name edits and the
Log Analysis optimization below. Building exposed a missing `KnownApps.cs`
link in the broker and engine-test projects; both now include it and compile
without warnings or errors. The model/service suite, native UI diagnostics
(including 500 list updates, Activity and Log Analysis), dependency integrity
checks, and MSI validation of all 803 extracted file hashes passed.

General core benchmarks completed, including live Windows endpoint capture
(19.7301 ms median), 10,000 connections grouped into 100 apps (7.8323 ms), and
1,000 local blocked UDP DNS requests without errors. The privileged Windows DNS
cache-provider measurement was skipped. All three deterministic Log Analysis
enrichment-call budgets passed. Microbenchmark success does not override the
failed whole-app foreground budgets.

App resource measurements ran sequentially after packaging, core benchmarks
and UI diagnostics completed. Optional collector, filtering and DNS proxy were off during the
app resource run. Its assembly hash matches the installer manifest:
`58092F1D388E29615C0FB4EDD94F9364E0D28C5A51C3E1D046A620E1D2AF217B`.
MSI SHA-256:
`A9A618F14AC9A3297C9E749BF42E5C78B01EF547EE35A8D68DB355702A40436C`.

[Foreground](benchmarks/2026-10-13-retest-foreground.json) ·
[Background](benchmarks/2026-10-13-retest-background.json) ·
[Core](benchmarks/2026-10-13-retest-core.json) ·
[Log Analysis](benchmarks/2026-10-13-retest-log-analysis.json) ·
[Earlier foreground](benchmarks/2026-10-13-performance-review-foreground.json)

## Log Analysis filtering and display cap, 13 October 2026

Log Analysis used to resolve current hostname hints for every retained event
before applying category/date filters or its 1,000-row display limit. It now
filters first. With an empty search it enriches only the displayed rows; a
nonempty search still resolves current hints for all filter candidates so
hostname matches remain complete. First-app classification, unread counts,
matching totals and stable newest-first ordering retain their existing semantics.

A fixed 10,000-event fixture (100 apps, older history) reproduced the excessive
work before the fix and passes the exact enrichment-call budgets afterward:

| View | Hint lookups before / after | Allocated bytes before / after | Median ms before / after |
| --- | ---: | ---: | ---: |
| Important | 10,000 / 100 | 4,128,919 / 1,677,074 | 5.5475 / 3.4776 |
| Today, all history outside range | 10,000 / 0 | 4,120,448 / 1,640,584 | 2.4801 / 1.5487 |
| All, capped display | 10,000 / 1,000 | 4,688,147 / 2,776,064 | 3.3529 / 1.8365 |

Each case uses five warmups and 100 Release iterations, the production
`LogAnalysis.Build` and `MonitorEnrichment.WithDomainHint`, and empty local hint
caches. Allocations fell 41–60%. Timings include host/JIT variability and are
microbenchmarks, not whole-app speedup claims. Sorting remains necessary for
arbitrarily ordered input; these measurements do not justify changing it.
Four additional regression checks cover filtered/capped enrichment, current
hostname search, displayed hints and unread totals. All 21 log checks pass.

Reproduce (throws if unnecessary enrichment returns):

```powershell
dotnet run --project tests/ZikWall.Benchmarks -c Release -- artifacts/benchmarks/log-analysis.json --log-analysis-only
dotnet run --project tests/WinUI.Models.Tests -c Release -- --log-analysis
```

[Before](benchmarks/2026-10-13-log-analysis-before.json) ·
[After](benchmarks/2026-10-13-log-analysis-after.json)

## Foreground refresh and hostname-cache fixes, 13 October 2026

The fixed foreground build passed the existing **2% of one core / 200 MiB private
memory** budgets: **1.205% CPU and 175.58 MiB peak private memory** over 181.56
seconds after a 15-second warmup. CPU is normalized to one logical core, not the
entire 28-thread machine. The measured assembly SHA-256 is
`7297E21CBB6DBEA50E78D51A393564EF4AD620F5735DF792EF56825382932D54`.

The same build passed the 60.56-second hidden-window confirmation after a
10-second warmup: **0.851% of one core, 138.45 MiB peak private memory**, and
3.62 MiB private growth. Window invisibility was checked at every sample.
[Final background report](benchmarks/2026-10-13-hostnames-background-final.json)
contains the matching assembly hash and raw samples.

App and process rows now retain their identity across changes to host names,
socket counts, flags and status. Property notifications update the displayed
cells, and sorting/insertion moves surviving rows instead of rebuilding their
native controls. Host details use one popup created on first use and cleared on
close. The Live indicator stays steady; the former 600 ms opacity animation
ran after every refresh. Sampling pointed to native UI/WinRT object work rather
than a large managed hostname-processing hot spot; managed sampling alone does
not identify native CPU stacks.

Unopened Activity and Log Analysis views are created on first navigation, and
host cells notify their individual displayed properties so socket-count changes
do not replace an unchanged flag source. The final memory fix gives the small
monitor flags explicit **64 by 48 pixel SVG rasters**, preserving their displayed
20 by 15 DIP size and country selection while bounding native image surfaces.

| Sequential foreground experiment | CPU, one core | Peak private | Result |
| --- | ---: | ---: | --- |
| Shared lazy popup only | 26.066% | 376.74 MiB | Did not solve the failure |
| Preserve rows and notify cells | 5.414% | 191.32 MiB | Memory passed; CPU exceeded |
| Also remove periodic Live animation | 1.444% | 188.40 MiB | Both passed |
| Final, including bounded flag rasters (181.56 s) | 1.205% | 175.58 MiB | Both passed |

The first three used a 10-second warmup and 60-second measurement with normal host
traffic and capture off. These are changing live workloads, not identical network
replays; the table establishes the observed budget results, not an exact causal
speedup for each individual change. There was no concurrent build, benchmark,
forced collection or working-set trimming. The final three-minute run grew
14.02 MiB during measurement, so it is not evidence of a permanent memory plateau.

Regression checks cover retained row identity, updated host/flag properties and
surviving rows after removal. The native check verifies an unchanged container
while a host label changes, correct popup contents on reuse and released contents
after closing. Native churn also passed 500 updates.

[Popup-only experiment](benchmarks/2026-10-13-hostnames-foreground-lazy-flyout.json) ·
[Incremental rows](benchmarks/2026-10-13-hostnames-foreground-incremental.json) ·
[First passing short run](benchmarks/2026-10-13-hostnames-foreground-fixed.json) ·
[Final three-minute foreground](benchmarks/2026-10-13-hostnames-foreground-bounded-flags.json)

The first longer run held CPU at 1.418% but exceeded memory at 207.43 MiB.
Deferring the unopened Activity and Log Analysis views did not by itself resolve
the limit: that subsequent live workload reached 259.36 MiB. Precise host-field
notifications then held CPU to 1.204% over 181.62 seconds, but peaked at
239.30 MiB private memory. These failed longer runs are preserved rather than
discarded. The final native-image check also asserts that a socket-count change
does not replace the existing flag source.

[First longer run](benchmarks/2026-10-13-hostnames-foreground-fixed-long.json) ·
[Deferred pages](benchmarks/2026-10-13-hostnames-foreground-lazy-pages.json) ·
[Precise host-field bindings](benchmarks/2026-10-13-hostnames-foreground-stable-images.json)

The DNS cache now maintains one expiration index entry and one recency node per
live address/name pair. Renewals replace their existing deadline; expiration and
TTL-zero deletion remove both indexes. Capacity eviction no longer scans all
8,192 entries for every new name. The fixed deterministic suite passes 269
passive-DNS checks, including expiration before valid-entry eviction, renewed
names and tied timestamps.

| Cache workload | Original median / p95 | Fixed median / p95 |
| --- | ---: | ---: |
| Insert 256 new names at full capacity | 54.7721 / 57.0830 ms | 0.2553 / 0.2927 ms |
| Read 2,048 IPs from full cache | 0.4071 / 0.4714 ms | 0.3439 / 0.4286 ms |
| Parse/cache 10,000 repeated replies | 6.3293 / 6.6790 ms | 9.5818 / 12.7743 ms |

Full-cache insertion is approximately 215 times faster in these synthetic runs,
allocating 632 bytes/name instead of 888. Retained managed memory increases from
1.41 to 2.16 MiB for the two indexes. Repeated-response parsing/cache updates were
slower in the later run, as was unchanged parser-only code (4.0536 to 5.5272 ms
per 10,000 replies); the table does not claim every operation improved. Warmup,
fixture sizes and exclusions match the original passive-DNS benchmark below.

[Fixed cache measurements](benchmarks/2026-10-13-passive-dns-core-fixed.json)

The final general suite measured compact notification formatting at **0.0074 ms
median / 0.0084 ms p95** (2,241 allocated bytes), 10,000 cached hostname reads at
0.2873 ms median, and grouping 10,000 connections into 100 apps at 8.6245 ms
median / 13.3040 ms p95. [Final core results](benchmarks/2026-10-13-hostnames-core-final.json)
include the remaining workloads. An initial sandboxed attempt could not read
Windows endpoint tables; the completed run used normal host access.

### Completed production capture benchmark

The corrected diagnostic completed all phases with **20 valid replies from 20
queries, zero timeouts, 20 polls matching the observed test hostname, and verified
trace/helper cleanup**. Final capture status reported 153 DNS reply observations,
zero lost events and zero unsupported packets. Startup took 1,835.4 ms; hint IPC
was 0.7437 ms median / 10.9784 ms p95 across 41 reads. Public DNS round trips were
191.4 ms median / 619.8 ms p95; those are network timings, not parser latency.

| Capture phase | Duration | Helpers' CPU, one core | Helpers' sum of private peaks | All tracked processes' CPU / sum of private peaks |
| --- | ---: | ---: | ---: | ---: |
| Off, diagnostic host only | 30.57 s | Not running | Not running | 1.1244% / 68.48 MiB |
| On, ambient traffic | 41.68 s | 0.4124% | 115.48 MiB | 3.8614% / 187.26 MiB |
| On, 20 paced DNS questions | 47.49 s | 0.3619% | 115.49 MiB | 3.4217% / 190.93 MiB |

The PowerShell capture controller accounts for most helper memory: 96.83 MiB
peak in the ambient phase versus the broker's 18.64 MiB. Its measured steady CPU
was below process-timer resolution. The diagnostic host creates no window and
runs no ordinary monitor; it polls the real authenticated broker. Its CPU and
memory are included in the rightmost column, so helper-only figures must not be
presented as total capture overhead. These process measurements exclude
kernel/system-wide ETW work in other processes. The enabled helpers are additional
to the normal UI footprint; the 200 MiB monitor budget above applies with optional
capture off. Sums of per-process memory peaks need not be simultaneous peaks.

Capture phases ran sequentially after the foreground benchmark, with no other
benchmark or build in progress. Queries used a unique test name under sslip.io
through 1.1.1.1, paced by two one-second delays plus reply/IPC time; no catch-up
bursts or retries. This is a low-rate integration/resource test, not a maximum
throughput, encrypted-DNS, VPN or multi-hour leak test. The UI hash matches the
final foreground run; broker and engine hashes are also recorded.

[Completed capture report and raw process samples](benchmarks/2026-10-13-domain-capture-resources.json)

## Initial passive-hostname and compact-notification measurements, 13 October 2026

**Background monitoring passed; foreground monitoring exceeded the CPU budget.**
These Release x64 measurements ran sequentially on Windows 11 build 28120,
Intel Core i7-14700HX (28 logical processors), 47.8 GiB RAM. CPU below uses
**one logical core = 100%**. The existing monitor budgets are 2% CPU and
200 MiB peak private memory. No build or other benchmark ran concurrently.

| Normal Connections page | Warmup / measured | Average CPU | Peak private | Budget result |
| --- | ---: | ---: | ---: | --- |
| Foreground | 10 s / 60.53 s | 14.300% | 300.94 MiB | CPU and memory exceeded |
| Foreground, longer warmup | 60 s / 60.51 s | 4.467% | 199.79 MiB | CPU exceeded |
| Hidden, background monitoring | 10 s / 60.51 s | 0.620% | 137.70 MiB | Passed |

Capture, usage collection, filtering, DNS proxy and desktop alerts were off in
these whole-app runs. Foreground CPU remained above target after the longer
warmup; the cause has not been isolated. The longer-warmup run had -0.60 MiB
private-memory growth, versus +116.75 MiB in the first run and +2.62 MiB in the
background run. These short, changing live workloads do not prove leak freedom
or establish a controlled before/after regression. The background window was
verified hidden during every sample.

[Foreground](benchmarks/2026-10-13-hostnames-foreground.json) ·
[Foreground, longer warmup](benchmarks/2026-10-13-hostnames-foreground-warm.json) ·
[Background](benchmarks/2026-10-13-hostnames-background.json)

### Initial capture resources: partial result

The first live run completed its 30-second capture-off and 40-second ambient
capture phases. The following figures are from the completed ambient phase,
with hostname polling every two seconds:

| Process | CPU, one core | Peak private memory |
| --- | ---: | ---: |
| DNS broker | 0.3513% | 17.68 MiB |
| PowerShell capture controller | 0.0390% | 84.74 MiB |
| Helpers combined | 0.3903% | 102.42 MiB (sum of peaks) |
| App and helpers combined | 5.4250% | 295.27 MiB (sum of peaks) |

The controller accounts for most helper memory. These are process counters;
they exclude ETW kernel/system-wide costs in other processes. Summed individual
memory peaks need not occur simultaneously. The app's ordinary monitoring was
still running in this attempt, despite its original "inactive UI shell" label;
its CPU was 4.6824% with capture off and 5.0347% with capture on. This is not a
controlled measurement of total capture overhead.

Startup took 1,278.5 ms. Hint IPC measured 0.6736 ms median / 2.7315 ms p95 across
24 reads. The subsequent paced-query phase aborted when the fifth public DNS
request exceeded its five-second receive timeout: four valid replies out of five
attempts. It therefore **does not supply a completed 20-query load result**.
At the final status read, capture reported 171 DNS reply observations and zero
lost events. The trace and helper cleanup checks passed for this attempt.

The harness was revised to record query timeouts and continue all 20 attempts.
A second attempt then exceeded its 300-second deadline without writing samples
or starting a capture broker. It awaited monitor pause before window activation;
the exact blocking cause was not traced. The diagnostic was moved ahead of
window creation and rebuilt successfully (zero warnings/errors). The initial
cleanup elevation was canceled; the task-owned process was subsequently stopped
and its exit verified before the performance-fix measurements above. No capture
broker was present when the stalled process was inspected.

[Preserved partial run](benchmarks/2026-10-13-domain-capture-resources-interrupted.json) ·
[Timed-out attempt](benchmarks/2026-10-13-domain-capture-resources-timeout.json)

Whole-app resource reports use assembly hash `EEE62B3A...039B9`; the partial
capture report uses `568369F0...57E57`. The diagnostic-only correction compiled
to `artifacts/DomainBenchmark` with hash `3AD657B9...BEDE4`; it has not been
measured. Full hashes are in the raw reports or can be computed from the build.

### Parser, cache and notification costs

| Synthetic workload | Median / p95 per measured batch | Allocation |
| --- | ---: | ---: |
| Reject 1,000,000 non-DNS UDP frames | 9.2792 / 10.0124 ms | Effectively zero per frame |
| Parse 10,000 Ethernet/IPv4 DNS replies | 4.0536 / 4.3456 ms | 1,632 bytes/reply |
| Parse and cache 10,000 repeated replies | 6.3293 / 6.6790 ms | 2,056 bytes/reply |
| Read 2,048 IPs from a full 8,192-entry cache | 0.4071 / 0.4714 ms | 517,974 bytes/read |
| Insert 256 new names into a full cache | 54.7721 / 57.0830 ms | 888 bytes/name |
| Format one compact notification with bundled flag | 0.0072 / 0.0086 ms | 2,240 bytes/notification |
| Find 10,000 cached hostname hints | 0.3750 / 0.5149 ms | 11 bytes/batch |
| Group 10,000 connections into 100 apps | 8.6675 / 11.7418 ms | 7,786,687 bytes/batch |

The full hostname cache retained approximately **1.41 MiB of managed memory**.
Full-cache insertion is the notable stress bottleneck: **0.214 ms per new name**,
because eviction scans the bounded dictionary for expired entries and the oldest
entry. Repeated-name parsing plus updates cost about **0.633 microseconds per
reply** in the small fixture. These are separate workloads, not a capacity claim.

The new passive-DNS suite warms each case for at least one second and collects
garbage before measurement; the existing general suite uses five warmup calls.
Parser fixtures send no traffic and exclude native capture callbacks, Windows
ETW, broker IPC and rendering. Allocation counts are per calling thread and
include small harness overhead. Notification timing measures formatting and
local flag selection, excluding Windows toast submission and display.

The general suite also measured live endpoint capture at 21.3315 ms median,
live enrichment at 0.7296 ms median, and 1,000 local blocked UDP DNS round trips
at 0.0426 ms median / 0.0733 ms p95. The Windows cache-provider scan was skipped
in that non-administrator run. Historical notification formatting was
0.0123 ms median / 2,601 bytes, while historical 10,000-hint lookup was
0.1858 ms median; neither timing difference is a controlled speedup/regression
measurement.

[Passive DNS microbenchmarks](benchmarks/2026-10-13-passive-dns-core.json) ·
[General core benchmarks](benchmarks/2026-10-13-hostnames-notifications-core.json)

### Reproduce these measurements

Build first with `tools/build-winui.ps1`, then run each command separately:

```powershell
dotnet run --project tests\ZikWall.Benchmarks -c Release -- artifacts\benchmarks\core.json
dotnet run --project tests\ZikWall.Benchmarks -c Release -- artifacts\benchmarks\passive-dns.json --passive-dns-only
.\tools\benchmark-app.ps1 -Label foreground -DurationSeconds 60 -OutputPath artifacts\benchmarks\foreground.json
.\tools\benchmark-app.ps1 -Label foreground-warm -WarmupSeconds 60 -DurationSeconds 60 -OutputPath artifacts\benchmarks\foreground-warm.json
.\tools\benchmark-app.ps1 -Label background -Background -DurationSeconds 60 -OutputPath artifacts\benchmarks\background.json
.\tools\benchmark-domain-capture.ps1 -OutputPath artifacts\benchmarks\capture.json
```

The capture command requests administrator elevation, sends 20 paced DNS
questions to 1.1.1.1 for a unique benchmark hostname under sslip.io, records
five-second receive timeouts without retries, and verifies helper/trace cleanup.
It uses the production capture path in a diagnostic process without a window
or ordinary monitoring. It does not alter rules or saved settings. Add `-EnforceBudgets` to
the app commands when a budget exceedance should fail the command.

## Notification type controls and policy alignment, 13 October 2026

The final installer for notification preferences and responsive policy controls
passed a 60.66-second hidden-window run at **0.824% of one core** and
**139.41 MiB peak private memory**, within the existing resource budgets. The
new-app filter processed 10,000 events in **0.646 ms median**; local blocked DNS
round trips measured **0.0684 ms median**. See the
[validation report](NOTIFICATION-SETTINGS-VALIDATION.md) for scenarios, limits,
before/after raw measurements and the verified installer assembly hash.

## Event notifications, 13 October 2026

Connection alerts now describe the observed event and include an offline country
flag, remote IP/port and an available cached hostname. Formatting runs only when
the existing bounded batch flushes (at most once per 30 seconds). It performs no
network lookup, flag download, runtime image conversion or per-endpoint UI work.

| Hidden-window monitoring | Before | Updated |
| --- | ---: | ---: |
| Measurement after 10-second warmup | 60.62 s | 60.57 s |
| Average CPU (one logical core = 100%) | 0.825% | 0.567% |
| Peak private memory | 138.11 MiB | 135.54 MiB |
| Peak working set | 195.55 MiB | 195.17 MiB |
| Private-memory growth during measurement | 4.09 MiB | 5.86 MiB |

Both passed the enforced 2%-of-one-core / 200 MiB private-memory budgets. No
performance fix was warranted by these measurements. These runs use changing
host traffic, so the difference is not evidence of a speedup. Monitoring samples
every 15 seconds while hidden; filtering, DNS, ETW collection and desktop alerts
were off. They do not establish enabled-collector overhead, toast compositor
cost or multi-hour memory stability. No build or other benchmark ran alongside
either resource measurement. Windows 11 build 28120, i7-14700HX, 28 logical
processors, 47.8 GiB RAM; assembly hashes are recorded in the reports.

The new production notification formatter (including local flag selection and
XML escaping) measured **0.0103 ms median / 0.0133 ms p95**, allocating **2,602
bytes per batch**, across 1,000 warmed iterations. This excludes Windows toast
submission. The core before/after runs also completed all existing workloads,
including live endpoint capture and 1,000 local blocked DNS round trips.

Validation: 312 model/service checks, 500 native list-churn updates, every flag
decoded, native toast submission and XAML rendering, and native context-menu,
usage, system-app and preference checks passed. The first Options-popup check
found a missing presenter; an isolated rerun passed all three widths. No app
change was made for that unreproduced diagnostic failure. Desktop toast
appearance was not independently captured.

[Background before](benchmarks/2026-10-13-notifications-before.json) ·
[Background after](benchmarks/2026-10-13-notifications-after.json) ·
[Core before](benchmarks/2026-10-13-notifications-core-before.json) ·
[Core after](benchmarks/2026-10-13-notifications-core-after.json)

## Sortable usage column, 13 October 2026

Connections now shows a sortable **Usage today** column while collection is
enabled. Totals are indexed by day and app. Usage-only refreshes reuse connection
groups and update observable cells without replacing native rows. Connection
regrouping reuses already-computed app/process keys for expansion bookkeeping.

The first native candidate replaced rows when numbers changed. The same native
fixture after in-place cell updates measured:

| Native fixture | Initial candidate | In-place cells |
| --- | ---: | ---: |
| Measurement after 10-second warmup | 61.55 s | 61.09 s |
| Average CPU (one logical core = 100%) | 11.17% | 2.20% |
| Peak private memory | 235.29 MiB | 186.57 MiB |
| Peak working set | 295.74 MiB | 243.82 MiB |

This is about 80% less CPU and 21% less peak private memory in this scenario.
The fixture has 1,000 connections, 100 apps, one expanded app, a descending usage
sort and synthetic usage samples every five seconds. It exercises the real WinUI
controls and bindings, but excludes ETW collection and live connection capture.
The 2.20% result exceeds the older 2% target for the different, default monitor
scenario; this is not a claim that every usage-enabled workload meets that target.
No builds or other benchmarks ran alongside either measurement. Short runs do
not establish long-term memory behavior or collector overhead.

| Core workload | Before median / p95 | Final median / p95 | Before / final allocated bytes |
| --- | ---: | ---: | ---: |
| 512 usage-label reads, 30 retained days | 2.402 / 2.706 ms | 0.254 / 0.265 ms | 159,684 / 106,416 |
| Rebuild 10,000 connections, 512 apps | 6.024 / 13.347 ms | 6.118 / 8.260 ms | 10,467,018 / 6,301,758 |
| Usage-only refresh of those 512 apps | Previously full rebuild above | 1.184 / 1.495 ms | Previously full rebuild / 330,680 |
| Observe 512 apps with 30 daily buckets each | 2.909 / 4.103 ms | 3.258 / 4.017 ms | 2,136,692 / 2,137,243 |

The final full rebuild includes the new combined-total display and numeric sort.
The new usage-only path reduces median refresh work by about 80% and allocations
by about 97% against the previous rebuild-on-every-usage-tick workflow. Full
regrouping and ledger observation are not claimed to be faster. The harness uses
100 measured iterations after ten warmups, with ordinary Release tiered JIT;
small timing changes and run-to-run variation should not be overinterpreted.

[Core before](benchmarks/2026-10-13-usage-core-before.json) ·
[Core after](benchmarks/2026-10-13-usage-core-after.json) ·
[Native initial candidate](benchmarks/2026-10-13-usage-ui-before.json) ·
[Native in-place cells](benchmarks/2026-10-13-usage-ui-after.json)

Reproduce core measurements with
`dotnet run --project tests/ZikWall.Benchmarks -c Release -- artifacts/benchmarks/usage.json --usage-only`.
After building, run `ZikWall.exe --test-usage-view` for native visibility, heading,
selection and live cell-binding checks, or `--test-usage-view-benchmark` for the
60-second fixture benchmark. Both use isolated in-memory usage and do not start
the collector or save usage. `tools/test-monitor-ui.ps1` includes the new checks.

Measured on 12 October 2026 using Release x64 builds on Windows 11 build 28120, Intel Core i7-14700HX (28 logical processors), with 47.8 GiB usable physical RAM. This is a local workstation benchmark, not a hardware-independent resource guarantee.

## Monitor with icons, direction, country flags and recent alerts

The new monitor passed the same **2% of one CPU core / 200 MiB private-memory** budgets after fixing a native icon/layout crash discovered during measurement. The fixed build's three-minute run measured **0.723% of one core** (0.026% of the 28-thread machine), **160.45 MiB peak private memory** and **217.17 MiB peak working set**, over 181.65 seconds after a 15-second warmup. Private memory grew 13.98 MiB during that interval; this is not a multi-hour leak test. Compared with the earlier monitor's 143.44 MiB private peak, the additional features use roughly 17 MiB more private memory in these different live workloads.

The final package confirmation also passed: **0.696% of one core**, **156.41 MiB peak private** and **212.23 MiB peak working set** over 60.61 seconds after warmup. The final native render/notification diagnostic and another 500-cycle churn run passed after the label correction.

[Monitor measurements and binary hashes](benchmarks/2026-10-12-monitor.json) include the long fixed-build run, the final-package confirmation and core results. The long run preceded only a final XAML text-encoding correction. No profiling, build or other test process ran alongside the measured ZikWall process. Routine editing/checks and normal host activity continued; workloads are not identical network replays.

Core results: the run started with 948 live endpoints. Capturing the Windows endpoint tables took **19.35 ms median**; adding direction and countries to the separately captured live fixture took **1.44 ms median / 1.68 ms p95**. A batch of **10,000 offline country lookups**, deliberately exceeding the cache, took **5.30 ms median / 9.24 ms p95**. Tracking 10,000 unchanged endpoints took **2.82 ms median**, allocating approximately 607.5 KB. These are warmed microbenchmarks; they do not establish maximum sustainable connection arrival rates.

Country tables are memory-mapped and searched without loading hundreds of thousands of managed objects. Icon caches hold at most 256 decoded 32×32 pixel buffers; each row owns its native bitmap, and visual changes are deferred outside native template layout. The chart/options panel defaults collapsed to prioritize the list, and a collapsed chart is not redrawn. Recent alerts are deduplicated and batched; Windows toasts stay opt-in and were off in the resource runs.

All **145 correctness checks** passed. Three successive native stress runs passed 500 list-update/alert-resize iterations each with the fix and full incremental rendering enabled. An earlier live run and deterministic native stress runs failed inside WinUI (`0xc000027b`); the crash was isolated to icon updates during template/layout work. Deferring those visual mutations through the dispatcher removed the reproduced failure. See [monitor validation details](MONITORING.md). These tests are additional evidence, not a guarantee of crash-free operation on all Windows/GPU combinations.

## Original optimization baseline

The final clean build passed the enforced resource budgets. [Raw measurements, samples and binary hashes](benchmarks/2026-10-12.json) are checked in so the summary can be verified.

| Default live-monitor process | Before | Final |
| --- | ---: | ---: |
| Measurement duration after warmup | 60.53 s | 181.64 s |
| Average CPU, one logical core = 100% | 32.088% | **1.067%** |
| Average CPU, full 28-thread machine = 100% | 1.146% | **0.038%** |
| Sampled peak private memory | 173.00 MiB | **143.44 MiB** |
| Sampled peak working set | 212.38 MiB | **195.02 MiB** |
| Peak handle count | 1,185 | 1,176 |

Final private memory grew 16.55 MiB between the first and last samples. That includes history and runtime/native caches; this short run does not establish a permanent plateau or prove leak freedom. There is no forced garbage collection or working-set trimming in the application to lower these measurements artificially.

| Core workload | Before | Final |
| --- | ---: | ---: |
| Track 10,000 unchanged endpoints: median | 6.25 ms | 3.04 ms |
| Same tracking pass: allocated bytes | 6,736,717 | 607,387 |
| Empty search across 10,000 endpoints: median | 2.09 ms | 0.087 ms |
| Same empty search: allocated bytes | 3,912,870 | 67 |
| Full history trim on monitor path: median | 1.08 ms | 0.103 ms |
| Same trim: allocated bytes | 1,050,366 | 171 |
| Live endpoint capture: median | 492.99 ms | 19.60 ms |

The tiny nonzero allocation totals include harness overhead. Live capture contained 903 endpoints in the baseline and 524 in the final core run; it is not an equal-sized workload comparison. Fixed synthetic fixtures are equal-sized. Core microbenchmarks use ordinary Release JIT execution rather than a statistical benchmarking framework; small timing differences should not be treated as significant.

Additional final workloads:

- 1,000 sequential locally blocked UDP DNS queries: **0.041 ms median / 0.076 ms p95**, with zero reported errors. The elapsed batch rate was approximately 21,071 queries/s; this is not a maximum sustainable throughput claim.
- Parse 100,000 blocklist domains: **109.6 ms median**, approximately 45.5 MB allocated per parse. This is an explicit import/refresh operation, not idle polling.
- Find 1,000 addresses in 50,000 geolocation networks: **0.36 ms median**.
- Bounded worst-case analysis of 1,500 rules: **18.4 ms median**, with the work-limit behavior preserved.

The Release build completed with zero warnings/errors and all **105 correctness checks** passed. The resource-budget failure was reproduced before the rendering fix. Two subsequent measurements with row transitions disabled passed the CPU target (1.597% over 30 seconds in a diagnostic build, then 1.067% over three minutes in the clean build).

## Reproduce

Build first, then run measurements sequentially with other build/test jobs stopped:

```powershell
.\tools\build-winui.ps1
.\tools\benchmark-app.ps1 -Label current -DurationSeconds 180 -WarmupSeconds 15 -EnforceBudgets
dotnet run --project tests\ZikWall.Benchmarks -c Release -- artifacts\benchmarks\core.json
```

The app benchmark starts a fresh normal ZikWall process on Connections, observes it, and closes only that process. Do not interact with that window during the measurement. It never enables filtering, DNS, activity persistence or subscriptions. Existing saved history, if present, can affect the starting footprint. Output contains aggregate resource counters and binary hashes, not connection addresses or executable paths.

The default regression budgets are **2% of one logical CPU** averaged over the steady-state window and **200 MiB peak private memory** in this scenario. `-EnforceBudgets` makes an exceedance fail after saving the result and closing the test app. The parameters `-CpuBudgetOneCorePercent` and `-PrivateMemoryBudgetMiB` can set stricter budgets for target hardware. These are targets for this measured monitor scenario, not promises about every optional feature or traffic volume.

CPU normalized to the whole machine is also recorded. On this 28-thread machine, 2% of one core is about 0.071% of total CPU capacity. A small Task Manager percentage alone can conceal a costly refresh loop. Private memory is committed private process memory; working set includes resident shared code/runtime pages and is also reported. Sampled peaks can miss short spikes.

## Optimizations

- Use limited-information executable-path queries and one fallback process-name snapshot per capture. Avoid repeatedly enumerating the machine's processes for protected PIDs. There is no stale PID-only identity cache.
- Stop asking Windows to sort endpoint tables that the app sorts again.
- Track connections using value keys instead of repeatedly formatting large identity strings.
- Empty searches allocate no per-row display strings; normal searches short-circuit against individual fields.
- Trim the live history buffers in place. Persistence still takes an independent snapshot.
- Update changed connection rows incrementally. Stable rows preserve their native list items; heavy churn uses a bounded fallback to one reset.
- Disable native row transition animations for the live and history lists. A short diagnostic comparison isolated rendering cost: capture without presentation averaged 0.826% of one core; presentation enabled averaged 2.735%; removing row transitions reduced that run to 1.597%. Live host traffic varied between runs. Temporary phase instrumentation was removed from the shipping source.
- Do not rebuild the hidden history list or redraw a hidden/minimized monitor. Rebind adapter choices only when their identities or names change.
- Omit computed display strings and identity helper objects from saved history JSON; source fields still round-trip.
- Default to five-second samples, with two- and ten-second choices. Hidden-page/minimized monitoring uses 15 seconds; restoring a minimized window can take up to that interval to resume the selected rate. Slower sampling can miss more short-lived connections. WFP filtering and broker expiry are independent of these monitor timers.

## Scope and remaining measurements

The core harness runs warmed synthetic 10,000-connection workloads, a full retained archive (10,000 events / 15,000 adapter samples), bounded worst-case analysis of 1,500 rules, parsing 100,000 domains, and lookups in 50,000 geolocation networks. It measures median/p95 elapsed time and thread allocations. It also verifies 1,000 sequential blocked DNS queries over real loopback UDP on a temporary port, without contacting a public resolver or changing system DNS. That test measures the local blocking path, not HTTPS forwarding or maximum concurrent DNS capacity. The live capture case reads the machine's changing endpoint tables; live counts are recorded and are not a fixed replay. Timing and allocation values include small harness overheads and JIT/host variability.

The process runs cover default live monitoring with no user interaction. Active WFP throughput/latency, DoH forwarding throughput, populated map rendering, full-history UI interaction, battery consumption, GPU/DWM cost, and multi-hour stability are not established by these numbers. The regression suite and earlier scoped WFP tests establish separate correctness evidence. A short memory run cannot prove the absence of leaks.

The baseline used the former two-second default; the final build uses five seconds as well as implementation optimizations. Their whole-app difference includes both changes. Core operations are compared per call; they do not assume identical refresh rates.


## Expandable monitor revision, 12 October 2026

The app now aggregates by package/executable, adds process expansion and displays multiple country flags per app. With the collector, filtering and DNS off, the tested Windows 11 host produced:

| Scenario | Duration | CPU, one core | Peak private memory | Peak working set |
| --- | ---: | ---: | ---: | ---: |
| Grouped foreground candidate | 181.71 s | 1.290% | 177.54 MiB | 241.25 MiB |
| Hidden-window candidate | 60.59 s | 0.542% | 143.66 MiB | 202.52 MiB |
| Grouped foreground, notification/path fixes | 60.62 s | 1.315% | 197.48 MiB | 260.04 MiB |
| Hidden window, notification/path fixes | 60.56 s | 0.464% | 121.55 MiB | 173.89 MiB |

All four runs passed the existing 2%-of-one-core / 200 MiB private-memory budget. The 197.48 MiB foreground run has little memory headroom. These are normal host traffic runs, not identical replays or maximum memory guarantees. Startup, current connections and GC timing affect the footprint. The older candidate and updated binaries are identified by SHA-256 in their JSON files. The final native X-button interception correction is measured separately below on the actual tray process.

[Foreground candidate](benchmarks/2026-10-12-grouped-monitor.json) · [Hidden candidate](benchmarks/2026-10-12-background-monitor.json) · [Updated foreground](benchmarks/2026-10-12-grouped-final.json) · [Updated background](benchmarks/2026-10-12-background-final.json) · [Core workloads](benchmarks/2026-10-12-grouped-core.json)

In the synthetic model workload, grouping **10,000 connections into 100 collapsed apps** took 7.369 ms median / 8.290 ms p95 (8.91 MB allocated per call). Updating **512 apps with 30 daily usage buckets each** took 2.981 ms median / 4.023 ms p95 (2.15 MB allocated). These operations run per snapshot, not per packet. The measured live host had 935 endpoint rows. The core harness also completed 1,000 local blocked DNS round trips without contacting a public resolver.

The privileged collector's UAC request returned error 1223 (canceled). Therefore these numbers **exclude the ETW collector**, and do not establish its accounting accuracy, memory/CPU under load, Internet/UDP/VPN coverage or crash cleanup. The collector has a bounded loopback diagnostic (`ZikWall.exe --test-usage`) ready for an approved elevated run. Do not substitute the synthetic ledger benchmark for the native collector measurement.


The **exact final executable**, running as the configured native tray instance after the close-path fix and read-only diagnostic persistence guard, measured **0.335% of one core**, **158.23 MiB peak private memory** and **219.09 MiB peak working set** over **60.63 seconds**. It passed both budgets and was left running. [Actual tray report](benchmarks/2026-10-12-final-native-tray.json). Reproduce after setup with `tools/benchmark-running-tray.ps1`.

Validation for this revision: 160 model/service checks, 1,500 native list churn iterations with icons/flags/alerts, rendered map fixture plus country-button invocation/details verification, background sampling, actual native WM_CLOSE hide, duplicate-launch reuse, window restore, and the current-user sign-in registration. The ETW runtime diagnostic remains the explicit exception above. Diagnostic instances now save usage only after actually collecting a usage snapshot, preventing an idle test instance from overwriting the live instance's totals.


## ZikWall namespace and project rename

The installed rebranded tray build measured **0.412% of one core**, **149.2 MiB
peak private memory** and **211.47 MiB peak working set** over **60.62 seconds**.
It passed both monitoring budgets. Collector, filtering and DNS were off; this
does not measure their enabled overhead. The report records the assembly hash.
[Rebranded tray report](benchmarks/2026-10-12-rebranded-tray.json).


## Connection context actions

The final context-menu build passed a visibility-checked, isolated hidden-window
measurement: **0.438% of one CPU core**, **121.97 MiB peak private memory**,
**174.48 MiB peak working set** over **60.63 seconds**, with collector, filtering
and DNS off. This measures background monitoring, not menu interaction or the
privileged collector. [Report](benchmarks/2026-10-12-context-actions-hidden.json).
Menus are constructed on demand and their captured snapshots released on close.

The first attempt measured the configured tray instance and reported 3.251% CPU /
215.08 MiB private. The app had a visible main window when inspected afterward;
the old harness did not check visibility during its samples. This is retained as
an [invalid tray-only measurement](benchmarks/2026-10-12-context-interrupted.json),
not discarded or interpreted as a valid idle regression. The window was left
alone and the isolated hidden instance above used instead. Both benchmark scripts
now reject visible/restored windows in background scenarios. The running-tray
script was verified to reject the visible app without changing it.


## Firewall throughput: scoped off/on comparison

On 12 October 2026 the production unelevated client and elevated broker passed
an IPv4 TCP loopback comparison. The test never loads a saved policy, changes
Windows Firewall defaults or transfers data to the Internet. Every filter is
scoped to the diagnostic executable and a loopback address. It verifies a
separate guard-port block before measuring allowed traffic, then removes rules
and verifies restored connectivity.

After warmup it runs five off/on pairs of two-second transfers per rule count,
alternating which state goes first. Every transfer uses a new connection and
checks payload bytes and totals. Off means no ZikWall filters; on includes a
matching permit, the guard block, and (for the larger case) additional synthetic
nonmatching loopback rules. Windows Firewall stays enabled in both states.

| Saved rules in test policy | Off median | On median | Median paired on/off ratio |
| --- | ---: | ---: | ---: |
| 2 | 4.302 Gbit/s | 4.919 Gbit/s | 1.12 |
| 1,000 | 3.942 Gbit/s | 4.530 Gbit/s | 1.12 |

The medians passed the 95% comparison threshold. **No consistent throughput loss
was observed in this test; this does not establish zero overhead.** Individual
paired ratios ranged from 0.81–1.51 and 0.85–2.30 respectively: scheduling and
host-load noise are substantial, so the higher filtered medians are not evidence
that filtering speeds up downloads. Median fresh-connect timings were
0.802/0.816 ms (off/on, 2 rules) and 0.842/1.029 ms (1,000 rules). Setup latency
is measured separately from payload throughput.

The transfer deliberately loads CPU and memory networking; its process CPU is
not idle monitoring overhead. This is not a NIC, Internet, VPN, IPv6, UDP, ETW
collector-on, DNS proxy or sustained multi-hour test. It cannot promise unchanged
speed with arbitrary third-party firewalls, drivers or rule distributions.

Source inspection confirms that the SDK firewall installs ordinary ALE
connection-authorization filters. It has no packet-relay proxy, traffic shaper,
QoS rate limit or per-packet user-mode firewall callback. Rule application and
expiry use the broker; allowed payloads do not pass through its IPC pipe. The
optional DNS proxy and ETW usage collector are separate features and were off.

[Raw samples, integrity checks and assembly hash](benchmarks/2026-10-12-wfp-throughput.json).
Reproduce after building with `tools/test-throughput.ps1`; Windows requests UAC
for the scoped diagnostic broker. Each invocation uses ephemeral loopback ports
and bypasses the normal UI instance. Do not accept a canceled or incomplete run
as a throughput result.


The same final assembly separately passed a 60.59-second hidden-window monitor
run at **0.387% of one core**, **119.2 MiB peak private memory** and **171.1 MiB
peak working set**. Filtering, DNS and the ETW collector were off in this resource
run; it does not measure their combined idle or load footprint. The normal visible
app was left alone while this isolated diagnostic ran. [Resource report](benchmarks/2026-10-12-throughput-build-hidden.json).

The final locked build passed all 177 model/service checks and dependency/asset
integrity checks. No compiler warnings or errors were reported. The installed
assembly matches the throughput and resource report hash.
