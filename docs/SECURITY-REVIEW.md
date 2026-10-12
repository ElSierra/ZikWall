# Source and supply-chain review

Reviewed 12 October 2026. The starting checkout was clean, at `633e75ca8afe85f13a503c1aced10528f57f24fe`, matching the public GitHub HEAD returned by `git ls-remote`. This establishes correspondence with the public source, not the trustworthiness of the maintainer, release binaries, or build infrastructure.

No obvious injected payload was identified in the reviewed application paths and build scripts. **This is not a clean bill of health.** Concrete supply-chain weaknesses were found; no evidence establishing that someone exploited them was found.

## Findings

### High: update transport relaxes certificate validation

The public `routine` revision `1acd9394395b7cee9e1ea7b985fb54e76be86b11`, `src/routine.c:14209–14214`, retries `ERROR_WINHTTP_SECURE_FAILURE` after setting `SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_WRONG_USAGE`. This weakens the identity check on the download server. An attacker able to intercept the connection with a matching-hostname certificate under an untrusted CA may bypass the intended trust boundary.

[Immutable source](https://github.com/henrypp/routine/blob/1acd9394395b7cee9e1ea7b985fb54e76be86b11/src/routine.c#L14209)

The exact matching dependency for this simplewall revision is unavailable in the public dependency repository. This finding applies to the reviewed public dependency; it is not proof that a particular released simplewall binary contains that code.

### High: downloaded updates are consumed without an authenticated artifact check in the reviewed path

In that same dependency, `src/rapp.c:2086–2148` downloads a component, then moves file updates to the target path. `src/rapp.c:2815–2835` launches a downloaded installer through `_r_sys_runasadmin`. The inspected path has no publisher-pinned signature verification, trusted signed manifest, or authenticated expected digest between download and use. A detached signature distributed separately is not equivalent to automatic verification by this path.

[Download path](https://github.com/henrypp/routine/blob/1acd9394395b7cee9e1ea7b985fb54e76be86b11/src/rapp.c#L2086) · [Install path](https://github.com/henrypp/routine/blob/1acd9394395b7cee9e1ea7b985fb54e76be86b11/src/rapp.c#L2815)

**Mitigation in this branch:** remove `APP_HAVE_UPDATES` from all six native build configurations; remove manual updater calls, enablement, and remote internal-rule registration; disable automatic-update controls; disable the release action because no ZikWall update channel is configured. Embedded blocklists remain available. This intentionally removes automatic updates until an authenticated update design exists. The old remote release manifest has been replaced with local preview version metadata.

The vulnerable dependency itself is not silently patched or vendored as compatible. The mitigation is at this application's entry points and has static regression checks. It still requires validation in a full native build once matching public dependency source is available.

### High: native build inputs are not reproducibly pinned

`.gitmodules` names `../routine` and `../builder`, and `ZikWall.vcxproj` compiles sibling `routine` source. `git ls-tree HEAD` contains no `160000` gitlink entries pinning either dependency. Build wrappers execute sibling builder scripts. A clean checkout therefore neither identifies the intended dependency contents nor builds independently.

The public routine HEAD also fails to compile against this checkout: examples include a missing `PCR_STRINGREF`, two-argument `_r_config_getboolean` versus three-argument callers, and a void `_r_tab_selectitem` versus callers expecting a result. These failures occur in the unmodified baseline with downloaded public source. A build that happens to work with someone's untracked sibling directory is not a reproducible build.

**Recorded, unresolved:** exact inspected commits and archive hashes are in `audit-inputs.json`. These are audit records, not a claim of compatibility. A matching source revision must be published or a separately reviewed compatibility migration completed before shipping the modified firewall executable.

### Medium: one CI action used a mutable tag

`github/codeql-action/upload-sarif@v3` could change without a local source change. Pinned it to `cf12ceefebab2c867b9c35d7f907e059681d37ab`, resolved from the public `v3` ref during review. Existing checkout, scorecard, and artifact upload steps were already commit-pinned. Replaced workflow-wide `read-all` with no default permissions and explicit job `contents: read`; existing security-event and OIDC permissions remain for Scorecard publishing.

### Remaining trust boundaries

- Native CppWinRT version is fixed, but legacy `packages.config` does not lock every restored artifact by hash. The downloaded package hash is recorded in the audit input file.
- Builder invokes `makensis.exe`, `gpg.exe`, and `7z.exe` through the environment. Tool provenance and PATH integrity remain a release responsibility; the builder was read, not executed.
- Application signature inspection in `src/helper.c` is separate from verifying the application's own update artifact.
- This review does not prove the absence of WFP logic bugs, unsafe memory handling, malicious historical commits, compromised signing keys, or tampering in distributed binaries.

## Original Windows 11 UI boundary (before the feature increments)

The new WinUI 3 app runs `asInvoker` and has no firewall engine bridge. Preview edits are in-memory only. File import parses user-selected XML read-only, disables DTDs and external entity resolution, and limits input size and entry count. It does not open executables named in profiles or resolve their paths. It does not evaluate effective policy, rule priority, OS applicability, `rules_config` overrides, encrypted/compressed profile containers, or signature trust.

**Feature update:** the later commits add a separate elevated Windows SDK WFP broker, optional local activity storage, HTTPS subscriptions, an explicitly started loopback DoH proxy, offline geolocation data import and on-demand executable trust inspection. The original review above does not constitute an independent audit of these new modules. See [broker boundaries and privileged validation gate](WFP-BROKER.md), [DNS/subscription boundaries](DNS-SUBSCRIPTIONS.md), [map provenance](MAP-DATA.md) and [current feature status](FEATURE-STATUS.md). The UI remains unelevated. Filtering and the DNS proxy start only through their explicit controls.

**Runtime validation, 12 October 2026:** user-approved scoped loopback tests passed for the native WFP engine and the production cross-integrity broker handshake. These demonstrate outbound IPv4 TCP block/removal, rejected policy preservation, broker-enforced expiry and session cleanup, not a complete security audit. The optional `--test-broker` diagnostic starts only a fixed self/loopback-port test and requests UAC; it never loads saved policy. Remaining adversarial and broader rule tests are listed in the broker document.

WinUI package versions and transitive content hashes are recorded in `ui/ZikWall.WinUI/packages.lock.json`; the build script restores with `--locked-mode`. `ui/NuGet.Config` clears inherited feeds and maps Microsoft/System package IDs to the NuGet.org feed. The Windows App SDK transitively brings additional Microsoft components; the app does not instantiate WebView2, AI, or ML components. Dependency pinning does not make those dependencies intrinsically trusted.

## Validation

- Public repository correspondence and the external dependency mismatch checked before UI integration.
- Source searches covered dependency/build inputs, process launch and library load sites, downloader/updater paths, encoded execution indicators, and workflow action references. The tracked tree contains no `.exe`, `.dll`, `.sys`, or `.lib` payloads.
- WinUI x64 Release build succeeded with zero warnings and zero errors.
- A fresh locked restore succeeded. NuGet's transitive vulnerability query reported no known vulnerable packages from the configured source on the review date; this is an advisory-feed result, not an integrity or malware verdict.
- Automated model/profile tests load the repository's 1,532 internal rules and reject malicious DTD, malformed XML, oversized input and invalid timestamps; verify search/state/timer semantics and unchanged source bytes.
- `tools/test-supply-chain.ps1` guards against reintroducing the removed updater entry points, mutable workflow action references, and missing WinUI package lock hashes.
- The original static review changed no firewall filters. Later user-approved scoped WFP tests are described above; they do not establish full firewall correctness. The legacy native executable is not claimed to build.

## Offline monitor assets and shell icons

The monitor increment adds no NuGet packages or runtime downloads. DB-IP country CSV and MIT flag-icons source archives were hash-pinned before bounded conversion/extraction; provenance, licenses and generated asset hashes are bundled. Static checks now verify country and flag content hashes. Country lookups are local memory-mapped binary searches, with no remote IP lookup requests. Windows shell thumbnail APIs read local executable icons without launching the target; UNC paths are excluded and caches/concurrency are bounded. Recent desktop notifications use the Windows App SDK, are opt-in, and may expose app/IP details in Notification Center. See [monitor provenance and limits](MONITORING.md). These checks do not certify the absence of a supply-chain compromise.

## Monitor/tray revision, 12 October 2026

The added usage collector uses Windows SDK ETW with no new third-party dependency or executable download. It accepts only fixed usage-start/usage commands over the existing parent-PID/start-time-validated pipe. Native DLL loading remains anchored to the broker directory. It does not install a service, change provider ACLs, capture payloads, or persist packet addresses. Path/package attribution checks process creation and exit timestamps against event time; unresolvable identities become an explicit unattributed counter. Tables, handles, trace buffers and IPC messages are bounded.

Sign-in registration is one named current-user Run value pointing to ZikWall with `--tray`, reversible in Settings. Country positions are bundled public-domain Natural Earth data, with input/output SHA-256 recorded and output checked by the build audit. The collector requires further elevated runtime coverage and load testing; source review and dependency hashes alone cannot establish correctness or absence of compromise.
