# Developer commands

Run `make help` from the repository root. The Makefile uses GNU Make on Windows
and explicitly selects 64-bit Windows PowerShell, including when Make itself is
32-bit. It does not need Bash, WSL or Unix command-line utilities. Install GNU
Make if needed, or call the same task dispatcher directly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/tasks.ps1 -Task help
```

Use `make doctor` to inspect the .NET SDK, Visual Studio C++ build tools and
installed app location. WiX and its UI extension are pinned to 4.0.6 and restored
locally when building an installer. Do not commit `.wix`, `temp` or `artifacts`.

| Task | Command |
| --- | --- |
| Build portable app, broker and engine; run core checks | `make build` |
| Build and launch for development | `make dev` |
| Launch existing dev / installed build | `make run` / `make run-installed` |
| Stop only the workspace dev app | `make stop` |
| Restore packages / rebuild engine and broker | `make restore` / `make engine` |
| Create and verify WinUI setup.exe and MSI | `make setup` (aliases: `make all`, `make package`) |
| Explicit release version | `make setup VERSION=1.0.0` |
| Open existing WinUI setup / installed product removal wizard | `make install` / `make uninstall` |
| Model and service tests / static checks / both | `make test` / `make lint` / `make check` |
| Native UI checks | `make test-ui` |
| Read-only DNS cache/provider and helper diagnostic (UAC) | `make test-domains` |
| Verify MSI contents / compare installed copy | `make verify-msi` / `make verify-installed` |
| Tray lifecycle / offline map diagnostic | `make test-lifecycle` / `make test-map` |
| Elevated WFP / broker / throughput / ETW tests | `make test-engine` / `make test-broker` / `make test-throughput` / `make test-usage` |
| Model/service benchmarks | `make benchmark` |
| Foreground / background app benchmarks | `make benchmark-ui SECONDS=60` / `make benchmark-background SECONDS=60` |
| Measure configured development tray instance | `make benchmark-tray SECONDS=60` |
| Register dev startup and exercise tray behavior | `make startup` |
| Regenerate installer artwork and license RTF | `make assets` |
| Regenerate app logos and icons (Python + Pillow) | `make brand-assets` |
| Rebuild world map from the pinned local GeoJSON | `make world-map` |
| Rebuild country positions / lookup tables / flags | `make country-points INPUT=path` / `make country-data INPUT=path` / `make flags INPUT=path` |
| Remove dev and compiler output | `make clean` |
| Remove MSI output and temporary MSI staging | `make clean-msi` |

Exit the installed app from its tray menu before starting a development session
or app benchmark. The dev commands refuse to silently reuse the installed app.
`make stop` stops only the dev executable, so session-owned dev filters end too.
`make startup` deliberately changes sign-in startup to the dev path; normal
builds and development launches do not change startup.

Diagnostics require an existing dev build. Elevated tests are separate targets,
request Windows UAC and can create temporary loopback-scoped rules or collect
ETW usage. These actions are not part of `make check`. Native engine tests should run in a disposable
Windows test environment. Tray benchmarks require `make startup` first and
accept 15–600 seconds; the other app benchmarks accept 15–3600 seconds.

`make install` uses the last successfully verified WinUI setup.exe and checks its
hash against the matching MSI. Run `make setup` after source changes, then
`make install` to open its wizard. `make verify-installed`
compares against that MSI, so it intentionally fails when the installed version
is older. Setup does not automatically stop or reinstall a running app.

Cleanup resolves every directory against the repository and refuses junctions
or symbolic links. `make clean` keeps installers, installed Program Files, user
settings, benchmark reports and recovery bundles. `make clean-msi` also keeps
the Git recovery bundle and cached WiX tools. Tasks run serially even with `-j`
because native and managed builds share output directories.
