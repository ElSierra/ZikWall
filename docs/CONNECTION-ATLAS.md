# Connection atlas

The Map page uses an offline shaded-relief map that fills the available height,
an application filter on the left, and a compact country/details rail on the right.
Summary counts live in that rail instead of taking space above the map.
Country selection shows a label and incoming/outgoing connection counts.
Curved lines fan out from a **This device** hub to every country marker; the
selected country's line is highlighted. Small traveling dots accompany connection
IP changes and selections. The schematic hub sits over the Atlantic and does not
imply the device's physical location.
Teal markers show remote IP counts; the selected country is amber. Dense markers
are separated deterministically, with leader lines back to their approximate
country centers. The country list remains the keyboard-accessible alternative.

Use **+**, **−**, and **Fit** to zoom or show the whole world. Drag with the mouse
or use touch panning/pinch zoom. Selecting an offscreen country pans to it. The country details panel
stacks below the map at narrow widths. The surrounding UI follows light/dark
theme settings; the map retains its dark ocean palette.

Opening the Map page (or restoring the app from the tray, or turning **Motion**
back on) plays an entrance: the hub pulses, a dot travels west-to-east to each
country (up to 24), and each marker pops in with a halo as its dot arrives. It
replays even when the data is unchanged and finishes in under two seconds;
live captures during it update counts without restarting it. Live remote-IP
changes then spotlight one changed country per capture with a 420 ms trail and
halo, rotating through changed destinations. Country selection plays an 850 ms
trail followed by its halo. All of this is one-shot compositor keyframes with no
timers or per-frame UI work. There is no idle loop: WinUI renders composition
in-process, so any continuous motion would cost CPU every frame. **Motion**
disables it, and Windows' animation preference is respected. Leaving the page,
hiding/minimizing the app, or unloading the view stops active animations.
Markers carry a static soft glow and lines fade in from the hub; neither animates.
Traveling dots live in a dedicated compositor shape layer, and unchanged line
styles are retained between updates.

Country centers and counts do not establish server positions or network routes.
Curves summarize observed device-to-country connections, not traced routing or
individual packet movement. Unlocated IPs remain included in the
remote-IP total and reduce the location-coverage percentage. Data stays offline.

## Performance implementation

The view retains a private copy of its last rendered immutable connection
records, so unchanged snapshots skip aggregation, marker writes and detail-list
sorting. A private array also detects changes when callers mutate/reuse their
input list. Changed snapshots reuse summaries for unchanged country groups.
Remote-IP sets replace sorted, concatenated socket signatures, so reconnecting
to an existing destination does not restart animation. Marker
buttons and country-list containers remain stable between updates; changing a
country count updates its bindings in place. Detail lists only rebind when their
contents change. Application rows and their decoded icons are retained too;
IP count changes update a binding rather than replacing the list. Applications
sort by name so activity changes do not move the row under the pointer. State-only
changes skip application aggregation. Hidden views retain the latest input but do no map work.

## Missing location data regression

The installed build inspected on 15 October contained country centroids and
provenance JSON, but neither `ipv4.zwc` nor `ipv6.zwc`. The network-owner tables
were missing as well. `--country-data-only` against that installation reproduced
the empty country code for `8.8.8.8`; the same test passed against repository data.
The old native map test supplied country codes in its fixture and missed this.

Release builds now validate the required output files. `test-location-payload.ps1`
verifies their recorded hashes; MSI validation requires these assets in its
manifest and verifies extracted hashes. The native map test additionally runs
real enrichment against the packaged data before testing synthetic load.
Unavailable data produces a repair message and retains an endpoint list, rather
than indefinitely saying it is waiting for located connections.

## Reproduce

```powershell
dotnet build ui/ZikWall.WinUI/ZikWall.WinUI.csproj -p:Platform=x64 -p:OutDir=F:\Projects\simplewall\artifacts\atlas\ -c Release --no-restore
.\tools\build-engine.ps1 -OutputDirectory artifacts/atlas
.\artifacts\atlas\ZikWall.exe --test-geo
.\tools\benchmark-app.ps1 -ExecutablePath artifacts/atlas/ZikWall.exe -Map -DurationSeconds 60 -EnforceBudgets -OutputPath artifacts/benchmarks/atlas.json
```

`--test-geo` uses isolated fixture data, pauses live monitoring, writes native
map renders and `geo-selftest.json`, and exits nonzero on failure. It tests the
1,000-row detail cap, stable markers, empty state, hidden animation cleanup,
motion-off behavior, native zoom/reset, and narrow layout. It measures 60
refreshes of 10,000 connections per scenario, then 90 animated updates/selections.
The unchanged-refresh allocation limit is 200 KB including the newly allocated
80 KB input array. Timing measurements are observations, not universal limits.

The model/service suite and existing native monitor UI diagnostics cover the
rest of the application. Component benchmarks cover grouping, usage, logs/read
markers, history persistence, sync planning, endpoint capture, country lookup,
rule analysis and local DNS processing. Live resource samples use isolated
benchmark mode, with filtering, privileged collectors and live DNS proxy off;
they do not measure firewall-driver throughput or long-running leak behavior.

## Measurements — 15 October 2026

### Shaded-relief redesign (afternoon run)

The native map regression passed packaged lookup, missing-data messaging,
application filtering, stable app/country rows, bounded markers/details,
pan/zoom/Fit, hidden-animation cleanup and narrow layout. The release build had
zero warnings/errors. The full model/service suite passed.

| Native map workload | Median | p95 | Allocation / refresh |
| --- | ---: | ---: | ---: |
| 10,000 unchanged endpoints | 0.131 ms | 0.296 ms | 80,108 B |
| One endpoint state changes | 4.288 ms | 17.342 ms | 3,091,927 B |
| Selected country, unchanged | 0.040 ms | 0.054 ms | 82,706 B |
| Hidden map | 0.0001 ms | 0.0002 ms | 80,042 B |

Allocations include the caller's 80 KB input array. The 90-selection/update
stress run recorded a 28.71 ms p95 render interval and 4 intervals over 50 ms
among 404. These are WinUI render callbacks, not GPU-present timings.

The final live foreground map used **3.449% of one core** (0.123% of the
28-thread machine) and **163.68 MiB peak private memory** over 60.71 seconds
after a 15-second warmup. It passed the 200 MiB memory budget but **missed the
strict 2% one-core CPU budget**. Do not describe this run as passing both.
An earlier version rebuilt application lists on count changes; retaining rows
reduced measured private-memory growth from 13.26 MiB to 7.47 MiB. The initial
motion-off probe still exceeded CPU budget, so animation alone did not explain
the cost. A 30.42-second comparison of the previous atlas on the same host also
exceeded the budget (5.496% one core); live traffic and scheduling differ, so
these runs do not establish a controlled CPU speedup or a universal limit.

Evidence: [native tests](benchmarks/2026-10-15-relief-native.json),
[final foreground](benchmarks/2026-10-15-relief-map-final.json),
[initial foreground](benchmarks/2026-10-15-relief-map.json),
[motion-off probe](benchmarks/2026-10-15-relief-static-probe.json),
[previous-map comparison](benchmarks/2026-10-15-relief-old-map-comparison.json).

Background mode passed both budgets: **1.030% of one core**, **126.36 MiB peak
private memory**, 60.70 seconds after 15 seconds of warmup. See
[background sample](benchmarks/2026-10-15-relief-background.json).
The existing native UI suite also passed, including 500 connection-list updates,
context menus, options, settings persistence, actual Windows tray registration,
policy layouts, activity, logs/history paging and the rule editor. See
[native suite output](benchmarks/2026-10-15-relief-native-ui.txt).

Installer `0.10.21055` passed extraction and all 808 payload hashes, including
the country/network tables and relief texture. Its application assembly hash
matches the benchmarked build:
`EA15410B8A4A693118AEA36CEFBE7F4F8FCAFD3BD5D2F73ACC85E79A252CE5C6`.
See [package checks](benchmarks/2026-10-15-relief-package.txt).
The matching `artifacts/installer/setup.exe` passed its 35 extraction, integrity,
and native installer-screen checks. Building/testing the installer does not
update the running installation; run this installer to apply the repair.

### Earlier vector-atlas measurements

Release build, same host, 10,000-endpoint native fixture. Allocations include
the caller's fresh 80 KB input array. The baseline and final map use the same
60-refresh measurement loop; JIT, native layout and host scheduling influence
timings.

| Map refresh | Before median | After median | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Unchanged snapshot | 14.35 ms | 0.302 ms | 5.96 MB | 0.080 MB |
| One endpoint state changes | 7.87 ms | 7.82 ms | 5.81 MB | 3.09 MB |
| Selected country, unchanged snapshot | 7.86 ms | 0.172 ms | 5.92 MB | 0.081 MB |
| Hidden view | 0.0001 ms | 0.0001 ms | 0.080 MB | 0.080 MB |

The 90-update/selection animation stress test recorded a 37.56 ms p95 render
interval, seven intervals above 50 ms out of 344, and 4.23 seconds of process CPU during
the stress sequence. This intentionally updates much faster than the normal
five-second monitor interval. Render-event intervals are a responsiveness
indicator, not GPU presentation timing or a universal frame-rate guarantee.
The richer changed-frame path has a similar median to the original flat map in
this fixture while allocating about 47% less. The unchanged path allocates 98.7%
less. Rapidly switching country detail lists is still the expensive stress case;
this test does not establish 60-fps interaction under that load. Timings varied
between runs; raw intermediate measurements are retained.

The full model/service suite and existing native UI regression script passed.
Native checks include 500 list updates, context-menu routing, tool-window
layout/lifecycle, usage sorting, preference persistence, system-app visibility,
policy layout, activity rankings, log navigation/read state/paging and rule editing.
The build completed without warnings, and supply-chain checks passed.

Raw evidence: [map before](benchmarks/2026-10-15-atlas-before.json),
[map after](benchmarks/2026-10-15-atlas-after.json),
[native UI checks](benchmarks/2026-10-15-atlas-native-ui.txt),
[grouping](benchmarks/2026-10-15-atlas-activity.json),
[usage](benchmarks/2026-10-15-atlas-usage.json),
[logs](benchmarks/2026-10-15-atlas-logs.json),
[history persistence](benchmarks/2026-10-15-atlas-history.json),
[sync planning](benchmarks/2026-10-15-atlas-sync.json).
Additional component evidence: [full core suite](benchmarks/2026-10-15-atlas-core.json)
and [passive DNS](benchmarks/2026-10-15-atlas-passive-dns.json).

## Live app resource checks

All three final-build scenarios passed the existing 2% one-core CPU and 200 MiB
private-memory budgets. Each used a 15-second warmup and approximately 60 seconds
of measurement, with native window visibility verified.

| Scenario | CPU, one core | Peak private memory |
| --- | ---: | ---: |
| Connections | 1.860% | 159.94 MiB |
| Atlas with curved links and activity trails | 1.987% | 138.96 MiB |
| Hidden/background | 0.413% | 113.87 MiB |

The final atlas, including curved links and traveling dots, measured **1.987% of
one CPU core** (0.071% of the 28-thread machine) and **138.96 MiB peak private
memory** over 60.56 seconds after a 15-second warmup. It passed the existing 2%
one-core / 200 MiB limits. Initial atlas measurements exceeded the CPU limit;
country-row reuse, simple ratio bars, short compositor effects, retained line
styles and IP-based animation triggers reduced that overhead. Socket reconnects
to an unchanged destination are covered by a native regression check.

Final assembly SHA-256:
`738557F30FEF7D85DA1F9959658C58517BE82549E455920EA699B4E6961B98B2`.
The sample ran without concurrent builds or component benchmarks. Host traffic
and load vary, and the short sample leaves little CPU headroom under the strict
one-core limit; it is not a long-duration resource guarantee.

Evidence: [final atlas](benchmarks/2026-10-15-atlas-map.json),
[final Connections](benchmarks/2026-10-15-atlas-foreground.json),
[final background](benchmarks/2026-10-15-atlas-background.json),
[initial atlas](benchmarks/2026-10-15-atlas-map-initial.json),
[static probe](benchmarks/2026-10-15-atlas-static-probe.json),
[reused rows](benchmarks/2026-10-15-atlas-reused-rows.json),
[socket-churn sample](benchmarks/2026-10-15-atlas-links-port-churn.json).
