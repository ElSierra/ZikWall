# ZikWall

<img src="ui/ZikWall.WinUI/Assets/ZikWall.png" width="88" alt="ZikWall Z shield logo" />

A native Windows 11 connection monitor and firewall preview built with WinUI 3,
Windows App SDK and the Windows SDK.

- Expand apps to see their processes and connections, with app icons, IP addresses,
  direction, country flags, recent activity and optional Windows notifications.
- Offline country map, local history and optional per-app observed traffic totals.
- Tray operation and current-user sign-in startup, with slower background sampling.
- Right-click app/process/connection actions, reviewed blocks with optional expiry,
  executable inspection and copy actions.
- Session firewall policies through an elevated WFP broker, plus DNS tools.

Build on Windows with .NET 8+, Visual Studio C++ tools and the Windows SDK:

```powershell
.\tools\build-winui.ps1
```

Run `artifacts\ZikWall\ZikWall.exe`; keep the output folder together.
See [build and usage](ui/README.md), [feature status](docs/FEATURE-STATUS.md),
[performance measurements](docs/PERFORMANCE.md), and [security review](docs/SECURITY-REVIEW.md).

This is a preview. Full Little Snitch parity is not claimed. Per-app usage needs
UAC approval each session and native runtime accuracy is not yet verified.
The app starts in monitoring mode. Enable filtering in Firewall policy and
approve Windows elevation to apply rules. Session filtering ends when the
app/broker closes; Windows Firewall settings are unchanged.

Licensed under [GPLv3](LICENSE). [Upstream attribution and changes](docs/UPSTREAM-NOTICES.md)
are preserved. This repository contains both the new WinUI application and the
legacy native source; use the PowerShell build above for the working application.
