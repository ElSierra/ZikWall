# ZikWall source identity and history

Updated 13 October 2026.

Active namespaces, project paths, native product strings, localized messages,
resource filenames and build references now use ZikWall. Both native main icons
use the supplied blue shield with orange/red flame. The WinUI tray uses the
separate white shield/flame supplied for it. Exact source PNGs are retained in
`tools/branding/ZikWall-source.png` and `ZikWall-tray-source.png`;
`tools/generate-brand-assets.py` preserves transparency and generates nine Windows
icon sizes. The app image also appears in the taskbar, titlebar and installer.
The working WinUI build is `ui/ZikWall.WinUI/ZikWall.WinUI.csproj`.
The legacy solution is `ZikWall.sln`; its public dependency compatibility blocker
remains unresolved, so it is not the supported build entry point.

Original copyright notices and GPLv3 remain. Historical upstream documentation
is under `docs/upstream`; source provenance and audit evidence are preserved in
[UPSTREAM-NOTICES.md](UPSTREAM-NOTICES.md) and [SECURITY-REVIEW.md](SECURITY-REVIEW.md).
The shipped WinUI output includes the GPL and upstream notices.
No upstream download is represented as a ZikWall release. The obsolete updater
manifest and release action are disabled; the unpinned legacy rule-regeneration
wrapper stops with an explanation instead of invoking an unknown renamed tool.

The requested standalone branch is `codex/zikwall`, with one initial snapshot
commit and no inherited parent commits. Original history remains recoverable
from the previous local branch and the verified local bundle
`temp/zikwall-before-history-rewrite.bundle`. No remote history is changed.
The checkout directory and its configured startup executable path are unchanged.

The inherited `origin` remote and its remote-tracking refs have been removed,
along with the old `master` branch's upstream tracking configuration. No Git
remote is configured. The old local branch and recovery bundle remain local
archives; the active branch has no inherited parent commits.
Obsolete submodule declarations and upstream funding configuration have been
removed. `build.bat` now builds the supported WinUI app and MSI installer instead
of invoking the external builder checkout. The archived legacy solution still refers to
the sibling routine sources and remains unsupported. Original license,
copyright, source links and audit records are retained as provenance.

Validation: the rebranded WinUI app, broker and Windows SDK engine built; all
160 model/service checks and the dependency/asset integrity checks passed.
Native UI checks passed 1,500 list updates, icon/flag decoding, Windows
notification submission and XAML rendering. Tray close-to-hide, single-instance
restore and sign-in registration passed with the installed build.
Per-app ETW usage accuracy and collector-on resource use remain unverified;
see [monitoring limits](MONITOR-AND-USAGE.md).
