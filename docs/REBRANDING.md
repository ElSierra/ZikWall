# ZikWall source identity and history

Updated 12 October 2026.

Active namespaces, project paths, native product strings, localized messages,
resource filenames and build references now use ZikWall. Both native main icons
use the Z shield. The working WinUI build is `ui/ZikWall.WinUI/ZikWall.WinUI.csproj`.
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

Validation: the rebranded WinUI app, broker and Windows SDK engine built; all
160 model/service checks and the dependency/asset integrity checks passed.
Native UI checks passed 1,500 list updates, icon/flag decoding, Windows
notification submission and XAML rendering. Tray close-to-hide, single-instance
restore and sign-in registration passed with the installed build.
Per-app ETW usage accuracy and collector-on resource use remain unverified;
see [monitoring limits](MONITOR-AND-USAGE.md).
