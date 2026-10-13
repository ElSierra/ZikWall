# ZikWall monitoring, tray and observed traffic

The Connections page defaults to one collapsed row per package family or executable path. Click an app to see its processes, then a process to see its sockets. Display names alone never merge unrelated programs. When Windows denies access to process identity, that process stays separate. Filters apply before grouping; group counts describe the visible subset. Usage columns cover the entire app, including local traffic, regardless of the connection filter.

The toolbar and Options flyout replace the large stacked controls. Activity receives the remaining window height. The native ListView stays virtualized, preserves unchanged rows, and disables row transitions. Icons are bounded cached pixels, loaded off the UI thread. Country summaries show up to three flags; expanded connections show their own country and IP endpoint. Details and CSV retain the full IPv6 address.

## Hide Windows system apps

**Monitor options → View → Hide Windows system apps** hides recognized Windows
components and built-in packages from Connections, Activity, map snapshots and
filtered CSV exports, and suppresses their new-connection banners and Windows
notifications. Changing it clears queued batches and the current banner; enabling
it also dismisses the registered monitor notification. Turning it off immediately
restores captured rows and allows future system alerts without replaying a backlog.

The toggle defaults off and is saved in
`%LOCALAPPDATA%\ZikWall\monitor-preferences.json`. Collection, saved history,
usage accounting and firewall policy are unaffected. Classification uses the
actual Windows system folders, Defender locations, Windows System PID 4 and
known Windows package names with Microsoft package publisher IDs. It does not
hide every Microsoft-published app: Edge and Office outside system locations,
third-party apps and ambiguous process identities remain visible. This is a
display filter, not a signature/trust check. It requires no elevation, package
enumeration, file inspection or extra polling.

`--test-system-apps` exercises the native toggle, row/count restoration, history,
map filtering and banner dismissal with in-memory fixtures. Diagnostic modes
neither load nor save the user's monitor preference.

## Tray and sign-in

Start at sign-in is a current-user Windows Run entry named **ZikWall**, pointing to the current executable with `--tray`. Toggle it in Settings. Keep the whole output folder at the registered path. Windows Task Manager can also disable startup.

Closing with X hides the app in the notification area. Open restores it; Pause / resume affects connection snapshots; Exit quits, stops collection and removes session firewall filters. The existing DNS warning still applies when explicitly exiting with the DNS proxy active. Hiding keeps those services alive. A second ordinary launch restores the existing window instead of creating another monitor. Explorer's taskbar restart recreates the tray icon. Windows may put the icon in its overflow area.

## Per-app usage

Enabling collection adds **Usage today** to Connections. The main number is
today's combined download and upload total; the smaller line shows current
download/upload rates. Hover for the separate totals. Click **Usage today** for
highest-first sorting and click again for lowest-first; **App / connection**
restores name order. Unknown totals stay last in either direction. Expanded
processes remain with their app and do not claim separate traffic totals. In
ungrouped mode, the tooltip identifies repeated figures as whole-app usage.
Pausing or losing the collector hides the column; recorded totals remain in
Details and All app usage.

Usage-only refreshes reuse the connection groups and update observable cells
in place. They do not rebuild app icons, connection rows or DNS summaries.
Native checks cover actual heading invocation, both sort directions, preserving
selection, enabling/disabling the column and visible number updates.

**Enable usage** requests Windows UAC for a session-owned collector. It does not enable filtering. The UI remains unelevated. The collector uses the installed Windows SDK ETW APIs and Microsoft-Windows-Kernel-Network metadata; it records no packet payload or ETL file and contacts no external service. It aggregates packet-size events before returning counters through the existing authenticated parent/child pipe. A separate collector process does not open a WFP session unless a policy is applied.

The first snapshot establishes rates; subsequent samples calculate upload and download per second. Daily totals are saved under `%LOCALAPPDATA%\ZikWall\daily-usage.json`, retaining today and 29 previous local calendar days. Details shows an app's retained days; Options → All app usage includes apps without current connections. A cumulative-counter session ID prevents duplicate samples and restarts from double-counting. A sample spanning midnight is assigned to the local day at its end, so boundary totals can shift by one polling interval.

These are **partial observed network bytes**, including local/loopback traffic, not ISP-billed bytes or guaranteed application payload counts. Collection gaps, denied/exited process identity, ETW losses, offloading, proxies and VPNs affect coverage and attribution. The status reports lost events, unsupported events, unattributed bytes and actual allocated trace buffers. Unknown identity never gets silently charged to the next process reusing that PID. No event coverage is claimed merely because a provider starts successfully.

Enabled usage collection and domain-cache access are remembered across restarts. Each requests Windows permission again when the app starts; cancelling leaves that feature off until it is enabled again. No permanent privileged service or elevated scheduled task is installed. Historical traffic from before collection cannot be recovered. Usage is polled independently of paused connection snapshots so an enabled collector retains a heartbeat.

Bounds: 512 app aggregates, 2,048 retained process handles, 64 KiB ETW buffers with requested 16–64 buffer count (Windows can adjust this), 2 MiB IPC limit, 15,360 daily rows and a 16 MiB load limit. New identities past the aggregate bound are reported as unattributed bytes. Unexpected trace-consumer exit is reported as collection failure.

## Live map

The map works immediately from bundled DB-IP country data and Natural Earth country positions. Select a country marker to see its apps, directions and IP addresses. Marker counts are distinct IPs, while the detail list contains connections. Positions are country label points, **not server coordinates**. Unknown/unmapped countries remain unlocated and are counted in the status. The country detail list displays up to 1,000 connections. The previous optional CSV map is replaced by this default country view; existing CSV files are not deleted.

Each changed country receives one 450 ms opacity pulse, bounded to 32 pulses per refresh and disabled when Windows animations are off. Hidden pages/windows stop their animations. Connection sampling remains five seconds by default and fifteen seconds in the background; this is a live snapshot view, not a per-packet animation.

## Validation

`tools/build-winui.ps1` builds the UI, native collector/WFP DLL and broker, runs model/service tests, and verifies pinned supply-chain inputs and bundled country/flag hashes.

`tools/test-monitor-ui.ps1 -Runs 3 -RenderAndNotify` exercises native rows and icon recycling. `ZikWall.exe --test-lifecycle` checks tray hide/restore and actual background samples. `ZikWall.exe --test-map` renders labeled offline fixtures. `ZikWall.exe --test-usage` requests UAC and tests four loopback TCP/UDP IPv4/IPv6 paths against known fixture payload sizes, reporting a coverage failure rather than accepting missing events as zero traffic. Diagnostics bypass normal single-instance reuse and do not enable autorun.

The first ETW runtime attempt on 12 October 2026 returned Windows error 1223 (UAC canceled). **Native accounting coverage and collector-under-load performance remain unverified on this machine.** Parsing/accounting tests and a successful build do not substitute for that privileged runtime test. Do not represent the feature as complete accounting until the diagnostic and additional Internet/VPN workload measurements pass.

Final setup verified real native WM_CLOSE hiding, a quiet duplicate launch, restoring the existing window from a second launch, and the current-user Run entry pointing to the final executable. The setup script deliberately leaves the app running in the tray; see `tools/configure-tray-startup.ps1`. The tray close handler intercepts native X/Alt+F4 messages because relying on AppWindow.Closing alone was insufficient in the lifecycle test.


## Right-click actions

Right-click an app, subprocess or connection in Connections (or an event in
Activity). Shift+F10 / the keyboard context-menu key opens actions for the selected
row. Right-clicking blank space does not act on an earlier selection.

- App groups: expand/collapse, block all currently observed executable paths,
  details and usage, executable inspection, file location, and copy path.
- Subprocesses: expand/collapse and block that executable, plus inspection and
  copy PID/path. Executable rules affect every instance of that file, not one PID
  or one service inside a shared host.
- Connections: block the executable plus remote IP/port, protocol and observed
  direction; copy local/remote endpoints, IP, path or PID. The rule covers all
  matching processes and local ports, not just the displayed socket.
- Manage firewall rules opens the policy page. Remove or edit a block there,
  then apply the saved policy to change active enforcement.

The block-review dialog lists the scope and offers no expiry, 5 minutes,
30 minutes or 1 hour. **Block now** saves rules at the start of the selected
profile and applies that profile, including existing saved changes shown in the
review. It requests UAC if filtering is off. Success is shown only after the
broker acknowledges the policy; a rejected/canceled elevation is reported as
saved but not confirmed active. Canceling the review itself saves nothing.
Session filters end on exit; saved unexpired rules can be applied again later.

Missing executable paths disable blocking; unavailable UDP destinations,
unknown direction, listening wildcards and unsupported scoped IPv6 disable
endpoint blocking. There is no silent fallback to an all-app or all-destination
rule. Package groups cover only observed paths, not future package executables.
A connection remaining visible is not evidence about its firewall decision.

Menus are built only on request, not per realized row or per snapshot. Their
immutable target snapshot survives list refresh and virtualization. Native tests
exercise routing, app/process expansion, command invocation after refresh,
unavailable actions and canceling the actual block-review dialog. They do not
apply rules or request UAC. Rule-scope tests cover IPv4, IPv6, inbound/outbound,
package helpers, missing identity and unsupported endpoints. This change uses
the existing broker; it does not expand the previous runtime enforcement claims.

Validated for context actions: 177 model/service checks, 500 native row updates,
8 native menu targeting/invocation checks, and block-review cancellation. The
final build completed without compiler warnings or errors.
