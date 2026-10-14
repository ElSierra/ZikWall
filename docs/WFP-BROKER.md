# ZikWall WFP broker

This is a new, small Windows SDK backend. It does not make the legacy simplewall/routine source compatible. The WinUI app runs without elevation; installed builds request isolated brokers through ZikWall Background Service. Portable development builds launch brokers with Windows UAC. See [service installation and validation](INSTALLER.md).

## Scope and lifetime

The broker uses a dynamic WFP session, its own sublayer, ALE connect/receive-accept layers for IPv4/IPv6, and transactional replacement of only its own filter IDs. It never changes Windows Firewall defaults or disables another firewall. The MSI installs the launcher service; the broker does not install itself. Closing the app, broker, pipe or idle session removes its filters. Stopping the service terminates its brokers too. This is **session filtering**, not protection across reboot or application exit.

Rules support a local executable path, remote address/CIDR, TCP/UDP/any protocol, optional remote port and inbound/outbound/both directions. Rules need an application or remote network scope. Higher list positions have higher weight in ZikWall's sublayer. Allow rules are soft permits and cannot override another provider's block. Rule expiry is enforced by the broker's half-second maintenance loop, independently of UI list refreshes.

Saved policy profiles are separate from the imported simplewall XML viewer. Network associations use Windows Network List Manager GUIDs. Switching is opt-in; multiple matching networks pause automatic selection. Pending edits also pause switching. Rules are applied only after the broker acknowledges acceptance; failed transactions retain the previous filters. Review the selected profile and click Apply after editing it.

## Privilege boundary

- A new unpredictable pipe name is used for each session; first-instance creation prevents pipe-name takeover.
- The pipe denies remote clients, grants the launching UI user's SID and SYSTEM access, and permits that user's medium-integrity UI to communicate with the elevated broker.
- The broker verifies the parent PID, creation time, sibling executable path and connecting pipe client's PID. The UI verifies the server PID against the helper returned by the authenticated service (or started by Windows elevation for a portable build).
- IPC is length-framed and limited to 2 MB. The broker independently validates every rule and supports only status/apply/stop.
- No caller-selected DLL, shell command, executable launch, registry edit or privileged file write is exposed.
- The engine DLL is loaded by an absolute path from the broker's own directory.

For distribution, install executable files under an administrator-owned directory and sign them. The development output is writable by the developer and is not a hardened deployment boundary. File path rules do not yet verify an application's signature or bind to a binary hash. Other pending features include blocked-connection prompts and default-deny modes.

## Validation and remaining release gate

The C++ engine and C# broker compile using installed Windows SDK/Visual Studio tooling, without routine or a new third-party package. Nonprivileged tests cover policy schema rejection, persistence, message framing, loopback endpoint discovery and reading actual Windows network GUIDs.

On 12 October 2026 the isolated native test passed after explicit user approval through Windows UAC (exit code 0). It verified actual loopback blocking scoped to the test executable and one TCP port, restoration after empty-policy replacement, and cleanup after closing the dynamic session. This is runtime evidence for those native operations, not yet for the full UI/broker lifecycle.

The application also provides an explicit `ZikWall.exe --test-broker` diagnostic. Run it unelevated and approve its broker's UAC prompt. It uses the production client, identity checks and IPC, with only its own executable and one ephemeral loopback port in scope. It checks rejected policy preservation, timed expiry and pipe-close cleanup, without loading saved policies. Results go to `broker-self-test.json` beside the executable; that directory must be writable for this developer test. It does not accept caller-supplied rules or output paths. This diagnostic passed all 15 checks on 12 October 2026 at 11:14 UTC, with exit code 0. It exercised the actual cross-integrity pipe handshake, scoped enforcement, rejection of an invalid replacement while keeping the active block, empty-policy restoration, independent broker expiry and pipe-disconnect cleanup.

An isolated test executable is provided at `tests/ZikWall.Engine.Tests`. In an administrator terminal in a disposable Windows environment, build the engine and run:

```powershell
.\tools\build-engine.ps1
dotnet run --project tests\ZikWall.Engine.Tests -c Release
```

It first proves a loopback connection works, then scopes a temporary block to its own test executable and one loopback TCP port, checks removal and checks cleanup after the dynamic session closes. It does not install global blocking rules. The remaining release gate includes UAC cancellation, adversarial pipe identity rejection, profile switching with active filters, forced broker-crash cleanup, stop-command cleanup and interactions through the visible UI. IPv6, inbound, UDP and multi-rule precedence still need scoped WFP runtime tests; the passing loopback tests only exercised outbound IPv4 TCP blocking.


## Startup state and throughput diagnostic

ZikWall launches in monitoring mode, including at sign-in. Saved monitoring
sessions resume through the installed service without UAC; saved firewall rules
are not applied automatically. Open **Firewall policy → Enable filtering** (or
confirm **Block now** from a connection menu) to apply rules. Portable builds
request Windows UAC approval for their helpers. Without saved rules there is no ZikWall block policy to
apply. Session filters end on app/broker exit; the sign-in tray entry does not
make them persistent. Windows Firewall configuration is unchanged.

`ZikWall.exe --test-throughput` uses the production broker with only self-scoped
loopback fixtures. The 2-rule and 1,000-rule allowed TCP comparisons passed their
median threshold, with substantial sample noise. See [throughput measurements
and limits](PERFORMANCE.md#firewall-throughput-scoped-offon-comparison).
