# ZikWall performance and resource budgets

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
