# Building and installing ZikWall

Run `make setup`, `build.bat` or `tools/build-setup-exe.ps1` after source changes. These build the
current WinUI app, broker and native engine, run the model/service and asset
integrity checks, and produce `artifacts/installer/setup.exe` and `setup.msi`. WiX 4.0.6 is
downloaded to the ignored `temp/wix` directory on first use. The usual .NET,
Visual Studio C++ and Windows SDK build prerequisites still apply.

`setup.exe` is a self-contained launcher for the native WinUI 3 setup interface.
It bundles the verified MSI, .NET, the Windows App SDK and the VC runtime, so it
does not download prerequisites during setup. Open it to install or upgrade;
the matching installed product offers repair and removal. The interface includes
license acceptance, an installation folder, progress, cancellation, error logs
and completion guidance for the all-users Start menu shortcut. Windows Installer
owns elevation and transactional installation/rollback through `MsiInstallProduct`
and `MsiSetExternalUIRecord`; `INSTALLUILEVEL_NONE | INSTALLUILEVEL_UACONLY`
keeps the wizard in WinUI while retaining the Windows consent prompt.

The launcher verifies its embedded archive before extracting to a unique temporary
folder and removes that folder when setup closes. Before installation, the MSI is
verified against its compiled SHA-256 and retained under
`%LOCALAPPDATA%\ZikWall\InstallerCache\<hash>` for subsequent repair. Setup logs
are retained under `%LOCALAPPDATA%\ZikWall\SetupLogs`. No app is forcibly closed,
and cancelling waits for Windows Installer to return before setup can exit.

`tools/build-setup-exe.ps1 -UseExistingMsi` packages an already verified MSI without
rebuilding the app. Every EXE build runs `setup.exe --self-test`: it extracts the
real payload, opens the MSI database, exercises native MSI progress/cancellation
records, checks the license gate and renders the WinUI screens in both themes.
This diagnostic does not install, uninstall or alter firewall rules. Results and
previews are in `artifacts/installer/setup-preview`; EXE/MSI hashes are recorded
in `setup-exe-manifest.json`. A real install/upgrade/rollback should additionally
be exercised in a disposable Windows environment before release.

The standalone MSI remains available for deployment and uses ZikWall's shield, navy artwork and Segoe UI type,
with clickable `https://isaacojo.me` links on the welcome and completion pages.
It includes the complete GPLv3 license, installation folder selection, progress,
cancellation, and repair/removal pages. Windows Installed apps also links to
the publisher website. `make assets` regenerates the deterministic installer
artwork and license page. The WiX UI extension is pinned to 4.0.6.

Each run uses a fresh staging directory, excluding debug symbols and old
diagnostic output. The MSI bundles .NET, the Windows App SDK and the x64 Visual
C++ runtime. `manifest.json` records the MSI version and SHA-256 hashes of the
package and every payload file. Build staging stays under ignored `temp/msi-*`.
The build extracts the finished MSI and checks every payload hash before
replacing the previous output. Run `tools/test-setup.ps1` to repeat that check;
add `-InstallDirectory 'C:\Program Files\ZikWall'` to verify an installed copy.

The default version is `0.<months since December 2025>.<UTC minute of month>`.
It increases across builds in different minutes, including uncommitted changes.
Builds within the same minute can replace each other. Release builds can supply
an explicit three-part MSI version, for example:

```powershell
.\tools\build-setup.ps1 -Version 1.0.0
```

After choosing an explicit release version, continue specifying increasing
release versions; a higher installed version prevents downgrades. Keep the
UpgradeCode in `installer/Package.wxs` stable across future releases.

Open `setup.exe` for the WinUI 3 interface. For direct MSI deployment, use:

```powershell
msiexec /i artifacts\installer\setup.msi /norestart
```

Installation requires elevation, places the app in `Program Files\ZikWall`,
adds `ZikWall.lnk` to the all-users Start menu Programs folder
(`%ProgramData%\Microsoft\Windows\Start Menu\Programs`) and registers ZikWall in Windows Installed apps.
Package verification checks the shortcut's folder, app component, working directory
and icon; installed-copy verification also checks that the shortcut exists.
Newer MSI builds upgrade the existing installation. Close the app through
tray Exit before upgrading. User settings and history stay in the user's
existing application-data directory. The installer does not enable filtering
or change Windows Firewall settings. Startup remains an app Settings option.
Use `/passive` for progress-only deployment or `/qn` for unattended deployment;
omit both to see the MSI wizard. `make install` opens the WinUI setup.exe interface.

Monitor choices (filters, grouping, refresh/range, adapter, alerts, history saving,
system-app visibility and usage sorting) save immediately to
`%LOCALAPPDATA%\ZikWall\monitor-preferences.json`. Theme and DNS provider fields
use `appearance-preferences.json` in the same stable directory. Existing files
from earlier releases retain their values; missing fields receive their prior
defaults. Settings that older versions never wrote must be selected once again.
Enabled usage and domain-cache access resume with new Windows permission prompts;
the local DNS proxy and firewall session still require explicit start/apply.
Installer verification rejects per-user settings components and cleanup actions.
The native `--test-preferences` diagnostic checks changes survive view recreation
before exit, including theme and DNS edits from separate panels, using isolated files.

Uninstall through Windows Installed apps. If you enabled Start at sign-in,
disable it in ZikWall Settings before uninstalling. The MSI is currently
unsigned; signing requires a publisher certificate.

For a portable development build only, use `tools/build-winui.ps1`. Running
that command does not create an MSI or update an installed copy.
