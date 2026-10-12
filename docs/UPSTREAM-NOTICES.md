# Upstream attribution and modification notice

ZikWall is a modified work based on [simplewall](https://github.com/henrypp/simplewall),
Copyright (c) 2016–2026 Henry++. The reviewed starting revision was
`633e75ca8afe85f13a503c1aced10528f57f24fe`. It is licensed under the
GNU General Public License version 3; see [LICENSE](../LICENSE).
Original copyright notices remain in the native sources. ZikWall is not an
upstream release or endorsed by the upstream author.

Modified 12 October 2026: the Windows 11 WinUI application, Windows SDK session
broker, monitoring, grouping, map, tray/startup integration, tests and branding
are ZikWall changes. The legacy native code also contains update hardening and
branding changes. Its public dependency compatibility blocker remains documented
in [the security review](SECURITY-REVIEW.md).

The standalone ZikWall Git history is a snapshot, not a claim that the upstream
code was authored for ZikWall. Historical upstream changelogs and documentation
are retained in `docs/upstream`; they describe upstream behavior, not this app.

## Native source references

These original source/issue links were moved out of active code during renaming:

- `src/main.c`: https://github.com/henrypp/simplewall/issues/1698#issuecomment-4532518363
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.2.3.12/src/main.cpp#L8273
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.2.3.13/src/main.cpp#L8354
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.2.4.6/src/main.cpp#L8828
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.3.0.5/src/wfp.cpp#L109
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.2.4.6/src/main.cpp#L8833
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.3.0.5/src/wfp.cpp#L114
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.3.1.1/src/wfp.cpp#L150
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.2.4.6/src/main.cpp#L8815
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.3.0.5/src/wfp.cpp#L96
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.2.4.6/src/main.cpp#L8820
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.3.0.5/src/wfp.cpp#L101
- `src/security.c`: https://github.com/henrypp/simplewall/blob/v.3.1.1/src/wfp.cpp#L137
