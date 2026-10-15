# Two-way Windows Firewall sync

Open **Firewall policy → Sync Windows Firewall…**. Review the selected profile's
outgoing changes, select compatible Windows rules to import, then click **Sync now**.
Sync is off until this initial review. One saved profile can be linked at a time;
every rule in that profile participates. Unselected Windows rules are untouched.
Existing Windows rules are imported without rewriting them.

After linking, additions in the ZikWall profile, edits, enabled/disabled state,
and deletions propagate between the applications. New, unrelated Windows rules
require explicit selection in the import list. Rule order and ZikWall groups
remain local metadata. Imported Windows descriptions and groups are preserved.

Choose **Sync this profile every 30 seconds while ZikWall is open** for automatic
reconciliation. Otherwise use **Sync Windows Firewall… → Sync now** each time.
Automatic sync uses the linked profile even when another profile is selected.
It pauses changes when review is needed and never chooses conflict winners.
Installed builds use the authenticated background-service broker; portable
builds request UAC for Windows writes.

## Enforcement and compatibility

Windows rules persist across app exit and reboot. They apply on Public, Private,
and Domain networks regardless of ZikWall profile selection or network switching.
Windows Firewall must be enabled for enforcement. Sync does not enable/disable
Windows Firewall, change its defaults, or override Group Policy. Existing Windows
blocks can override an allow.

Dynamic WFP filtering remains a separate session. Disabling that session does not
remove Windows rules. When sync updates a currently applied profile, **Apply saved
policy** updates its separate WFP filters. **Disconnect sync** forgets links but
retains rules on both sides, including completed changes from an interrupted sync.
Manage retained native rules through Windows Firewall.

Supported conditions are a local executable and/or one remote IP/CIDR, TCP/UDP/any
protocol, one remote port or any, inbound/outbound/both, allow/block, and
enabled/disabled. Both-direction rules map to two native rules. Deleting one in
Windows narrows the local rule to the remaining direction. Differing conditions
between the pair require review.

The preview lists unsupported Windows rules and reasons. Service, package, user,
machine, IPsec, edge-traversal, local-address/port, interface, specific Windows
profile, port-list/range, and unrepresentable network conditions are not stripped
or broadened. Temporary ZikWall rules cannot sync because native rules would
outlive their expiry. Overlapping enabled allow/block pairs in the linked profile
are rejected because ZikWall list order and native block precedence differ.
Use a profile containing compatible rules, or adjust offending rules before
linking. Limits: 1,000 linked rules and 1,000 native changes per operation, plus
the normal policy-file and IPC byte limits.

## Conflicts and recovery

Persisted baseline snapshots distinguish edits from deletions. Changes on one
side copy to the other; changes on both sides pause unless both versions agree.
The preview offers **Keep ZikWall version** or **Keep Windows Firewall version**
for all conflicts in that preview. Unsupported conditions and duplicate native
names remain blocked regardless of the choice.

Exported rules carry a description marker for their profile, rule, and direction,
allowing Windows renames to be imported. Existing Windows imports are identified
by their original native name: renaming one is observed as a deletion, and the
renamed rule must be selected for import again. Do not copy exported identity
markers to other rules. Removing both marker and original name breaks tracking.

Native COM operations are not transactional. Before writes, `policy.json`
atomically records a recovery journal containing the intended local result and
native before/after snapshots. The broker validates the batch, checks native
snapshots before each write, and verifies the result. Failure retains the journal,
locks policy editing, and offers **Resume sync**. Completed changes are idempotent;
a missing rule from an interrupted remove/add is repaired only during explicit
journal resume. Further external edits are rejected. Windows offers no atomic
compare-and-swap for rules, so a narrow race with other writers remains between
comparison and mutation.

## Validation

The normal model suite covers merge, conflicts, deletion, direction mapping,
compatibility, persistence, IPC, stale previews, duplicate names, and interrupted
writes through an in-memory adapter.

```powershell
dotnet run --project tests/WinUI.Models.Tests -c Release -- --windows-sync-only
dotnet run --project tests/WinUI.Models.Tests -c Release -- --windows-sync-read
```

The second diagnostic only enumerates the machine's rules. From an administrator
terminal, `--windows-sync-native` exercises creation, editing, conflict detection,
repair, deletion, and cleanup using disabled rules scoped to the diagnostic
executable. It covers TCP, UDP, any protocol, IPv4, IPv6, and both directions.
The report is `windows-sync-native.txt` beside the diagnostic executable. This
does not validate effective traffic enforcement or managed-domain policy behavior.

On 14 October 2026, all 46 sync-specific checks and the full model/service suite
passed. Native read-only enumeration inspected 1,003 rules (88 compatible), and
`ZikWall.exe --test-windows-sync-ui` passed preview/cancellation checks using an
isolated policy. The native write diagnostic was not executed because its Windows
elevation prompt was cancelled; real COM write/readback validation remains open.

The performance follow-up passed 53 sync checks, the full model/service suite,
native selective-read parity, and the UI preview/cancellation diagnostic. The
1,000-rule mixed planner dropped from 142.30 MB to 4.41 MB allocated per pass;
automatic native polling and completed retries were also measured and optimized.
See [benchmark results, CPU/memory scope, and reproduction](PERFORMANCE.md#windows-firewall-sync-14-october-2026).
